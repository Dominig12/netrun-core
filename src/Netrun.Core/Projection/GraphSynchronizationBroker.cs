using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Projection;

/// <summary>
/// Один persistent graph stream: ACKed baseline и один frozen target, без restart на каждом leaf move.
/// Native применяет максимум 32 operations, затем explicit Continue подтверждает именно application commit.
/// </summary>
public sealed class GraphSynchronizationBroker : IDisposable
{
    /// <summary>Address-independent identity physical edge; NodeKey order сохраняет поколения и не зависит от rename.</summary>
    private readonly record struct EdgeKey(NodeKey First, NodeKey Second, PhysicalLinkKind Kind);
    /// <summary>Bounded terminal record без graph, rows или старых target snapshots.</summary>
    private sealed record History(RequestState State, string Code, ulong AcknowledgedPage);

    /// <summary>Последняя пригодная runtime publication; null паркует capture нового batch.</summary>
    private readonly Func<GraphSnapshot?> _graph;
    /// <summary>Отдельные infrastructure stamps и current world node revisions.</summary>
    private readonly IGraphAuthority _authority;
    /// <summary>Идентичность единственного владельца read-model stream.</summary>
    private readonly string _binding;
    /// <summary>Явные hard caps baseline/target, страниц и replay metadata.</summary>
    private readonly GraphSynchronizationOptions _options;
    /// <summary>Native-ACKed node rows, максимум MaxNodes.</summary>
    private readonly Dictionary<NodeKey, GraphSynchronizationNodeUpsert> _baselineNodes = [];
    /// <summary>Native-ACKed physical edge rows, максимум MaxEdges.</summary>
    private readonly Dictionary<EdgeKey, GraphSynchronizationEdgeUpsert> _baselineEdges = [];
    /// <summary>Frozen target node values; неизменившиеся записи разделяют immutable baseline objects.</summary>
    private readonly Dictionary<NodeKey, GraphSynchronizationNodeUpsert> _targetNodes = [];
    /// <summary>Frozen target physical edges; нет третьего latest-map или event history.</summary>
    private readonly Dictionary<EdgeKey, GraphSynchronizationEdgeUpsert> _targetEdges = [];
    /// <summary>Rename требует explicit incident edge_upsert, чтобы native address views обновлялись bounded operations.</summary>
    private readonly HashSet<NodeKey> _renamedNodes = [];
    /// <summary>Net diff одного frozen batch; максимум сумма baseline+target rows, transient intermediate graphs не сохраняются.</summary>
    private readonly List<GraphSynchronizationOperation> _operations = [];
    /// <summary>Terminal dedup window сохраняет только status и последний explicit page ACK.</summary>
    private readonly Dictionary<RequestKey, History> _history = [];
    /// <summary>FIFO bounded terminal window.</summary>
    private readonly Queue<RequestKey> _historyOrder = [];
    /// <summary>Забытый старый producer serial нельзя принять повторно после history eviction.</summary>
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    /// <summary>Один принятый stream; terminal slot освобождается после ACK terminal страницы.</summary>
    private RequestKey? _key;
    /// <summary>Единственная retained structural publication target; baseline не удерживает GraphSnapshot.</summary>
    private GraphSnapshot? _targetGraph;
    /// <summary>Frozen target stamps и отдельная world read-model revision.</summary>
    private ulong _targetInfrastructure, _targetAvailability, _targetRevision;
    /// <summary>Подтверждённые metadata полного baseline batch.</summary>
    private ulong _baselineGraphVersion, _baselineInputSequence, _baselineRevision;
    /// <summary>Baseline появляется только после ACK последней страницы первоначального snapshot.</summary>
    private bool _initialized;
    /// <summary>Следующая operation frozen diff; cursor никогда не считается native ACK.</summary>
    private int _operationCursor;
    /// <summary>Положительный page serial за stream без повторного использования отозванных номеров.</summary>
    private ulong _pageSerial;
    /// <summary>Последняя подтверждённая page.</summary>
    private ulong _acknowledgedPage;
    /// <summary>Ровно одна immutable page до explicit native application ACK.</summary>
    private GraphSynchronizationPage? _inflight;
    /// <summary>Событие ещё не передано Host journal.</summary>
    private bool _eventPending;
    /// <summary>Continue до фактического handoff не может подтверждать невыданные данные.</summary>
    private bool _delivered;
    /// <summary>Terminal отказ/отмена после ACK освобождает stream slot и остаётся только в bounded history.</summary>
    private RequestState? _terminal;
    /// <summary>Безопасная terminal причина без native exception или входных значений.</summary>
    private string? _terminalCode;
    /// <summary>Single-owner reentrancy guard.</summary>
    private bool _busy;
    /// <summary>Disposed binding не принимает новые commands.</summary>
    private bool _disposed;

    /// <summary>Один active stream либо ноль, включая terminal page до ACK.</summary>
    public int ActiveCount => _key is null ? 0 : 1;
    /// <summary>Один unread event либо ноль; delivered-but-unACKed page остаётся in-flight.</summary>
    public int EventCount => _eventPending ? 1 : 0;
    /// <summary>Число node+edge rows именно native-ACKed baseline.</summary>
    public int BaselineRowCount => _baselineNodes.Count + _baselineEdges.Count;
    /// <summary>Число rows одного frozen target; после terminal/cancel равно нулю.</summary>
    public int TargetRowCount => _targetNodes.Count + _targetEdges.Count;
    /// <summary>Текущий bounded terminal replay window.</summary>
    public int HistoryCount => _history.Count;
    /// <summary>Не вытесняемые producer barriers.</summary>
    public int ProducerCount => _watermarks.Count;

    /// <summary>Фиксирует зависимости без capture/scan; первое продвижение выполняет bounded full diff вне native потока.</summary>
    public GraphSynchronizationBroker(Func<GraphSnapshot?> graph, IGraphAuthority authority, string bindingEpoch,
        GraphSynchronizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(authority);
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        _options = options ?? new();
        if (_options.MaxNodes is < 1 or > 10_000 || _options.MaxEdges is < 0 or > 100_000 ||
            _options.PageOperations is < 1 or > 32 || _options.HistoryCapacity is < 1 or > 4096 || _options.MaxProducers is < 1 or > 128)
            throw new ArgumentException("Invalid graph synchronization options.", nameof(options));
        _graph = graph;
        _authority = authority;
        _binding = bindingEpoch;
    }

    /// <summary>Резервирует один persistent stream; producer serial не расходуется при would_block.</summary>
    public TransportAdmission Start(RequestKey key)
    {
        Enter();
        try
        {
            if (!TransportFingerprint.ValidKey(key)) return new(AdmissionStatus.Rejected, "invalid_key", key);
            if (_key == key) return new(AdmissionStatus.Duplicate, _terminalCode ?? "duplicate", key, _terminal ?? RequestState.Queued);
            if (_history.TryGetValue(key, out var history)) return new(AdmissionStatus.Duplicate, history.Code, key, history.State);
            if (_watermarks.TryGetValue(key.ProducerId, out var serial) && key.Sequence <= serial)
                return new(AdmissionStatus.Rejected, "request_expired", key);
            if (_key is not null) return new(AdmissionStatus.WouldBlock, "stream_capacity", key);
            if (!_watermarks.ContainsKey(key.ProducerId) && _watermarks.Count == _options.MaxProducers)
                return new(AdmissionStatus.WouldBlock, "producer_capacity", key);
            ClearRows();
            _key = key;
            _watermarks[key.ProducerId] = key.Sequence;
            _pageSerial = 0;
            _acknowledgedPage = 0;
            _terminal = null;
            _terminalCode = null;
            return new(AdmissionStatus.Accepted, "accepted", key, RequestState.Queued);
        }
        finally { _busy = false; }
    }

    /// <summary>Готовит максимум одну 32-op page; unACKed page применяет backpressure без потери target/diff cursor.</summary>
    public int Advance(int workBudget)
    {
        Enter();
        try
        {
            if (workBudget <= 0 || _key is null || _inflight is not null) return 0;
            if (_targetGraph is null)
            {
                var graph = _graph();
                if (graph is null) return 0;
                if (_initialized && graph.Version == _baselineGraphVersion && graph.InputSequence == _baselineInputSequence &&
                    _authority.Revision == _baselineRevision) return 0;
                try { Capture(graph); }
                catch (RoutingException failure) { Terminal(failure.Code, RequestState.Failed); return 1; }
            }
            Publish();
            return 1;
        }
        finally { _busy = false; }
    }

    /// <summary>Выдаёт page один раз в Host journal; исторические строки не отменяются из-за движения новой latest publication.</summary>
    public bool TryDequeueEvent(out GraphSynchronizationPage? page)
    {
        Enter();
        try
        {
            if (!_eventPending) { page = null; return false; }
            page = _inflight;
            _eventPending = false;
            _delivered = true;
            return true;
        }
        finally { _busy = false; }
    }

    /// <summary>Применяет только фактически выданную page к baseline; duplicate ACK не повторяет mutation.</summary>
    public TransportActionResult Continue(RequestKey key, ulong pageIndex)
    {
        Enter();
        try
        {
            if (!TransportFingerprint.ValidKey(key) || pageIndex == 0) return new(false, "invalid_page_ack");
            if (_key != key)
                return _history.TryGetValue(key, out var history) && pageIndex <= history.AcknowledgedPage ?
                    new(true, "duplicate") : new(false, "request_unknown");
            if (pageIndex <= _acknowledgedPage) return new(true, "duplicate");
            if (_inflight is null || pageIndex != _inflight.PageIndex) return new(false, "page_unknown");
            if (!_delivered) return new(false, "page_not_delivered");
            var page = _inflight;
            foreach (var operation in page.Operations) ApplyBaseline(operation);
            _acknowledgedPage = pageIndex;
            _inflight = null;
            _delivered = false;
            if (_terminal is { } terminal)
            {
                Remember(key, new(terminal, _terminalCode!, _acknowledgedPage));
                _key = null;
                _terminal = null;
                _terminalCode = null;
            }
            else if (page.BatchComplete)
            {
                _baselineGraphVersion = page.GraphVersion;
                _baselineInputSequence = page.GraphInputSequence;
                _baselineRevision = _targetRevision;
                _initialized = true;
                ClearTarget();
            }
            return new(true, "acknowledged");
        }
        finally { _busy = false; }
    }

    /// <summary>Отзывает in-flight read model без нового binding; target освобождается немедленно, slot — после cancelled page ACK.</summary>
    public TransportActionResult Cancel(RequestKey key)
    {
        Enter();
        try
        {
            if (!TransportFingerprint.ValidKey(key)) return new(false, "invalid_key");
            if (_key != key) return _history.TryGetValue(key, out var history) && history.State == RequestState.Cancelled ?
                new(true, "duplicate") : new(false, "request_unknown");
            if (_terminal == RequestState.Cancelled) return new(true, "duplicate");
            Terminal("cancelled", RequestState.Cancelled);
            return new(true, "cancelled");
        }
        finally { _busy = false; }
    }

    /// <summary>Очищает все row/cursor references, history и barriers владельца.</summary>
    public void Dispose()
    {
        if (_busy) throw new InvalidOperationException("Graph synchronization is already executing.");
        if (_disposed) return;
        _disposed = true;
        ClearRows();
        _inflight = null;
        _eventPending = false;
        _key = null;
        _history.Clear();
        _historyOrder.Clear();
        _watermarks.Clear();
    }

    /// <summary>Один bounded full comparison строит только net diff, не отправляя всю станцию повторно при одном leaf move.</summary>
    private void Capture(GraphSnapshot graph)
    {
        if (graph.Nodes.Count > _options.MaxNodes || graph.PhysicalEdges.Count > _options.MaxEdges)
            throw new RoutingException("graph_sync_capacity");
        var revision = _authority.Revision;
        var infrastructure = _authority.InfrastructureStamp;
        var availability = _authority.AvailabilityInfrastructureStamp;
        if (infrastructure == 0 || availability == 0) throw new RoutingException("graph_sync_unavailable");
        _targetGraph = graph;
        _targetRevision = revision;
        _targetInfrastructure = infrastructure;
        _targetAvailability = availability;
        foreach (var (key, fact) in graph.Nodes)
        {
            var nodeRevision = _authority.NodeRevision(key);
            if (nodeRevision == 0) throw new RoutingException("graph_sync_state_changed");
            var endpoint = _authority.IsEndpoint(key);
            graph.Bindings.TryGetValue(key, out var anchor);
            graph.LanIds.TryGetValue(key, out var lan);
            var relay = fact.State.Position is not null && fact.State.Network?.IsNtnetRelay == true;
            if (_baselineNodes.TryGetValue(key, out var previous) && previous.Node.Address != fact.State.Address) _renamedNodes.Add(key);
            if (_baselineNodes.TryGetValue(key, out var old) && old.NodeRevision == nodeRevision && old.IsEndpoint == endpoint &&
                old.Node.Address == fact.State.Address && old.Node.Anchor == anchor && old.Node.LanId == lan && old.Node.IsRelay == relay)
                _targetNodes.Add(key, old);
            else _targetNodes.Add(key, new(new(key, fact.State.Address, anchor, lan, relay), nodeRevision, endpoint));
        }
        foreach (var edge in graph.PhysicalEdges)
        {
            var key = Identity(edge);
            if (_baselineEdges.TryGetValue(key, out var old) && old.Edge == edge &&
                !_renamedNodes.Contains(edge.From) && !_renamedNodes.Contains(edge.To)) _targetEdges.Add(key, old);
            else _targetEdges.Add(key, new(old?.EdgeId ?? EdgeId(key), edge));
        }
        if (_authority.Revision != revision) throw new RoutingException("graph_sync_state_changed");
        // Deletes precede additions; all node_upsert rows precede edge_upsert even when phases cross page boundaries.
        foreach (var (key, edge) in _baselineEdges)
            if (!_targetEdges.ContainsKey(key)) Add(new GraphSynchronizationEdgeRemove(edge.EdgeId));
        foreach (var key in _baselineNodes.Keys)
            if (!_targetNodes.ContainsKey(key)) Add(new GraphSynchronizationNodeRemove(key));
        foreach (var node in _targetNodes.Values.Where(value => !_baselineNodes.TryGetValue(value.Node.Key, out var old) || !ReferenceEquals(old, value))
            .OrderBy(value => value.Node.Address, StringComparer.Ordinal)) Add(node);
        foreach (var edge in graph.PhysicalEdges)
        {
            var key = Identity(edge);
            var target = _targetEdges[key];
            if (!_baselineEdges.TryGetValue(key, out var old) || !ReferenceEquals(old, target)) Add(target);
        }
    }

    /// <summary>Ограничивает diff до union baseline/target rows до роста list.</summary>
    private void Add(GraphSynchronizationOperation operation)
    {
        if (_operations.Count >= 2 * (_options.MaxNodes + _options.MaxEdges)) throw new RoutingException("graph_sync_capacity");
        _operations.Add(operation);
    }

    /// <summary>Cursor движется только по frozen plan; baseline остаётся прежним до explicit Continue.</summary>
    private void Publish()
    {
        var count = Math.Min(_options.PageOperations, _operations.Count - _operationCursor);
        var rows = _operations.GetRange(_operationCursor, count);
        _operationCursor += count;
        var complete = _operationCursor == _operations.Count;
        SetPage(new(_binding, _key!, NextPage(), _initialized ? GraphSynchronizationPhase.Delta : GraphSynchronizationPhase.Snapshot,
            _baselineGraphVersion, _targetGraph!.Version, _targetGraph.InputSequence, _targetInfrastructure, _targetAvailability,
            rows, complete, complete ? "completed" : "page"));
    }

    /// <summary>Применяет ACKed operations к baseline за O(page), без копирования всего graph на каждой странице.</summary>
    private void ApplyBaseline(GraphSynchronizationOperation operation)
    {
        switch (operation)
        {
            case GraphSynchronizationNodeUpsert node: _baselineNodes[node.Node.Key] = node; break;
            case GraphSynchronizationNodeRemove node: _baselineNodes.Remove(node.Node); break;
            case GraphSynchronizationEdgeUpsert edge: _baselineEdges[Identity(edge.Edge)] = edge; break;
            case GraphSynchronizationEdgeRemove edge:
                // Deletes уже находятся в frozen target plan; bounded page использует отдельный O(1) ID index ниже.
                _baselineEdges.Remove(_edgeIds[edge.EdgeId]);
                _edgeIds.Remove(edge.EdgeId);
                break;
        }
        if (operation is GraphSynchronizationEdgeUpsert upsert) _edgeIds[upsert.EdgeId] = Identity(upsert.Edge);
    }

    /// <summary>O(1) reverse delete index содержит только baseline edges и ограничен MaxEdges.</summary>
    private readonly Dictionary<string, EdgeKey> _edgeIds = new(StringComparer.Ordinal);

    /// <summary>Terminal page не удерживает target или baseline; следующие данные требуют нового stream key.</summary>
    private void Terminal(string code, RequestState state)
    {
        var graphVersion = _targetGraph?.Version ?? _baselineGraphVersion;
        var input = _targetGraph?.InputSequence ?? _baselineInputSequence;
        var infrastructure = _targetGraph is null ? _authority.InfrastructureStamp : _targetInfrastructure;
        var availability = _targetGraph is null ? _authority.AvailabilityInfrastructureStamp : _targetAvailability;
        var phase = _initialized ? GraphSynchronizationPhase.Delta : GraphSynchronizationPhase.Snapshot;
        var from = _baselineGraphVersion;
        ClearRows();
        _terminal = state;
        _terminalCode = code;
        SetPage(new(_binding, _key!, NextPage(), phase, from, graphVersion, input, infrastructure, availability, [], true, code));
    }

    /// <summary>Запоминает единственную page, которая не может потеряться при output pressure.</summary>
    private void SetPage(GraphSynchronizationPage page)
    {
        _inflight = page;
        _eventPending = true;
        _delivered = false;
    }

    /// <summary>Выдаёт новый serial до page publication; wrap запрещён.</summary>
    private ulong NextPage()
    {
        if (_pageSerial == ulong.MaxValue) throw new RoutingException("graph_sync_page_exhausted");
        return ++_pageSerial;
    }

    /// <summary>Освобождает единственный pinned target после batch ACK или terminal; dictionaries переиспользуют bounded capacity.</summary>
    private void ClearTarget()
    {
        _targetGraph = null;
        _targetNodes.Clear();
        _targetEdges.Clear();
        _renamedNodes.Clear();
        _operations.Clear();
        _operationCursor = 0;
    }

    /// <summary>Удаляет graph data без изменения producer barriers или page serial.</summary>
    private void ClearRows()
    {
        ClearTarget();
        _baselineNodes.Clear();
        _baselineEdges.Clear();
        _edgeIds.Clear();
        _initialized = false;
        _baselineGraphVersion = 0;
        _baselineInputSequence = 0;
        _baselineRevision = 0;
    }

    /// <summary>History eviction не снимает serial watermark.</summary>
    private void Remember(RequestKey key, History history)
    {
        _history.Add(key, history);
        _historyOrder.Enqueue(key);
        while (_historyOrder.Count > _options.HistoryCapacity) _history.Remove(_historyOrder.Dequeue());
    }

    /// <summary>Canonical endpoint ordering основан на ID/generation, а не адресах и не orientation Edge payload.</summary>
    private static EdgeKey Identity(PhysicalEdge edge)
    {
        var first = edge.From;
        var second = edge.To;
        var order = StringComparer.Ordinal.Compare(first.NodeId, second.NodeId);
        if (order > 0 || order == 0 && first.Generation > second.Generation) (first, second) = (second, first);
        return new(first, second, edge.Kind);
    }

    /// <summary>Непрозрачный fixed-size ID исключает delimiter injection из произвольного Unicode NodeId.</summary>
    private static string EdgeId(EdgeKey key)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-GRAPH-EDGE-1");
            writer.Write((int)key.Kind);
            writer.Write(key.First.NodeId);
            writer.Write(key.First.Generation);
            writer.Write(key.Second.NodeId);
            writer.Write(key.Second.Generation);
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    /// <summary>Один последовательный writer исключает callback reentrancy и использование disposed binding.</summary>
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy) throw new InvalidOperationException("Graph synchronization is already executing.");
        _busy = true;
    }
}
