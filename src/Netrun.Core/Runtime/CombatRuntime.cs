using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Combat;
using Netrun.Core.Scheduling;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>FIFO composition общего actor owner и CombatSimulation; никаких внешних HP/status фактов.</summary>
public sealed partial class NetworkRuntime
{
    /// <summary>Частичный head удерживает только semantic result; Apply не повторяется после output pressure.</summary>
    private sealed class CombatWork
    {
        internal bool Applied;
        internal CombatResult? Result;
    }

    private CombatSimulation? _combat;
    private readonly Dictionary<EntityWork, CombatWork> _combatWork = [];
    private readonly Dictionary<ActorKey, ValueList<string>> _combatLoadouts = [];
    private readonly Dictionary<string, ActorKey> _combatActorIds = new(StringComparer.Ordinal);
    private readonly LinkedList<ActorKey> _combatActorOrder = [];
    private readonly Dictionary<ActorKey, LinkedListNode<ActorKey>> _combatActorEntries = [];
    private readonly Queue<TransportEvent> _combatEvents = [];
    private readonly HashSet<ActorKey> _preparedCombatMoves = [];
    private CombatEffect? _pendingCombatEffect;
    private EntityOperation? _pendingCombatOperation;
    private RequestKey? _pendingCombatKey;
    private ulong _combatEffectSerial;
    private LinkedListNode<ActorKey>? _combatRefreshCursor;
    private int _combatRefreshTime = -1;
    private bool _combatRefreshReady;

    /// <summary>Только immutable непрочитанные semantic projections и native notices.</summary>
    public int CombatEventCount => _combatEvents.Count;
    /// <summary>При output pressure следующий FIFO intent не может обогнать не применённый combat effect.</summary>
    private bool CombatEffectsPending => _pendingCombatEffect is not null || _combat?.EffectCount > 0;
    /// <summary>Combat state создаётся после actor registry; profile сам не выдаёт ни одного executable.</summary>
    private void InitializeCombat() => _combat = new CombatSimulation();

    /// <summary>Закрывает binding без повторения staged эффекта при reconnect.</summary>
    private void StopCombat()
    {
        _combat = null; _combatWork.Clear(); _combatLoadouts.Clear(); _combatActorIds.Clear();
        _combatActorOrder.Clear(); _combatActorEntries.Clear(); _combatEvents.Clear();
        _preparedCombatMoves.Clear();
        _pendingCombatEffect = null; _pendingCombatOperation = null; _pendingCombatKey = null;
        _combatRefreshCursor = null; _combatRefreshTime = -1; _combatRefreshReady = false;
    }

    /// <summary>Позволяет root switch направлять закрытый command set в общий FIFO.</summary>
    private static bool IsCombatCommand(RuntimeCommand command) => command is SubmitCombatIntent or ConfigureActorCombat;

    /// <summary>Только admission/fingerprint; боевые вычисления выполняются последующим Core Pump.</summary>
    private TransportAdmission EnqueueCombat(RuntimeCommand command)
    {
        var key = command switch
        { SubmitCombatIntent submit => submit.Key, ConfigureActorCombat configure => configure.Key, _ => throw new ArgumentException("Invalid combat command.", nameof(command)) };
        if (State != MirrorSyncState.Ready) return new(AdmissionStatus.Rejected, "world_unavailable", key);
        var fingerprint = CombatCommandFingerprint(command);
        return fingerprint is null ? new(AdmissionStatus.Rejected, "combat_command_invalid", key) :
            _tasks!.Enqueue(key, new EntityWork(command), fingerprint);
    }

    /// <summary>Intent применяется единожды, а все его registry effects завершаются до следующего FIFO head.</summary>
    private CoreTaskStep ExecuteCombatTask(EntityWork task, int budget)
    {
        if (budget <= 0) return new(0, false);
        if (!_combatWork.TryGetValue(task, out var work)) _combatWork.Add(task, work = new());
        var used = DrainCombatEffects(budget);
        if (CombatEffectsPending || used == budget) return new(used, false);
        var actor = task.Command switch
        { SubmitCombatIntent submit => submit.Intent.Actor, ConfigureActorCombat configure => configure.Actor, _ => throw new InvalidOperationException("Invalid combat task.") };
        var key = task.Command is SubmitCombatIntent intent ? intent.Key : ((ConfigureActorCombat)task.Command).Key;
        if (!work.Applied)
        {
            if (_combatEvents.Count >= CombatOutputLimit) return new(used, false);
            work.Applied = true;
            work.Result = ExecuteCombatCommand(task.Command);
            used++;
        }
        if (used < budget) used += DrainCombatEffects(budget - used);
        if (CombatEffectsPending || _combatEvents.Count >= CombatOutputLimit) return new(used, false);
        var result = work.Result!;
        _combatEvents.Enqueue(new CombatIntentCompleted(_epochs.Binding, key, actor, result.Accepted, result.Code,
            _combat!.ReadEncounter(actor), _combat.ReadActor(actor), Loadout(actor)));
        _combatWork.Remove(task);
        return new(Math.Max(1, used), true, result.Code, result.Accepted);
    }

    /// <summary>Получает HP/residence/status только у EntityWorld, включая trusted inventory configuration.</summary>
    private CombatResult ExecuteCombatCommand(RuntimeCommand command)
    {
        if (_combat is null || _entities is null) return new(false, "combat_unavailable");
        if (command is ConfigureActorCombat configure)
        {
            if (_entities.Read(configure.Actor) is not { Alive: true }) return new(false, "actor_unavailable");
            if (configure.InstalledProgramIds.Any(id => _combat.ReadProgram(id) is null)) return new(false, "program_unknown");
            _combatLoadouts[configure.Actor] = configure.InstalledProgramIds;
            SyncCombatActor(configure.Actor);
            return new(true, "combat_configured");
        }
        var intent = ((SubmitCombatIntent)command).Intent;
        if (!SyncCombatActor(intent.Actor)) return new(false, "actor_unavailable");
        var source = _entities.Read(intent.Actor)!;
        if (source.PendingEffectId is not null) return new(false, "combat_move_pending");
        if (!CombatNodeAvailable(source.Node)) return new(false, "combat_contact_unavailable");
        var target = intent switch { UseCombatProgram use => use.Target, AdmitCombatAction admit => admit.Target, _ => null };
        if (target is not null)
        {
            if (!SyncCombatActor(target) || _entities.Read(target) is not { } targetView ||
                targetView.PendingEffectId is not null || targetView.Node != source.Node || !CombatNodeAvailable(targetView.Node))
                return new(false, "combat_contact_unavailable");
        }
        if (intent is JoinCombat join && join.Node != source.Node) return new(false, "combat_contact_unavailable");
        return _combat.Apply(intent, SimulationTimeDs);
    }

    /// <summary>Bounded refresh/timers/effects делят бюджет; root не исполняет следующий intent при CombatEffectsPending.</summary>
    private int AdvanceCombat(int now, int budget)
    {
        if (_combat is null || budget <= 0) return 0;
        var used = DrainCombatEffects(budget);
        if (CombatEffectsPending || used == budget) return used;
        if (_combatRefreshCursor is null && !_combatRefreshReady && _combatRefreshTime != now)
        { _combatRefreshTime = now; _combatRefreshCursor = _combatActorOrder.First; }
        while (used < budget && _combatRefreshCursor is { } current)
        {
            _combatRefreshCursor = current.Next;
            SyncCombatActor(current.Value);
            used++;
        }
        if (_combatRefreshCursor is null) _combatRefreshReady = true;
        if (CombatEffectsPending && used < budget) used += DrainCombatEffects(budget - used);
        if (CombatEffectsPending || _combatRefreshCursor is not null || used == budget) return used;
        used += _combat.Advance(now, budget - used);
        _combatRefreshReady = false;
        if (used < budget) used += DrainCombatEffects(budget - used);
        return used;
    }

    /// <summary>Output dequeue не продвигает часы, encounter либо HP.</summary>
    private bool TryCombatEvent(out TransportEvent? output) => _combatEvents.TryDequeue(out output);
    /// <summary>Одинаковый root task event limit применяется к combat projection lane.</summary>
    private int CombatOutputLimit => _options.Tasks?.MaximumEvents ?? int.MaxValue;

    /// <summary>Факт конкретного поколения строится из единственного authoritative registry.</summary>
    private bool SyncCombatActor(ActorKey key)
    {
        if (_combat is null || _entities is null) return false;
        var view = _entities.Read(key);
        if (view is null || !view.Alive)
        {
            _combat.RemoveActor(key, SimulationTimeDs);
            _combatLoadouts.Remove(key);
            _preparedCombatMoves.Remove(key);
            if (_combatActorEntries.Remove(key, out var old))
            { if (_combatRefreshCursor == old) _combatRefreshCursor = old.Next; _combatActorOrder.Remove(old); }
            if (_combatActorIds.GetValueOrDefault(key.Id) == key) _combatActorIds.Remove(key.Id);
            return false;
        }
        if (_combatActorIds.TryGetValue(key.Id, out var previous) && previous != key)
        {
            _combat.RemoveActor(previous, SimulationTimeDs);
            _combatLoadouts.Remove(previous);
            _preparedCombatMoves.Remove(previous);
            if (_combatActorEntries.Remove(previous, out var old))
            { if (_combatRefreshCursor == old) _combatRefreshCursor = old.Next; _combatActorOrder.Remove(old); }
        }
        _combatActorIds[key.Id] = key;
        if (!_combatActorEntries.ContainsKey(key)) _combatActorEntries.Add(key, _combatActorOrder.AddLast(key));
        _combat.UpsertActor(new(key, view.Node, view.Integrity, view.MaxIntegrity, Loadout(key),
            view.Kind == EntityKind.Ice ? CombatActorKind.Ice : CombatActorKind.Entity,
            InitialShieldFactor: view.ShieldFactor,
            InitialShieldUntilDs: view.ShieldedByTurn ? long.MaxValue : view.ShieldUntilDs,
            InitialStunUntilDs: view.StunnedByTurn ? long.MaxValue : view.StunnedUntilDs));
        return true;
    }

    /// <summary>Core topology/availability проверяется также после delayed cast; remote contact пока fail-closed.</summary>
    private bool CombatNodeAvailable(NodeKey node) => Graph?.Nodes.ContainsKey(node) == true &&
        _availability.Get(node) is { Present: true, Online: true };
    /// <summary>Без trusted configuration список пуст: profile metadata не является inventory.</summary>
    private ValueList<string> Loadout(ActorKey actor) => _combatLoadouts.GetValueOrDefault(actor) ?? new([]);

    /// <summary>Хранит максимум один переданный из CombatSimulation effect и один стабильный EntityOperation retry.</summary>
    private int DrainCombatEffects(int budget)
    {
        var used = 0;
        while (_combat is not null && used < budget)
        {
            if (_pendingCombatEffect is null)
            {
                if (!_combat.TryDequeueEffect(out _pendingCombatEffect)) break;
                _pendingCombatKey = new("combat.effects", ++_combatEffectSerial);
            }
            var effect = _pendingCombatEffect!;
            MarkBotReperceive(effect.Actor);
            if (_combatEvents.Count >= CombatOutputLimit) break;
            var view = _entities!.Read(effect.Actor);
            if (view is not null && ValidCombatContact(effect, view))
            {
                var action = CombatEntityAction(effect);
                if (action is not null)
                {
                    _pendingCombatOperation ??= new(_pendingCombatKey!, effect.Actor, null, action);
                    var admission = _entities.Submit(_pendingCombatOperation, SimulationTimeDs);
                    if (admission.Status == AdmissionStatus.WouldBlock) break;
                    if (admission.Status is not (AdmissionStatus.Accepted or AdmissionStatus.Duplicate))
                    {
                        _combatEvents.Enqueue(new CombatStateChanged(_epochs.Binding, _pendingCombatKey!, effect.Actor,
                            _combat.ReadEncounter(effect.Actor), _combat.ReadActor(effect.Actor), "combat_effect_rejected"));
                        ClearPendingCombatEffect(); used++; continue;
                    }
                    view = _entities.Read(effect.Actor)!;
                    SyncCombatActor(effect.Actor);
                }
                if (effect.Kind == CombatEffectKind.ConsumeProgram)
                {
                    var installed = Loadout(effect.Actor).ToList();
                    var index = installed.FindIndex(id => id == effect.ProgramId);
                    if (index >= 0) { installed.RemoveAt(index); _combatLoadouts[effect.Actor] = new(installed); SyncCombatActor(effect.Actor); }
                }
                if (effect.Kind == CombatEffectKind.ConsumeProgram || effect.Biofeedback)
                    _combatEvents.Enqueue(new CombatNativeEffect(_epochs.Binding, _pendingCombatKey!, effect.Actor,
                        "combat-" + _pendingCombatKey!.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        view.Revision, view.ResidenceRevision, view.Node, effect));
                else if (effect.Kind is CombatEffectKind.CastStarted or CombatEffectKind.CastEnded or CombatEffectKind.EncounterChanged or
                    CombatEffectKind.EncounterEnded or CombatEffectKind.Wake)
                    _combatEvents.Enqueue(new CombatStateChanged(_epochs.Binding, _pendingCombatKey!, effect.Actor,
                        _combat.ReadEncounter(effect.Actor), _combat.ReadActor(effect.Actor), effect.Kind.ToString().ToLowerInvariant()));
            }
            ClearPendingCombatEffect();
            used++;
        }
        return used;
    }

    /// <summary>Проверка текущего contact делается до registry mutation, включая каждый повтор после pressure.</summary>
    private bool ValidCombatContact(CombatEffect effect, EntityView target)
    {
        if (effect.Kind is not (CombatEffectKind.Damage or CombatEffectKind.Heal or CombatEffectKind.Shield or CombatEffectKind.Stun)) return true;
        if (!target.Alive) return false;
        if (target.PendingEffectId is not null) return false;
        if (effect.Source is null || effect.Source == target.Actor) return true;
        var source = _entities!.Read(effect.Source);
        return source is { Alive: true, PendingEffectId: null } && source.Node == target.Node && CombatNodeAvailable(target.Node);
    }

    /// <summary>Damage уже прошёл Combat shield scaling; повторное применение щита в EntityWorld запрещено.</summary>
    private EntityAction? CombatEntityAction(CombatEffect effect)
    {
        var view = _entities!.Read(effect.Actor);
        return effect.Kind switch
        {
            CombatEffectKind.Damage when effect.Value > 0 => new DamageEntity(effect.Value, IgnoreShield: true),
            CombatEffectKind.Heal when effect.Value > 0 => new HealEntity(effect.Value),
            CombatEffectKind.Trace => new AdjustEntityTrace(effect.Value),
            CombatEffectKind.Reveal => new ClearEntityCombatGrace(),
            // Применяем точный effect prefix, а не финальный будущий status симуляции.
            // Иначе HP refresh перед queued ClearShield мог бы воскресить turn-owned shield из старого read-model.
            CombatEffectKind.Shield when view is not null => new SetEntityCombatState(effect.Value, effect.UntilDs,
                view.StunnedByTurn ? long.MaxValue : view.StunnedUntilDs),
            CombatEffectKind.Stun when view is not null => new SetEntityCombatState(view.ShieldFactor,
                view.ShieldedByTurn ? long.MaxValue : view.ShieldUntilDs, effect.UntilDs),
            _ => null
        };
    }

    /// <summary>После ACL/adjacency/cooldown admission движения резервирует ровно один AP до exact native ACK.</summary>
    private CombatResult TryPrepareEntityMove(EntityView actor)
    {
        if (_combat is null || !_combatActorEntries.ContainsKey(actor.Actor)) return new(true, "outside_combat");
        if (_preparedCombatMoves.Contains(actor.Actor)) return new(false, "combat_move_pending");
        if (!SyncCombatActor(actor.Actor)) return new(false, "actor_unavailable");
        if (_combat.ReadEncounter(actor.Actor) is not { } encounter) return new(true, "outside_combat");
        var result = _combat.Apply(new SpendCombatAp(actor.Actor, 1, Deferred: true, Token: encounter.DecisionId), SimulationTimeDs);
        if (result.Accepted) _preparedCombatMoves.Add(actor.Actor);
        return result;
    }

    /// <summary>Success/failure native результата освобождает deferral единожды без возврата AP; uncertain не повторяет движение.</summary>
    private void ResolveEntityMove(ActorKey actor)
    {
        if (_combat is null || !_combatActorEntries.ContainsKey(actor)) return;
        if (_preparedCombatMoves.Remove(actor)) _combat.Apply(new ResolveCombatAction(actor), SimulationTimeDs);
        SyncCombatActor(actor);
    }

    /// <summary>ACK ledger не удерживает CombatEffect после успешной registry mutation/notice handoff.</summary>
    private void ClearPendingCombatEffect()
    { _pendingCombatEffect = null; _pendingCombatOperation = null; _pendingCombatKey = null; }

    /// <summary>Полный deterministic command fingerprint, общий для runtime replay и FIFO producer fences.</summary>
    internal static byte[]? CombatCommandFingerprint(RuntimeCommand command)
    {
        if (!CombatCommandRules.IsValid(command)) return null;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            if (command is ConfigureActorCombat configure)
            {
                writer.Write((byte)1); Key(writer, configure.Key); Actor(writer, configure.Actor);
                writer.Write(configure.InstalledProgramIds.Count);
                foreach (var id in configure.InstalledProgramIds) writer.Write(id);
            }
            else
            {
                var submit = (SubmitCombatIntent)command;
                writer.Write((byte)2); Key(writer, submit.Key); Actor(writer, submit.Intent.Actor);
                switch (submit.Intent)
                {
                    case AdmitCombatAction admit: writer.Write((byte)1); OptionalActor(writer, admit.Target); writer.Write(admit.Cost); break;
                    case JoinCombat join: writer.Write((byte)2); writer.Write(join.Node.NodeId); writer.Write(join.Node.Generation); Optional(writer, join.Token); break;
                    case PassCombatTurn pass: writer.Write((byte)3); Optional(writer, pass.Token); break;
                    case UseCombatProgram use:
                        writer.Write((byte)4); writer.Write(use.ProgramId); OptionalActor(writer, use.Target); Optional(writer, use.Token);
                        writer.Write(use.Telegraph); writer.Write(use.DamageScale); Optional(writer, use.ExecutableId); break;
                    case ResolveCombatCast: writer.Write((byte)5); break;
                    case CancelCombatCast: writer.Write((byte)6); break;
                    case SpendCombatAp spend:
                        writer.Write((byte)7); writer.Write(spend.Cost); writer.Write(spend.Deferred); writer.Write(spend.Hostile); Optional(writer, spend.Token); break;
                    case ResolveCombatAction: writer.Write((byte)8); break;
                }
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));

        static void Key(BinaryWriter writer, RequestKey key) { writer.Write(key.ProducerId); writer.Write(key.Sequence); }
        static void Actor(BinaryWriter writer, ActorKey actor) { writer.Write(actor.Id); writer.Write(actor.Generation); }
        static void Optional(BinaryWriter writer, string? text) { writer.Write(text is not null); if (text is not null) writer.Write(text); }
        static void OptionalActor(BinaryWriter writer, ActorKey? actor) { writer.Write(actor is not null); if (actor is not null) Actor(writer, actor); }
    }
}
