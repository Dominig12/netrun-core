using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Scheduling;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>
/// FIFO-подключение единоличного владельца bot brain/goals (BotEngine) к общей очереди ядра.
/// DM публикует восприятие (PublishBotFact) и приказы, ядро думает в Pump и выдаёт native-решения;
/// HP/residence здесь не меняются — ими владеет EntityWorld и Combat.
/// </summary>
public sealed partial class NetworkRuntime
{
    /// <summary>Владелец brain всех ботов binding; восприятие поставляет DM, а не Core-перерасчёт.</summary>
    private BotEngine? _bots;
    /// <summary>Непереданные read-model и native-решения ботов; тело команды сюда не попадает.</summary>
    private readonly Queue<TransportEvent> _botEvents = [];
    /// <summary>Монотонный серийник корреляции выданных native-решений, не зависит от brain.</summary>
    private ulong _botDecisionSerial;

    /// <summary>Непрочитанные события ботов (решения и подтверждения команд).</summary>
    public int BotEventCount => _botEvents.Count;
    /// <summary>Число действующих brain для диагностики; DM хранит лишь read models.</summary>
    public int BotCount => _bots?.Count ?? 0;
    /// <summary>Общий предел очереди событий применяется и к лейну ботов.</summary>
    private int BotOutputLimit => _options.Tasks?.MaximumEvents ?? int.MaxValue;

    /// <summary>Создаёт пустой brain-owner штатными profile/program; вызывается из InitializeEntities.</summary>
    private void InitializeBots() => _bots = new BotEngine();

    /// <summary>Закрывает binding без повторного выпуска staged решений при reconnect.</summary>
    private void StopBots()
    {
        _bots?.Dispose();
        _bots = null;
        _botEvents.Clear();
    }

    /// <summary>Закрытый allowlist команд бота направляется в общую FIFO.</summary>
    private static bool IsBotCommand(RuntimeCommand command) =>
        command is PublishBotFact or RemoveBot or SubmitBotOrder or RememberBotAttacker or WakeBot or FinishBotEffect;

    /// <summary>Только admission/fingerprint; мышление и выдачу эффектов выполняет последующий Pump.</summary>
    private TransportAdmission EnqueueBot(RuntimeCommand command)
    {
        var key = BotCommandKey(command);
        if (State != MirrorSyncState.Ready) return new(AdmissionStatus.Rejected, "world_unavailable", key);
        var fingerprint = BotCommandFingerprint(command);
        return fingerprint is null ? new(AdmissionStatus.Rejected, "bot_command_invalid", key) :
            _tasks!.Enqueue(key, new EntityWork(command), fingerprint);
    }

    /// <summary>Одна FIFO задача применяет команду к brain-owner и публикует read-model после обработки.</summary>
    private CoreTaskStep ExecuteBotTask(EntityWork work, int budget)
    {
        if (budget <= 0) return new(0, false);
        if (_bots is null) return new(1, true, "bot_unavailable", false);
        if (_botEvents.Count >= BotOutputLimit) return new(0, false);
        var (bot, accepted, code) = ApplyBotCommand(work.Command);
        _botEvents.Enqueue(new BotCommandCompleted(_epochs.Binding, BotCommandKey(work.Command), bot, accepted, code, _bots.Query(bot)));
        return new(1, true, code, accepted);
    }

    /// <summary>Применяет ровно одну команду к brain-owner; world/HP здесь не меняются.</summary>
    private (BotKey Bot, bool Accepted, string Code) ApplyBotCommand(RuntimeCommand command) => command switch
    {
        PublishBotFact publish => (publish.Fact.Key, _bots!.Upsert(publish.Fact), "bot_fact"),
        SubmitBotOrder order => (order.Bot, _bots!.SetOrder(order.Bot, order.Order, SimulationTimeDs), "bot_order"),
        RemoveBot remove => (remove.Bot, _bots!.Remove(remove.Bot), "bot_removed"),
        WakeBot wake => (wake.Bot, _bots!.Wake(wake.Bot), "bot_wake"),
        RememberBotAttacker attacker => (attacker.Bot, _bots!.RememberAttacker(attacker.Bot, attacker.Attacker), "bot_attacker"),
        FinishBotEffect finish => (finish.Result.Bot, _bots!.ApplyResult(finish.Result), "bot_effect_result"),
        _ => throw new InvalidOperationException("Invalid bot command.")
    };

    /// <summary>Доля brain-owner в общем entity lane: сначала выдача решений, затем мышление, затем снова выдача.</summary>
    private int AdvanceBots(int now, int budget)
    {
        if (_bots is null || budget <= 0) return 0;
        var used = DrainBotEffects(budget);
        if (used < budget) used += _bots.Advance(now, budget - used);
        if (used < budget) used += DrainBotEffects(budget - used);
        return used;
    }

    /// <summary>Передаёт каждое решение brain как native decision; исполнение и world-эффект остаются в DM.</summary>
    private int DrainBotEffects(int budget)
    {
        var used = 0;
        while (_bots is not null && used < budget && _botEvents.Count < BotOutputLimit && _bots.TryDequeueEffect(out var effect))
        {
            _botEvents.Enqueue(new BotNativeDecision(_epochs.Binding, new RequestKey("bot.effects", ++_botDecisionSerial), effect));
            used++;
        }
        return used;
    }

    /// <summary>Output dequeue не продвигает часы и ничего не исполняет в native мире.</summary>
    private bool TryBotEvent(out TransportEvent? output) => _botEvents.TryDequeue(out output);

    /// <summary>Исходный producer:serial команды бота для ACK и дедупликации.</summary>
    private static RequestKey BotCommandKey(RuntimeCommand command) => command switch
    {
        PublishBotFact publish => publish.Key,
        SubmitBotOrder order => order.Key,
        RemoveBot remove => remove.Key,
        WakeBot wake => wake.Key,
        RememberBotAttacker attacker => attacker.Key,
        FinishBotEffect finish => finish.Key,
        _ => throw new InvalidOperationException("Invalid bot command.")
    };

    /// <summary>
    /// Полный deterministic fingerprint команды бота для runtime replay и FIFO producer fences.
    /// PublishBotFact намеренно ключуется на DM-назначенную Revision (BotEngine.Upsert уже отвергает
    /// нестрого возрастающую Revision), а не на повторную сериализацию всего BotContext: Revision —
    /// авторитетная версия содержимого факта по контракту DM. Остальные команды кодируются полностью.
    /// </summary>
    internal static byte[]? BotCommandFingerprint(RuntimeCommand command)
    {
        if (!BotCommandRules.IsValid(command)) return null;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            switch (command)
            {
                case PublishBotFact publish:
                    writer.Write((byte)1); Key(writer, publish.Key); Bot(writer, publish.Fact.Key);
                    writer.Write(publish.Fact.Revision); Actor(writer, publish.Fact.Actor.Actor);
                    writer.Write(publish.Fact.Actor.Revision); writer.Write(publish.Fact.ProgramId);
                    break;
                case SubmitBotOrder order:
                    writer.Write((byte)2); Key(writer, order.Key); Bot(writer, order.Bot);
                    writer.Write(order.Order.Kind); writer.Write(order.Order.Target is not null);
                    if (order.Order.Target is { } target) Node(writer, target);
                    Optional(writer, order.Order.CommandId);
                    break;
                case RemoveBot remove:
                    writer.Write((byte)3); Key(writer, remove.Key); Bot(writer, remove.Bot); break;
                case WakeBot wake:
                    writer.Write((byte)4); Key(writer, wake.Key); Bot(writer, wake.Bot); break;
                case RememberBotAttacker attacker:
                    writer.Write((byte)5); Key(writer, attacker.Key); Bot(writer, attacker.Bot); Actor(writer, attacker.Attacker); break;
                case FinishBotEffect finish:
                    writer.Write((byte)6); Key(writer, finish.Key); Bot(writer, finish.Result.Bot); Actor(writer, finish.Result.Actor);
                    writer.Write(finish.Result.ActionSerial); writer.Write(finish.Result.ExpectedRevision);
                    writer.Write(finish.Result.Phase); Optional(writer, finish.Result.Response);
                    break;
                default: return null;
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));

        static void Key(BinaryWriter writer, RequestKey key) { writer.Write(key.ProducerId); writer.Write(key.Sequence); }
        static void Bot(BinaryWriter writer, BotKey bot) { writer.Write(bot.Id); writer.Write(bot.Generation); }
        static void Actor(BinaryWriter writer, ActorKey actor) { writer.Write(actor.Id); writer.Write(actor.Generation); }
        static void Node(BinaryWriter writer, NodeKey node) { writer.Write(node.NodeId); writer.Write(node.Generation); }
        static void Optional(BinaryWriter writer, string? text) { writer.Write(text is not null); if (text is not null) writer.Write(text); }
    }
}
