using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Scheduling;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>
/// FIFO-подключение единоличного владельца bot brain/goals (BotEngine) к общей очереди ядра.
/// DM шлёт сырые факты (ConfigureBot) и приказы; ядро строит восприятие, думает в Pump и выдаёт решения.
/// Движение исполняет владелец сущностей Core через общий MoveEntity (EntityWorld остаётся в синхроне);
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
    /// <summary>Монотонная revision вычисленного Core восприятия; DM больше не назначает версию факта.</summary>
    private ulong _botFactRevision;
    /// <summary>Каталог конфигураций ботов (credential/идентичности): authority для Core-инициированных
    /// действий (navigate), чтобы intent нёс только identity и цель, а не повторял station policies.</summary>
    private readonly Dictionary<string, BotConfiguration> _botCatalog = [];
    /// <summary>Обратный индекс actor→bot: адресная переосвещение после завершения хода именно этого actor.</summary>
    private readonly Dictionary<ActorKey, string> _botByActor = [];
    /// <summary>Боты, которым нужна свежая перцепция после хода; инъекция ConfigureBot — вне _tasks.Advance.</summary>
    private readonly HashSet<string> _botReperceive = [];
    /// <summary>Монотонный серийник Core-инициированных задач бота (move/reconfigure), вне DM producer space.</summary>
    private ulong _botInternalSerial;

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
        _botCatalog.Clear();
        _botByActor.Clear();
        _botReperceive.Clear();
    }

    /// <summary>Закрытый allowlist команд бота направляется в общую FIFO.</summary>
    private static bool IsBotCommand(RuntimeCommand command) =>
        command is ConfigureBot or RemoveBot or SubmitBotOrder or RememberBotAttacker or WakeBot or FinishBotEffect or QueryBot;

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
        if (work.Command is ConfigureBot configure) return ExecuteBotConfigure(work, configure, budget);
        var (bot, accepted, code) = ApplyBotCommand(work.Command);
        // Исход DM-исполненного решения (device/presentation) переосвещает бота, чтобы brain продолжил
        // (например, следующий salvage после recover), не ожидая отдельного хода/боя.
        if (accepted && work.Command is FinishBotEffect finish) MarkBotReperceive(finish.Result.Actor);
        _botEvents.Enqueue(new BotCommandCompleted(_epochs.Binding, BotCommandKey(work.Command), bot, accepted, code, _bots.Query(bot)));
        return new(1, true, code, accepted);
    }

    /// <summary>
    /// Core строит восприятие бота из собственных владельцев (graph/authority/combat/storage) ограниченными
    /// bounded-шагами в рамках той же FIFO задачи, затем единожды публикует его в brain-owner. DM поставляет
    /// только сырые идентичности и credentials через ConfigureBot и никогда готовую perception.
    /// </summary>
    private CoreTaskStep ExecuteBotConfigure(EntityWork work, ConfigureBot configure, int budget)
    {
        work.BotSnapshot ??= StartBotSnapshot(configure.Configuration, ++_botFactRevision);
        var used = AdvanceBotSnapshot(work.BotSnapshot, budget);
        if (!work.BotSnapshot.Complete) return new(used, false);
        var snapshot = work.BotSnapshot;
        work.BotSnapshot = null;
        var accepted = snapshot.Result is { } fact && _bots!.Upsert(fact);
        var code = snapshot.Result is null ? snapshot.ErrorCode ?? "bot_snapshot_failed" : accepted ? "bot_configured" : "bot_rejected";
        if (accepted)
        {
            _botCatalog[configure.Configuration.Bot.Id] = configure.Configuration;
            _botByActor[configure.Configuration.Actor] = configure.Configuration.Bot.Id;
        }
        snapshot.Dispose();
        _botEvents.Enqueue(new BotCommandCompleted(_epochs.Binding, configure.Key, configure.Configuration.Bot,
            accepted, code, _bots!.Query(configure.Configuration.Bot)));
        return new(Math.Max(1, used), true, code, accepted);
    }

    /// <summary>Применяет ровно одну команду к brain-owner; world/HP здесь не меняются.</summary>
    private (BotKey Bot, bool Accepted, string Code) ApplyBotCommand(RuntimeCommand command) => command switch
    {
        QueryBot query => (query.Bot, true, "bot_query"),
        SubmitBotOrder order => (order.Bot, _bots!.SetOrder(order.Bot, order.Order, SimulationTimeDs), "bot_order"),
        RemoveBot remove => (remove.Bot, RemoveBotBrain(remove.Bot), "bot_removed"),
        WakeBot wake => (wake.Bot, _bots!.Wake(wake.Bot), "bot_wake"),
        RememberBotAttacker attacker => (attacker.Bot, _bots!.RememberAttacker(attacker.Bot, attacker.Attacker), "bot_attacker"),
        FinishBotEffect finish => (finish.Result.Bot, _bots!.ApplyResult(finish.Result), "bot_effect_result"),
        _ => throw new InvalidOperationException("Invalid bot command.")
    };

    /// <summary>Снимает brain и его каталог credential ровно для этого поколения.</summary>
    private bool RemoveBotBrain(BotKey key)
    {
        if (_botCatalog.Remove(key.Id, out var config)) _botByActor.Remove(config.Actor);
        _botReperceive.Remove(key.Id);
        return _bots!.Remove(key);
    }

    /// <summary>Доля brain-owner в общем entity lane: сначала выдача решений, затем мышление, затем снова выдача.</summary>
    private int AdvanceBots(int now, int budget)
    {
        if (_bots is null || budget <= 0) return 0;
        DrainBotReperceive();
        var used = DrainBotEffects(budget);
        if (used < budget) used += _bots.Advance(now, budget - used);
        if (used < budget) used += DrainBotEffects(budget - used);
        return used;
    }

    /// <summary>
    /// После завершённого хода бот получает свежую перцепцию из каталога (новая позиция/маршруты), и brain
    /// планирует следующий hop — так замыкается непрерывная навигация без повторного ConfigureBot от DM.
    /// Инъекция идёт здесь (автоматический lane, до _tasks.Advance), а не из completion hook внутри Advance;
    /// would_block остаётся на следующий цикл.
    /// </summary>
    private void DrainBotReperceive()
    {
        if (_botReperceive.Count == 0 || State != MirrorSyncState.Ready) return;
        foreach (var botId in _botReperceive.ToArray())
        {
            if (!_botCatalog.TryGetValue(botId, out var config)) { _botReperceive.Remove(botId); continue; }
            var admission = EnqueueBot(new ConfigureBot(new RequestKey("bot.internal", ++_botInternalSerial), config));
            if (admission.Status != AdmissionStatus.WouldBlock) _botReperceive.Remove(botId);
        }
    }

    /// <summary>Завершённый ход или смена боевого состояния actor помечает его bot для переосвещения; enqueue — в AdvanceBots.</summary>
    private void MarkBotReperceive(ActorKey actor)
    {
        if (_bots is not null && _botByActor.TryGetValue(actor, out var botId)) _botReperceive.Add(botId);
    }

    /// <summary>
    /// Навигацию и бой исполняют владельцы Core, а не DM: BotNavigate → MoveEntity владельцу сущностей (к
    /// следующему hop, credential из каталога), BotCombat → SubmitCombatIntent в CombatSimulation. Общие native
    /// пути двигают/бьют, EntityWorld остаётся в синхроне; brain получает "uncertain" (одноразовый fire) и
    /// планирует дальше после переосвещения. Остальные решения уходят в DM как native-presentation.
    /// </summary>
    private int DrainBotEffects(int budget)
    {
        var used = 0;
        while (_bots is not null && used < budget && _botEvents.Count < BotOutputLimit && _bots.TryDequeueEffect(out var effect))
        {
            if (effect.Action is BotNavigate navigate) RouteBotNavigate(effect, navigate);
            else if (effect.Action is BotCombat combat) RouteBotCombat(effect, combat);
            else _botEvents.Enqueue(new BotNativeDecision(_epochs.Binding, new RequestKey("bot.effects", ++_botDecisionSerial), effect));
            used++;
        }
        return used;
    }

    /// <summary>
    /// Ставит один MoveEntity владельцу сущностей к СЛЕДУЮЩЕМУ hop: для direct цели — она сама, для далёкой —
    /// первый hop source-tree из перцепции (Core navigation). Общий native move path двигает бота на один шаг,
    /// а петля переосвещения ведёт его к далёкой цели hop-by-hop. Если шаг недоступен, ход не ставится.
    /// </summary>
    private void RouteBotNavigate(BotEffect effect, BotNavigate navigate)
    {
        var hop = navigate.Direct ? navigate.Destination : _bots!.FirstHop(effect.Bot, navigate.Destination);
        if (hop is { } step && _botCatalog.TryGetValue(effect.Bot.Id, out var config) &&
            _entities?.Read(effect.Actor) is { Alive: true, PendingEffectId: null } self)
            EnqueueEntity(new SubmitEntityOperation(
                new EntityOperation(new RequestKey("bot.internal", ++_botInternalSerial), effect.Actor, self.Revision, new MoveEntity(step)),
                config.Access));
        _bots!.ApplyResult(new BotEffectResult(effect.Bot, effect.Actor, effect.ActionSerial, effect.ExpectedRevision, "uncertain"));
    }

    /// <summary>
    /// BotCombat → SubmitCombatIntent в общий combat owner: тот сам синхронизирует actor+loadout, валидирует
    /// контакт/цель и применяет intent (вся turn/AP/cast механика уже в CombatSimulation). Brain получает
    /// "uncertain"; смена боевого состояния бота (включая Wake на его ход) триггерит переосвещение.
    /// </summary>
    private void RouteBotCombat(BotEffect effect, BotCombat combat)
    {
        EnqueueCombat(new SubmitCombatIntent(new RequestKey("bot.internal", ++_botInternalSerial), combat.Intent));
        _bots!.ApplyResult(new BotEffectResult(effect.Bot, effect.Actor, effect.ActionSerial, effect.ExpectedRevision, "uncertain"));
    }

    /// <summary>Output dequeue не продвигает часы и ничего не исполняет в native мире.</summary>
    private bool TryBotEvent(out TransportEvent? output) => _botEvents.TryDequeue(out output);

    /// <summary>Исходный producer:serial команды бота для ACK и дедупликации.</summary>
    private static RequestKey BotCommandKey(RuntimeCommand command) => command switch
    {
        ConfigureBot configure => configure.Key,
        QueryBot query => query.Key,
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
                case ConfigureBot configure:
                    writer.Write((byte)1); Key(writer, configure.Key);
                    var config = configure.Configuration;
                    Bot(writer, config.Bot); Actor(writer, config.Actor); writer.Write(config.ProgramId);
                    writer.Write(EntityAccessAuthority.Fingerprint(config.Access)!);
                    writer.Write(config.Home is not null); if (config.Home is { } home) Node(writer, home);
                    writer.Write(config.Owner is not null); if (config.Owner is { } owner) Actor(writer, owner);
                    writer.Write(config.Inventory is not null);
                    if (config.Inventory is { } inventory) { writer.Write(inventory.Id); writer.Write(inventory.Generation); }
                    writer.Write(config.Behavior is not null);
                    if (config.Behavior is { } behavior)
                    { writer.Write(behavior.Strategy); writer.Write(behavior.Aggression); writer.Write(behavior.Caution); writer.Write(behavior.Cooperation); }
                    writer.Write(config.ConsoleAvailable); writer.Write(config.GoalsEnabled);
                    break;
                case QueryBot query:
                    writer.Write((byte)7); Key(writer, query.Key); Bot(writer, query.Bot);
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
