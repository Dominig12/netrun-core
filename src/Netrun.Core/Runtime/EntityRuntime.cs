using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Scheduling;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Netrun.Core.Plugins;

namespace Netrun.Core.Runtime;

public sealed partial class NetworkRuntime
{
    /// <summary>FIFO head сохраняет только immutable intent и incremental access/link cursors.</summary>
    private sealed class EntityWork(RuntimeCommand command)
    {
        internal readonly RuntimeCommand Command = command;
        internal EntityAccessAuthority? Authority;
        internal GraphSnapshot? Graph;
        internal ulong AvailabilityVersion;
        internal int EdgeCursor;
        internal bool LinkConfirmed;
        internal IModuleTask? ModuleTask;
        internal ModuleStep? ModuleStep;
        internal int EffectCursor, ProjectionCursor;
        internal bool CombatMovePrepared, EntityEffectResolved;
        internal TransportActionResult? EntityEffectOutcome;
        /// <summary>Незавершённый cooperative снимок восприятия бота ConfigureBot; продолжается тем же FIFO head.</summary>
        internal BotSnapshotWork? BotSnapshot;
    }

    /// <summary>Authoritative состояние всех resident/bot/ICE процессов binding.</summary>
    private EntityWorld? _entities;
    /// <summary>Единый FIFO admission actor tasks; native ожидание никогда не удерживает его head.</summary>
    private CoreTaskQueue<EntityWork>? _tasks;
    /// <summary>Разрешение действует только в стеке текущей FIFO задачи, после Core ACL/link проверки.</summary>
    private EntityWork? _executingEntityWork;
    /// <summary>Автоматические lifecycle/timer owners получают отдельную долю, не вытесняя FIFO малым бюджетом.</summary>
    private int _automaticEntityLane;
    private bool _automaticEntityTurn;

    /// <summary>Authoritative registry count для мониторинга; DM хранит лишь read models.</summary>
    public int EntityCount => _entities?.Count ?? 0;
    /// <summary>Количество принятых задач, ожидающих FIFO dispatch/continuation.</summary>
    public int PendingTaskCount => _tasks?.Count ?? 0;
    /// <summary>Глобальная последовательность принятых задач в этом binding.</summary>
    public ulong EnqueuedTaskSequence => _tasks?.EnqueuedSequence ?? 0;
    /// <summary>Граница завершённого FIFO prefix.</summary>
    public ulong CompletedTaskSequence => _tasks?.CompletedSequence ?? 0;

    /// <summary>Создаёт actor owner и общую очередь до обработки первого input.</summary>
    private void InitializeEntities()
    {
        _entities = new EntityWorld(_epochs.Binding, _options.Entities, (actor, destination) =>
        {
            var work = _executingEntityWork;
            return work?.Command is SubmitEntityOperation submit && submit.Operation.Actor == actor.Actor &&
                work.LinkConfirmed && work.Graph == Graph && work.Authority?.CanTraverse(destination, actor.Node) == true
                ? null : "authority_unavailable";
        });
        _tasks = new CoreTaskQueue<EntityWork>(_epochs.Binding, ExecuteEntityTask, _options.Tasks);
        InitializeStorage();
        InitializeCombat();
        InitializeBots();
        InitializeConsoleSessions();
        _moduleSessions = _pluginRegistry.Modules.CreateSessions(new ModuleContext(_epochs.Binding,
            key => _entities.Read(key), () => SimulationTimeDs));
    }

    /// <summary>Читает точное поколение процесса; не возвращает mutable registry или native объекты.</summary>
    public EntityView? ReadEntity(ActorKey actor)
    {
        Enter();
        try { return _entities?.Read(actor); }
        finally { _busy = false; }
    }

    /// <summary>Только enqueue: ни ACL indexing, ни node traversal внутри Apply не выполняются.</summary>
    private TransportAdmission EnqueueEntity(SubmitEntityOperation submit)
    {
        if (State != MirrorSyncState.Ready) return new(AdmissionStatus.Rejected, "world_unavailable", submit.Operation.Key);
        return _tasks!.Enqueue(submit.Operation.Key, new EntityWork(submit), EntityCommandFingerprint(submit)!);
    }

    /// <summary>Clock/lifecycle и FIFO dispatch делят один lane budget; output handoff имеет собственные credits.</summary>
    private int AdvanceEntityTasks(int budget)
    {
        if (_entities is null || _tasks is null) return 0;
        var work = 0;
        if (CombatEffectsPending)
        {
            work = AdvanceCombat(SimulationTimeDs, budget);
            if (CombatEffectsPending || work == budget) return work;
        }
        _automaticEntityTurn = !_automaticEntityTurn;
        var automaticBudget = budget - work > 1 ? (budget - work) / 2 : (_automaticEntityTurn ? budget - work : 0);
        var automaticWork = 0;
        var start = _automaticEntityLane;
        _automaticEntityLane = (start + 1) % 4;
        for (var offset = 0; offset < 4 && automaticWork < automaticBudget; offset++)
        {
            var remaining = automaticBudget - automaticWork;
            automaticWork += ((start + offset) % 4) switch
            {
                0 => _entities.Advance(SimulationTimeDs, remaining),
                1 => AdvanceStorage(SimulationTimeDs, remaining),
                2 => AdvanceCombat(SimulationTimeDs, remaining),
                _ => AdvanceBots(SimulationTimeDs, remaining)
            };
        }
        work += automaticWork;
        if (CombatEffectsPending) return work;
        return work + _tasks.Advance(budget - work);
    }

    /// <summary>Одна FIFO задача либо продолжает Core вычисление, либо завершает постановку native эффекта.</summary>
    private CoreTaskStep ExecuteEntityTask(EntityWork work, int budget)
    {
        var effects = DrainCombatEffects(budget);
        if (CombatEffectsPending || effects == budget) return new(effects, false);
        var step = ExecuteEntityTaskCore(work, budget - effects);
        return step with { Work = step.Work + effects };
    }

    /// <summary>Каждый доменный head начинается после обязательных effects предыдущего head.</summary>
    private CoreTaskStep ExecuteEntityTaskCore(EntityWork work, int budget)
    {
        if (IsStorageCommand(work.Command)) return ExecuteStorageTask(work, budget);
        if (IsCombatCommand(work.Command)) return ExecuteCombatTask(work, budget);
        if (IsBotCommand(work.Command)) return ExecuteBotTask(work, budget);
        if (IsConsoleCommand(work.Command)) return ExecuteConsoleTask(work, budget);
        if (work.Command is SubmitModuleRequest module) return ExecuteModuleTask(work, module.Request, budget);
        if (work.Command is FinishEntityEffect finish)
        {
            var effectWork = 0;
            if (work.EntityEffectOutcome is null)
            {
                var result = _entities!.Complete(finish.Result, SimulationTimeDs);
                if (result.WouldBlock) return new(0, false);
                work.EntityEffectOutcome = result;
                effectWork++;
            }
            if (work.EntityEffectOutcome.Accepted && !work.EntityEffectResolved)
            {
                ResolveEntityMove(finish.Result.Actor);
                MarkBotReperceive(finish.Result.Actor);
                work.EntityEffectResolved = true;
            }
            if (effectWork < budget) effectWork += DrainCombatEffects(budget - effectWork);
            if (CombatEffectsPending) return new(effectWork, false);
            return new(Math.Max(1, effectWork), true, work.EntityEffectOutcome.Code, work.EntityEffectOutcome.Accepted);
        }
        var submit = (SubmitEntityOperation)work.Command;
        var operation = submit.Operation;
        var consumed = 0;
        if (operation.Action is CreateEntity create &&
            (!Nodes.TryGetValue(create.Definition.Node.NodeId, out var node) || node.Generation != create.Definition.Node.Generation))
            return new(1, true, "stale_endpoint", false);
        if (operation.Action is MoveEntity move)
        {
            if (submit.Access is null) return new(1, true, "authority_unavailable", false);
            var actor = _entities!.Read(operation.Actor);
            if (actor is null) return new(1, true, "entity_unknown", false);
            var graph = Graph;
            if (graph is null) return new(1, true, "topology_pending", false);
            if (work.Graph is null)
            {
                work.Graph = graph;
                work.AvailabilityVersion = _availability.Revision;
            }
            if (work.Graph != graph || work.AvailabilityVersion != _availability.Revision)
                return new(1, true, "navigation_changed", false);
            work.Authority ??= new(submit.Access, key => work.Graph.LanIds.GetValueOrDefault(key));
            if (!work.Authority.Ready && work.Authority.ErrorCode is null)
            {
                consumed = work.Authority.Advance(budget);
                if (consumed == budget) return new(consumed, false);
            }
            if (work.Authority.ErrorCode is { } code) return new(Math.Max(1, consumed), true, code, false);
            var sourceState = _availability.Get(actor.Node);
            var targetState = _availability.Get(move.Destination);
            if (!graph.Nodes.ContainsKey(actor.Node) || !graph.Nodes.ContainsKey(move.Destination) ||
                !sourceState.Present || !sourceState.Online || !targetState.Present || !targetState.Online)
                return new(Math.Max(1, consumed), true, "stale_endpoint", false);
            if (!work.Authority.CanTraverse(move.Destination, actor.Node)) return new(Math.Max(1, consumed), true, "access_denied", false);
            var source = graph.Nodes[actor.Node];
            var target = graph.Nodes[move.Destination];
            if (_availability.BackboneEnabled && source.State.Network?.IsNtnetRelay == true &&
                target.State.Network?.IsNtnetRelay == true && sourceState.RelayOperational && targetState.RelayOperational)
                work.LinkConfirmed = true;
            if (!work.LinkConfirmed && graph.Adjacency.TryGetValue(actor.Node, out var edges))
            {
                while (consumed < budget && work.EdgeCursor < edges.Count)
                {
                    var edge = edges[work.EdgeCursor++];
                    consumed++;
                    if (edge.From == move.Destination || edge.To == move.Destination) { work.LinkConfirmed = true; break; }
                }
                if (!work.LinkConfirmed && work.EdgeCursor < edges.Count) return new(consumed, false);
            }
            if (!work.LinkConfirmed) return new(Math.Max(1, consumed), true, "not_adjacent", false);
            if (consumed == budget) return new(consumed, false);
            if (!work.CombatMovePrepared)
            {
                if (!actor.Alive) return new(Math.Max(1, consumed), true, "entity_dead", false);
                if (operation.ExpectedRevision is { } expected && expected != actor.Revision)
                    return new(Math.Max(1, consumed), true, "stale_actor_revision", false);
                if (actor.PendingEffectId is not null) return new(Math.Max(1, consumed), true, "entity_effect_pending", false);
                if (SimulationTimeDs < actor.NextHopDs || SimulationTimeDs < actor.StunnedUntilDs)
                    return new(Math.Max(1, consumed), true, "entity_move_cooldown", false);
                var prepared = TryPrepareEntityMove(actor);
                if (!prepared.Accepted) return new(Math.Max(1, consumed), true, prepared.Code, false);
                work.CombatMovePrepared = true;
            }
        }
        _executingEntityWork = work;
        TransportAdmission admission;
        try { admission = _entities!.Submit(operation, SimulationTimeDs); }
        finally { _executingEntityWork = null; }
        if (work.CombatMovePrepared && admission.Status is AdmissionStatus.Rejected)
            ResolveEntityMove(operation.Actor);
        return admission.Status == AdmissionStatus.WouldBlock ? new(consumed, false) :
            new(Math.Max(1, consumed), true, admission.Code, admission.Status is AdmissionStatus.Accepted or AdmissionStatus.Duplicate);
    }

    /// <summary>Полная identity queued команды, включая raw access snapshot и native effect proof.</summary>
    internal static byte[]? EntityCommandFingerprint(RuntimeCommand command)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            switch (command)
            {
                case SubmitEntityOperation submit:
                    var operation = EntityWorld.Fingerprint(submit.Operation);
                    if (operation is null || submit.Access is not null && !EntityAccessAuthority.IsValidSnapshot(submit.Access)) return null;
                    writer.Write((byte)1); writer.Write(operation); writer.Write(submit.Access is not null);
                    if (submit.Access is not null) writer.Write(EntityAccessAuthority.Fingerprint(submit.Access)!);
                    break;
                case FinishEntityEffect finish when TransportFingerprint.ValidKey(finish.Key) && EntityWorld.IsValidResult(finish.Result):
                    writer.Write((byte)2); writer.Write(finish.Key.Id); writer.Write(finish.Result.BindingEpoch);
                    writer.Write(finish.Result.Actor.Id); writer.Write(finish.Result.Actor.Generation); writer.Write(finish.Result.EffectId);
                    writer.Write(finish.Result.ResidenceRevision); writer.Write(finish.Result.Success);
                    break;
                default: return null;
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }
}
