using System.Security.Cryptography;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Netrun.Core.Communication;
using Netrun.Core.Discovery;
using Netrun.Core.Projection;

namespace Netrun.Core.Runtime;

/// <summary>
/// Последовательный coordinator mirror → topology → routes → transport с общей causal последовательностью.
/// Clock меняется только подтверждённым RuntimeInput; Pump обслуживает ту же временную отметку.
/// </summary>
public sealed class NetworkRuntime : INetworkWorldView, IDisposable
{
    /// <summary>Bounded replay хранит hash и безопасный результат команды, но не входной payload.</summary>
    private sealed record ReplayEntry(byte[] Fingerprint, TransportAdmission? Admission,
        TransportActionResult? Action, RouteResult? Route, string? ErrorCode, AnchorQueryResult? Anchor);

    /// <summary>Идентичность одной world/engine/binding сессии.</summary>
    private readonly Epochs _epochs;
    /// <summary>Typed конфигурация composition.</summary>
    private readonly RuntimeOptions _options;
    /// <summary>Фактические limits mirror и общего semantic ledger.</summary>
    private readonly CoreLimits _core;
    /// <summary>Владелец snapshot/catch-up и опубликованных world records.</summary>
    private readonly WorldSynchronization _synchronization;
    /// <summary>Владелец стабильных bindings и целых immutable graphs.</summary>
    private readonly NetworkTopology _topology;
    /// <summary>Текущие поколения availability, обновляемые по затронутым ID.</summary>
    private readonly NetworkAvailability _availability;
    /// <summary>Bounded router готовой публикации с доступностью текущего мира.</summary>
    private readonly NetworkRouter _router;
    /// <summary>Current native port advertisements; обычный graph остаётся независимым.</summary>
    private readonly CommunicationPortCatalog _communicationPorts;
    /// <summary>Явный fallback provider с family gate и текущим availability overlay.</summary>
    private readonly CommunicationRouter _communicationRoutes;
    /// <summary>Owner-local contributors позволяют обновлять только services затронутых физических экземпляров.</summary>
    private readonly Dictionary<NodeKey, SortedDictionary<string, CommunicationPortFact>> _portSources = [];
    /// <summary>Только существующие owner с несовместимыми advertisements; такие порты отозваны до исправления.</summary>
    private readonly HashSet<NodeKey> _conflictedPorts = [];
    /// <summary>Очереди запросов и native outcomes; ссылка освобождается при Dispose вместе с payload.</summary>
    private NetworkTransport? _transport;
    /// <summary>Family/tag индекс обновляется после initial publication и затем только по touched service IDs.</summary>
    private readonly ServiceDirectory _directory;
    /// <summary>Bounded async discovery имеет отдельные query/pages caps; после invalidation больше не выдаёт наблюдения.</summary>
    private ServiceDiscovery? _discovery;
    /// <summary>Persistent indexed observations; only facts generate notifications.</summary>
    private ServiceWatchBroker? _serviceWatches;
    /// <summary>Общий bounded full source-tree cache использует тот же live availability overlay, что pair router.</summary>
    private readonly SourceRouteTreeProvider _sourceTrees;
    /// <summary>Одноразовые graph/routes UI проекции без native effects.</summary>
    private ProjectionBroker? _projections;
    /// <summary>O(touched) stamps отделяют global transit authority от независимых endpoint revisions.</summary>
    private readonly GraphAuthorityTracker _authority;
    /// <summary>Один persistent native mirror stream с явным page ACK и bounded baseline/target.</summary>
    private GraphSynchronizationBroker? _graphSynchronization;
    /// <summary>Число независимых work/output lanes одного последовательного владельца.</summary>
    private const int LaneCount = 4;
    private const int OutputLaneCount = 5;
    /// <summary>Первый work producer следующего Pump; единичный budget справедлив между calls.</summary>
    private int _nextPumpLane;
    /// <summary>Независимый round-robin transport/discovery/projection outputs.</summary>
    private int _nextOutputLane;
    /// <summary>Replay только успешно принятых полных logical frames.</summary>
    private readonly Dictionary<ulong, ReplayEntry> _recent = [];
    /// <summary>FIFO bounded окна, без сканирования всей истории.</summary>
    private readonly Queue<ulong> _recentOrder = [];
    /// <summary>Неиспользуемый при pending старый graph хранится только самим topology owner.</summary>
    private GraphSnapshot? _graph;
    /// <summary>Монотонный serial structural изменений, не совпадающий с каждым logical command.</summary>
    private ulong _topologyRevision;
    /// <summary>Повторяем failed build только после новых structural inputs, не на каждом heartbeat.</summary>
    private ulong _attemptedTopologyRevision;
    /// <summary>Последняя published node/tombstone capacity диагностика от sync.</summary>
    private int _trackedCount;
    /// <summary>Запрет callbacks повторно войти в single-owner runtime.</summary>
    private bool _busy;
    /// <summary>Явный lifetime освобождает staging hash и транспортные payload.</summary>
    private bool _disposed;
    /// <summary>Standalone использует options; streaming Host откладывает structural Build до bounded input group.</summary>
    private bool _deferTopologyBuild;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, NodeFact> Nodes => _synchronization.Nodes;
    /// <inheritdoc />
    public IReadOnlyDictionary<string, SectorFact> Sectors => _synchronization.Sectors;
    /// <inheritdoc />
    public IReadOnlyDictionary<string, ServiceFact> Services => _synchronization.Services;
    /// <summary>Стадия синхронизации фактов, независимая от готовности производного графа.</summary>
    public MirrorSyncState State => _synchronization.State;
    /// <summary>Последняя целиком принятая логическая команда или пакет фактов.</summary>
    public ulong AppliedSequence => _synchronization.AppliedSequence;
    /// <summary>Подтверждённое игровое время в целых децисекундах.</summary>
    public int SimulationTimeDs => _synchronization.SimulationTimeDs;
    /// <summary>Только пригодный graph; старый graph при pending/resync никогда не передаётся transport.</summary>
    public GraphSnapshot? Graph => State == MirrorSyncState.Ready && !TopologyPending ? _graph : null;
    /// <summary>До успешного Build маршруты и handoff invocation запрещены.</summary>
    public bool TopologyPending { get; private set; } = true;
    /// <summary>Последняя безопасная причина отказа Build либо null после исправления.</summary>
    public string? TopologyError { get; private set; }
    /// <summary>Native metadata conflict не превращается в fatal ErrorCode и не закрывает обычные Machine routes.</summary>
    public string? CommunicationError => _conflictedPorts.Count == 0 ? null : "communication_port_conflict";
    /// <summary>Количество текущих collapsed native advertisements, независимо от их switches.</summary>
    public int CommunicationPortCount => _communicationPorts.Count;
    /// <summary>Число незавершённых bounded запросов; после Dispose — ноль.</summary>
    public int ActiveRequestCount => _transport?.ActiveCount ?? 0;
    /// <summary>Число ожидающих отдачи transport events; payload не включается в диагностику.</summary>
    public int EventCount => (_transport?.EventCount ?? 0) + (_discovery?.EventCount ?? 0) + (_serviceWatches?.EventCount ?? 0) + (_projections?.EventCount ?? 0) + (_graphSynchronization?.EventCount ?? 0);
    /// <summary>Количество активных async lookup, независимо от machine request slots.</summary>
    public int ActiveDiscoveryCount => _discovery?.ActiveCount ?? 0;
    public int ActiveServiceWatchCount => _serviceWatches?.ActiveCount ?? 0;
    /// <summary>Число живых service IDs в lookup индексе; диагностический getter не копирует registry.</summary>
    public int DiscoveryServiceCount => _directory.Count;
    /// <summary>Монотонная версия actual touched-service изменений lookup индекса.</summary>
    public ulong DiscoveryDirectoryRevision => _directory.Revision;
    /// <summary>Активные одноразовые UI queries, включая terminal страницы до handoff.</summary>
    public int ActiveProjectionCount => _projections?.ActiveCount ?? 0;

    /// <summary>Создаёт composition без endpoint, background worker или автоматического продвижения времени.</summary>
    public NetworkRuntime(Epochs epochs, RuntimeOptions? options = null)
    {
        _epochs = epochs;
        _options = options ?? new RuntimeOptions();
        _deferTopologyBuild = _options.DeferTopologyBuild;
        _core = _options.Core ?? new CoreLimits();
        if (_options.PumpWorkBudget < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _synchronization = new WorldSynchronization(epochs, _core);
        _topology = new NetworkTopology(_options.Topology);
        _availability = new NetworkAvailability(maxNodes: _core.MaxTrackedNodes);
        _router = new NetworkRouter(_availability, options: _options.Router);
        _communicationPorts = new CommunicationPortCatalog(maxPorts: _core.MaxTrackedNodes);
        _communicationRoutes = new CommunicationRouter(_communicationPorts, _availability, _router, options: _options.Communication);
        _transport = new NetworkTransport(this, () => Graph, _router, epochs.Binding, _options.Transport, _communicationRoutes);
        _sourceTrees = new SourceRouteTreeProvider(_availability, options: _options.SourceTrees);
        _directory = new ServiceDirectory(_core.MaxTrackedServices);
        _discovery = new ServiceDiscovery(this, _directory, () => Graph, new SourceTreeRouteProvider(_sourceTrees, _availability),
            epochs.Binding, _options.Discovery, _communicationRoutes);
        _serviceWatches = new ServiceWatchBroker(this, _directory, epochs.Binding);
        if (_options.Projection?.PageRows > 32)
            throw new ArgumentOutOfRangeException(nameof(options), "Runtime projection pages cannot exceed 32 rows.");
        _authority = new GraphAuthorityTracker(this);
        _projections = new ProjectionBroker(() => Graph, _sourceTrees, epochs.Binding, _options.Projection, () => _availability.Revision, _authority);
        _graphSynchronization = new GraphSynchronizationBroker(() => Graph, _authority, epochs.Binding, _options.GraphSynchronization);
    }

    /// <summary>
    /// Проверяет полную command identity до sync и domain effects. Принятый frame потребляет sequence,
    /// включая semantic rejected/would_block; повтор domain операции выполняется новым frame с прежним domain ID.
    /// </summary>
    public RuntimeResult Apply(RuntimeInput input)
    {
        Enter();
        try { return ApplyCore(input); }
        finally { _busy = false; }
    }

    /// <summary>Выполняет заданный default budget, не меняя логическое время.</summary>
    public int Pump() => Pump(_options.PumpWorkBudget);

    /// <summary>Выбирает bounded graph page credits до Start; применяется при согласовании duplex Host.</summary>
    public void ConfigureGraphSynchronizationWindow(int maximumPages)
    {
        Enter();
        try { _graphSynchronization!.ConfigurePageWindow(maximumPages); }
        finally { _busy = false; }
    }

    /// <summary>Включает coalesced structural publication до первого input, сохраняя legacy Apply semantics по умолчанию.</summary>
    public void EnableDeferredTopologyBuild()
    {
        Enter();
        try
        {
            if (AppliedSequence != 0) throw new InvalidOperationException("Topology deferral must be selected before processing input.");
            _deferTopologyBuild = true;
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// Публикует максимум один coalesced graph, затем делит общий workBudget между transport, lookup и UI projection.
    /// Build ограничен graph limits, а не количеством timer work units; физические часы не участвуют.
    /// </summary>
    public int Pump(int workBudget)
    {
        if (workBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(workBudget));
        Enter();
        try
        {
            TryBuildTopology();
            if (workBudget == 0) return _transport!.Advance(SimulationTimeDs, 0);
            var start = _nextPumpLane;
            _nextPumpLane = (start + 1) % LaneCount;
            var remaining = workBudget;
            for (var offset = 0; offset < LaneCount && remaining > 0; offset++)
            {
                var lanes = LaneCount - offset;
                var budget = remaining / lanes + (remaining % lanes == 0 ? 0 : 1);
                remaining -= AdvanceLane((start + offset) % LaneCount, budget);
            }
            // Неиспользованный остаток пустых/blocked lanes передаётся runnable producers без превышения общего cap.
            for (var offset = 0; offset < LaneCount && remaining > 0; offset++)
                remaining -= AdvanceLane((start + offset) % LaneCount, remaining);
            return workBudget - remaining;
        }
        finally { _busy = false; }
    }

    /// <summary>Передаёт один revalidated event вызывающему каналу; ничего не исполняет в native мире.</summary>
    public bool TryDequeueEvent(out TransportEvent? transportEvent)
    {
        Enter();
        try
        {
            _transport!.Advance(SimulationTimeDs, 0);
            for (var offset = 0; offset < OutputLaneCount; offset++)
            {
                var lane = (_nextOutputLane + offset) % OutputLaneCount;
                if (!TryLaneEvent(lane, out transportEvent)) continue;
                if (transportEvent is ServiceInvocation invocation)
                    transportEvent = invocation with
                    {
                        SourceNodeRevision = CurrentNodeRevision(invocation.SourceNode),
                        DestinationNodeRevision = CurrentNodeRevision(invocation.DestinationNode),
                        InfrastructureStamp = _authority.InfrastructureStamp,
                        AvailabilityInfrastructureStamp = _authority.AvailabilityInfrastructureStamp,
                        CommunicationRevision = invocation.Traffic == TrafficClass.Communications ? _communicationPorts.Revision : null
                    };
                _nextOutputLane = (lane + 1) % OutputLaneCount;
                return true;
            }
            transportEvent = null;
            return false;
        }
        finally { _busy = false; }
    }

    /// <summary>Закрывает binding после disconnect, отзывает непрочитанные invocation и классифицирует uncertain outcomes.</summary>
    public TransportActionResult MarkDisconnected()
    {
        Enter();
        try
        {
            _synchronization.Invalidate("runtime_disconnected");
            TopologyPending = true;
            _graph = null;
            StopReadModels();
            return _transport!.MarkUnavailable(SimulationTimeDs);
        }
        finally { _busy = false; }
    }

    /// <summary>Освобождает staging/hash и ссылки на транспортные payload; published world facts остаются диагностически доступны.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (_busy)
            throw new InvalidOperationException("Reentrant runtime disposal is not supported.");
        _synchronization.Dispose();
        StopReadModels();
        _transport = null;
        _graph = null;
        _recent.Clear();
        _recentOrder.Clear();
        TopologyPending = true;
        _disposed = true;
    }

    /// <summary>Единый ACK ledger не позволяет подменить command одним и тем же empty tick внутри sync.</summary>
    private RuntimeResult ApplyCore(RuntimeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (State == MirrorSyncState.ResyncRequired)
            return Result(ApplyStatus.ResyncRequired, "resync_required");
        if (input.Epochs != _epochs)
            return Result(ApplyStatus.Rejected, "epoch_mismatch");
        var fingerprint = RuntimeFingerprint.Compute(input, _core.MaxEntries);
        if (fingerprint is null)
            return Result(ApplyStatus.Rejected, "invalid_runtime_input");
        if (input.Sequence <= AppliedSequence)
        {
            if (_recent.TryGetValue(input.Sequence, out var previous) &&
                CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint))
                return Result(ApplyStatus.Duplicate, "duplicate", previous);
            return Fail("replay_conflict_or_expired");
        }
        if (AppliedSequence == ulong.MaxValue || input.Sequence != AppliedSequence + 1)
            return Fail("sequence_gap");
        if (input.SimulationTimeDs < SimulationTimeDs)
            return Result(ApplyStatus.Rejected, "time_regression");
        if (input.Command is not ApplyWorld && State != MirrorSyncState.Ready)
            return Result(ApplyStatus.Rejected, "not_ready");

        var priorState = State;
        var world = input.Command as ApplyWorld;
        var oldNodes = CaptureNodes(world?.Entries);
        var oldSectors = CaptureSectors(world?.Entries);
        var oldServices = CaptureServices(world?.Entries);
        var sync = _synchronization.Apply(new InputBatch(_epochs, input.Sequence, input.SimulationTimeDs, world?.Entries ?? []));
        _trackedCount = sync.Apply.TrackedCount;
        if (sync.Apply.Status == ApplyStatus.ResyncRequired)
            return Fail(sync.Apply.Code);
        if (sync.Apply.Status != ApplyStatus.Applied)
            return Result(sync.Apply.Status, sync.Apply.Code);

        TransportAdmission? admission = null;
        TransportActionResult? action = null;
        RouteResult? route = null;
        AnchorQueryResult? anchor = null;
        string? error = null;
        try
        {
            if (world is not null && State == MirrorSyncState.Ready)
            {
                if (priorState != MirrorSyncState.Ready)
                {
                    _availability.Reset(Nodes.Values);
                    _authority.Reset();
                    RebuildServiceIndexes();
                    DirtyTopology();
                }
                else
                    UpdateWorldEffects(oldNodes, oldSectors);
                if (priorState == MirrorSyncState.Ready)
                    UpdateServiceIndexes(oldServices, oldNodes);
                if (!_deferTopologyBuild)
                    TryBuildTopology();
                error = TopologyError;
            }
            else
            {
                switch (input.Command)
                {
                    case SubmitRequest submit:
                        admission = _transport!.Submit(submit.Request, SimulationTimeDs);
                        break;
                    case RejectRequest reject:
                        admission = new TransportAdmission(AdmissionStatus.Rejected, reject.Code, reject.Key);
                        break;
                    case CancelRequest cancel:
                        action = _transport!.Cancel(cancel.Key, SimulationTimeDs);
                        break;
                    case DiscoverServices discover:
                        admission = _discovery!.Submit(discover.Request);
                        break;
                    case CancelDiscovery cancel:
                        action = _discovery!.Cancel(cancel.Key);
                        break;
                    case WatchServices watch:
                        admission = _serviceWatches?.Subscribe(watch.Request) ?? new(AdmissionStatus.Rejected, "unavailable", watch.Request.Key);
                        break;
                    case UnwatchServices unwatch:
                        action = _serviceWatches?.Unsubscribe(unwatch.Key) ?? new(false, "unavailable");
                        break;
                    case QueryProjection projection:
                        admission = _projections!.Submit(projection.Request);
                        break;
                    case CancelProjection cancel:
                        action = _projections!.Cancel(cancel.Key);
                        break;
                    case StartGraphSynchronization start:
                        admission = _graphSynchronization!.Start(start.Key);
                        break;
                    case ContinueGraphSynchronization next:
                        action = _graphSynchronization!.Continue(next.Key, next.PageIndex);
                        break;
                    case CancelGraphSynchronization cancel:
                        action = _graphSynchronization!.Cancel(cancel.Key);
                        break;
                    case FinishInvocation finish:
                        action = _transport!.CompleteInvocation(finish.Result, SimulationTimeDs);
                        break;
                    case QueryRoute query:
                        if (Graph is { } graph)
                            route = _router.FindRoute(graph, query.Source, query.Destination, query.Mode);
                        else
                            error = "topology_pending";
                        break;
                    case QueryAnchor query:
                        anchor = ResolveAnchor(query.Request);
                        break;
                    case ConfigureBackbone backbone:
                        _availability.SetBackbone(backbone.Enabled);
                        _authority.SetBackbone(backbone.Enabled);
                        action = new TransportActionResult(true, "configured");
                        break;
                }
            }
        }
        catch (RoutingException failure)
        {
            error = failure.Code;
            if (world is not null)
            {
                TopologyPending = true;
                TopologyError = failure.Code;
                _graph = null;
            }
        }
        var entry = new ReplayEntry(fingerprint, admission, action, route, error, anchor);
        _recent.Add(input.Sequence, entry);
        _recentOrder.Enqueue(input.Sequence);
        if (_recentOrder.Count > _core.ReplayWindow)
            _recent.Remove(_recentOrder.Dequeue());
        return Result(ApplyStatus.Applied, "applied", entry);
    }

    /// <summary>Копирует только затронутые NodeFact references до изменения read-only live view.</summary>
    private Dictionary<string, NodeFact?> CaptureNodes(IReadOnlyList<WorldChange>? entries)
    {
        var result = new Dictionary<string, NodeFact?>(StringComparer.Ordinal);
        if (State != MirrorSyncState.Ready || entries is null)
            return result;
        foreach (var change in entries)
        {
            var id = change switch { NodeUpsert upsert => upsert.Node.NodeId, NodeRemove remove => remove.NodeId, _ => null };
            if (id is not null && !result.ContainsKey(id))
                result.Add(id, Nodes.GetValueOrDefault(id));
        }
        return result;
    }

    /// <summary>Сохраняет затронутые определения секторов; stale catalog input не вызывает лишнего Build.</summary>
    private Dictionary<string, SectorFact?> CaptureSectors(IReadOnlyList<WorldChange>? entries)
    {
        var result = new Dictionary<string, SectorFact?>(StringComparer.Ordinal);
        if (State != MirrorSyncState.Ready || entries is null)
            return result;
        foreach (var change in entries)
        {
            var id = change switch { SectorUpsert upsert => upsert.Sector.SectorId, SectorRemove remove => remove.SectorId, _ => null };
            if (id is not null && !result.ContainsKey(id))
                result.Add(id, Sectors.GetValueOrDefault(id));
        }
        return result;
    }

    /// <summary>Копирует references только затронутых service IDs до атомарного изменения registry.</summary>
    private Dictionary<string, ServiceFact?> CaptureServices(IReadOnlyList<WorldChange>? entries)
    {
        var result = new Dictionary<string, ServiceFact?>(StringComparer.Ordinal);
        if (State != MirrorSyncState.Ready || entries is null)
            return result;
        foreach (var change in entries)
        {
            var id = change switch { ServiceUpsert upsert => upsert.Service.ServiceId, ServiceRemove remove => remove.ServiceId, _ => null };
            if (id is not null && !result.ContainsKey(id))
                result.Add(id, Services.GetValueOrDefault(id));
        }
        return result;
    }

    /// <summary>Единственный полный проход по registry выполняется после initial snapshot publication.</summary>
    private void RebuildServiceIndexes()
    {
        _directory.Reset();
        _portSources.Clear();
        _conflictedPorts.Clear();
        foreach (var service in Services.Values)
        {
            IndexService(service);
            AddPortSource(service);
        }
        var resolved = new List<CommunicationPortFact>();
        foreach (var owner in _portSources.Keys)
            if (ResolvePort(owner) is { } port)
                resolved.Add(port);
        _communicationPorts.Replace(resolved);
    }

    /// <summary>Обновляет touched family/tag и owner-local port indexes; service-only delta не перестраивает обычную topology.</summary>
    private void UpdateServiceIndexes(Dictionary<string, ServiceFact?> oldServices, Dictionary<string, NodeFact?> oldNodes)
    {
        var owners = new HashSet<NodeKey>();
        foreach (var (id, before) in oldServices)
        {
            var after = Services.GetValueOrDefault(id);
            if (before == after)
                continue;
            if (after is not null) IndexService(after);
            else if (before is not null) _directory.Remove(new(before.ServiceId, before.Generation));
            _serviceWatches?.Observe(before, after);
            if (before?.Communication is not null)
            {
                owners.Add(before.Owner);
                if (_portSources.TryGetValue(before.Owner, out var sources))
                {
                    sources.Remove(id);
                    if (sources.Count == 0)
                        _portSources.Remove(before.Owner);
                }
            }
            if (after?.Communication is not null)
            {
                AddPortSource(after);
                owners.Add(after.Owner);
            }
        }
        foreach (var (id, before) in oldNodes)
        {
            if (before is not null)
                owners.Add(new NodeKey(before.NodeId, before.Generation));
            if (Nodes.GetValueOrDefault(id) is { } after)
                owners.Add(new NodeKey(after.NodeId, after.Generation));
        }
        foreach (var owner in owners)
        {
            var resolved = ResolvePort(owner);
            if (resolved is null)
                _communicationPorts.Remove(owner);
            else
                _communicationPorts.Upsert(resolved);
        }
    }

    /// <summary>Неактивный service не рекламирует capability; canonical sets дают устойчивое сравнение contributors.</summary>
    private void AddPortSource(ServiceFact service)
    {
        if (!service.Online || service.Communication is not { } port)
            return;
        if (!_portSources.TryGetValue(service.Owner, out var sources))
            _portSources.Add(service.Owner, sources = new SortedDictionary<string, CommunicationPortFact>(StringComparer.Ordinal));
        sources[service.ServiceId] = port with
        {
            ListeningFrequencies = new ValueList<int>(port.ListeningFrequencies.Order(), 128),
            ConfiguredDirectedLinks = new ValueList<NodeKey>(port.ConfiguredDirectedLinks.OrderBy(key => key.NodeId, StringComparer.Ordinal)
                .ThenBy(key => key.Generation), 128)
        };
    }

    /// <summary>Эквивалентные advertisements схлопываются; конфликт отзывает только этот owner, не выбирая случайного победителя.</summary>
    private CommunicationPortFact? ResolvePort(NodeKey owner)
    {
        _conflictedPorts.Remove(owner);
        if (!Nodes.TryGetValue(owner.NodeId, out var node) || node.Generation != owner.Generation ||
            !_portSources.TryGetValue(owner, out var sources) || sources.Count == 0)
            return null;
        var candidate = sources.First().Value;
        if (sources.Values.Any(port => port != candidate))
        {
            _conflictedPorts.Add(owner);
            return null;
        }
        return candidate;
    }

    /// <summary>Availability обновляется за O(touched); topology dirty только при фактически изменившейся геометрии/placement.</summary>
    private void UpdateWorldEffects(Dictionary<string, NodeFact?> oldNodes, Dictionary<string, SectorFact?> oldSectors)
    {
        var structural = false;
        foreach (var (id, before) in oldNodes)
        {
            var after = Nodes.GetValueOrDefault(id);
            if (before == after)
                continue;
            _authority.ObserveNodeChange(before, after);
            _serviceWatches?.ObserveNode(before, after);
            if (after is null)
            {
                if (before is not null)
                    _availability.Remove(new NodeKey(before.NodeId, before.Generation));
            }
            else
                _availability.UpdateFact(after);
            structural |= StructuralChange(before, after);
        }
        foreach (var (id, before) in oldSectors)
        {
            var after = Sectors.GetValueOrDefault(id);
            _authority.ObserveSectorChange(before, after);
            structural |= SectorGeometryChanged(before, after);
        }
        if (structural)
            DirtyTopology();
    }

    /// <summary>Отделяет routing availability от правил native attachment и derived LAN.</summary>
    private static bool StructuralChange(NodeFact? before, NodeFact? after)
    {
        if (before is null || after is null || before.Generation != after.Generation)
            return true;
        var first = before.State;
        var second = after.State;
        var left = first.Network;
        var right = second.Network;
        return first.Address != second.Address || first.NodeType != second.NodeType || first.Position != second.Position ||
            first.SectorId != second.SectorId || left?.BaseAreaId != right?.BaseAreaId || left?.SuppliedAreaId != right?.SuppliedAreaId ||
            (left?.Department ?? "general") != (right?.Department ?? "general") ||
            (left?.IsNtnetRelay ?? false) != (right?.IsNtnetRelay ?? false) ||
            (left?.IsStationLevel ?? true) != (right?.IsStationLevel ?? true) ||
            (left?.IsMiningLevel ?? false) != (right?.IsMiningLevel ?? false) || left?.InitialAnchor != right?.InitialAnchor;
    }

    /// <summary>Одна новая catalog revision с прежним ordered stack не требует повторной spatial публикации.</summary>
    private static bool SectorGeometryChanged(SectorFact? before, SectorFact? after) =>
        before is null || after is null ? before != after : !before.Levels.Equals(after.Levels);

    /// <summary>Закрывает graph admission немедленно; попытка публикации назначается один раз на новую structural revision.</summary>
    private void DirtyTopology()
    {
        if (_topologyRevision == ulong.MaxValue)
            throw new RoutingException("topology_revision_exhausted");
        _topologyRevision++;
        TopologyPending = true;
        TopologyError = null;
    }

    /// <summary>Одна bounded publication attempt; ошибка сохраняет исходные факты, но не допускает выдачу старых маршрутов.</summary>
    private void TryBuildTopology()
    {
        if (State != MirrorSyncState.Ready || !TopologyPending || _attemptedTopologyRevision == _topologyRevision)
            return;
        _attemptedTopologyRevision = _topologyRevision;
        try
        {
            _graph = _topology.Build(Nodes, Sectors, AppliedSequence);
            TopologyPending = false;
            TopologyError = null;
        }
        catch (TopologyException failure)
        {
            _graph = null;
            TopologyError = failure.Code;
        }
    }

    /// <summary>Возвращает текущий ACK; replay повторяет только безопасные command metadata и не повторяет domain effects.</summary>
    private RuntimeResult Result(ApplyStatus status, string code, ReplayEntry? reply = null) => new(
        new ApplyResult(status, code, AppliedSequence, SimulationTimeDs, Nodes.Count, _trackedCount), State,
        Graph?.Version ?? 0, TopologyPending, reply?.Admission, reply?.Action, reply?.Route, reply?.ErrorCode, CommunicationError,
        Graph?.InputSequence ?? 0, _availability.Revision, reply?.Anchor, _authority.InfrastructureStamp, _authority.AvailabilityInfrastructureStamp,
        _communicationPorts.Revision);

    /// <summary>Handoff читает текущий mirror revision: structural GraphSnapshot старше availability-only входов.</summary>
    private ulong CurrentNodeRevision(NodeKey key) =>
        Nodes.TryGetValue(key.NodeId, out var node) && node.Generation == key.Generation ? node.Revision : 0;

    /// <summary>Dirty/failed topology закрывает point queries; semantic sector failures не инвалидируют пригодный graph.</summary>
    private AnchorQueryResult ResolveAnchor(AnchorRequest request)
    {
        if (Graph is not { } graph) return new(request.Key, null, 0, 0, "topology_pending");
        try
        {
            var node = _topology.ResolveAnchor(graph, request.Position, request.SectorId, request.BaseAreaId);
            return new(request.Key, node, graph.Version, graph.InputSequence, node is null ? "not_found" : "found");
        }
        catch (TopologyException failure)
        {
            return new(request.Key, null, graph.Version, graph.InputSequence, failure.Code);
        }
    }

    /// <summary>Потеря полной frame identity закрывает и sync, и invocation admission; staged hash освобождается.</summary>
    private RuntimeResult Fail(string code)
    {
        _synchronization.Invalidate(code);
        TopologyPending = true;
        _graph = null;
        StopReadModels();
        _transport!.MarkUnavailable(SimulationTimeDs);
        return Result(ApplyStatus.ResyncRequired, code);
    }

    /// <summary>Полный accepted mirror fact обязан помещаться в согласованный directory cap без частичной индексации.</summary>
    private void IndexService(ServiceFact service)
    {
        if (_directory.Upsert(service) is not (DirectoryUpdateResult.Applied or DirectoryUpdateResult.Ignored))
            throw new InvalidOperationException("Accepted service cannot be indexed.");
    }

    /// <summary>Одна work lane не может получить больше выделенного ей остатка общего бюджета.</summary>
    private int AdvanceLane(int lane, int budget) => lane switch
    {
        0 => _transport!.Advance(SimulationTimeDs, budget),
        1 => _discovery?.Advance(budget) ?? 0,
        2 => _projections?.Advance(budget) ?? 0,
        _ => _graphSynchronization?.Advance(budget) ?? 0
    };

    /// <summary>Адаптирует read-only lookup page к общему event каналу без исполнения native handler.</summary>
    private bool TryLaneEvent(int lane, out TransportEvent? transportEvent)
    {
        if (lane == 0) return _transport!.TryDequeueEvent(out transportEvent);
        if (lane == 1 && _discovery is not null && _discovery.TryDequeueEvent(out var page))
        {
            transportEvent = page;
            return true;
        }
        if (lane == 2 && _projections is not null && _projections.TryDequeueEvent(out var projection))
        {
            transportEvent = projection;
            return true;
        }
        if (lane == 3 && _graphSynchronization is not null && _graphSynchronization.TryDequeueEvent(out var graphPage))
        {
            transportEvent = graphPage;
            return true;
        }
        if (lane == 4 && _serviceWatches is not null && _serviceWatches.TryDequeueEvent(out var watchEvent))
        {
            transportEvent = watchEvent;
            return true;
        }
        transportEvent = null;
        return false;
    }

    /// <summary>После потери binding незавершённые observations и retained pages освобождаются без replay в новую сессию.</summary>
    private void StopReadModels()
    {
        _discovery?.Dispose();
        _discovery = null;
        _projections?.Dispose();
        _projections = null;
        _graphSynchronization?.Dispose();
        _graphSynchronization = null;
        _directory.Reset();
        _serviceWatches?.Dispose();
        _serviceWatches = null;
    }

    /// <summary>Проверяет lifetime и single-owner reentrancy до любых изменений.</summary>
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy)
            throw new InvalidOperationException("Reentrant runtime access is not supported.");
        _busy = true;
    }
}
