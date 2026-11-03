using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Netrun.Core.Access;

namespace Netrun.Core.Entities;

/// <summary>
/// Последовательный BFS по native ACL-фактам. Сохраняет минимальное число hops существующей DM-навигации,
/// выполняет не более одной проверки соседнего узла за work unit и никогда не исполняет native-эффект.
/// </summary>
public sealed class EntityNavigationBroker : IDisposable
{
    /// <summary>Фазы сохраняют cursor между work units и output backpressure.</summary>
    private enum Phase { Authority, Search, Reconstruct, Publish }

    /// <summary>Один запрос и его временные структуры; после terminal остаются только bounded metadata.</summary>
    private sealed class Query
    {
        /// <summary>Исходный immutable body; raw facts освобождаются после построения authority индексов.</summary>
        internal required RequestKey Key;
        /// <summary>Поколение native сущности.</summary>
        internal required ActorKey Actor;
        /// <summary>Captured native authority revision.</summary>
        internal ulong ActorRevision;
        /// <summary>Точный исходный узел.</summary>
        internal required NodeKey Source;
        /// <summary>Точное назначение.</summary>
        internal required NodeKey Destination;
        /// <summary>Полная semantic identity, включая actor revision и все ACL-facts.</summary>
        internal required byte[] Fingerprint;
        /// <summary>Публикация удерживается слабо, устаревший поиск не сохраняет станционный граф.</summary>
        internal WeakReference<GraphSnapshot>? Graph;
        /// <summary>Захваченные версии графа и availability.</summary>
        internal ulong GraphVersion, InputSequence, AvailabilityVersion;
        /// <summary>Положительный следующий номер страницы.</summary>
        internal ulong NextPage = 1;
        /// <summary>Фаза вычисления authority, поиска и выдачи.</summary>
        internal Phase Phase;
        /// <summary>Core вычисляет authority по raw facts, не по готовому списку DM allowed nodes.</summary>
        internal EntityAccessAuthority? Authority;
        /// <summary>Raw snapshot сохраняется лишь до захвата Core graph, затем переходит incremental authority.</summary>
        internal EntityAccessSnapshot? Access;
        /// <summary>Visited и predecessor объединены; Source имеет null predecessor.</summary>
        internal Dictionary<NodeKey, NodeKey?> Previous = [];
        /// <summary>FIFO сохраняет BFS hop semantics.</summary>
        internal Queue<NodeKey> Frontier = [];
        /// <summary>Текущий узел и cursor его физических рёбер.</summary>
        internal NodeKey? Current;
        /// <summary>Следующее incident edge текущего узла.</summary>
        internal int EdgeCursor;
        /// <summary>Следующий quantum сосед; -1 означает, что bucket ещё не открыт текущим узлом.</summary>
        internal int RelayCursor = -1;
        /// <summary>Полный relay bucket расширяется только один раз за весь BFS.</summary>
        internal bool RelaysExpanded;
        /// <summary>Обратный путь строится по одному predecessor за work unit.</summary>
        internal List<NodeKey> ReversePath = [];
        /// <summary>Следующее поколение обратного пути.</summary>
        internal NodeKey? Reconstruct;
        /// <summary>Индекс следующего шага при выдаче пути в прямом порядке.</summary>
        internal int PublishCursor;
        /// <summary>O(1) round-robin removal.</summary>
        internal LinkedListNode<Query>? Schedule;
        /// <summary>O(1) отзыв единственной непрочитанной страницы.</summary>
        internal LinkedListNode<Pending>? Event;
        /// <summary>Terminal metadata сохраняется до фактического handoff.</summary>
        internal string? TerminalCode;
        /// <summary>Terminal состояние для duplicate admission.</summary>
        internal RequestState? TerminalState;
    }

    /// <summary>Связывает страницу с её единственным владельцем без копирования workspace.</summary>
    private sealed record Pending(Query Query, EntityNavigationPage Page);
    /// <summary>Replay история не удерживает native факты или route nodes.</summary>
    private sealed record History(byte[] Fingerprint, RequestState State, string Code);

    /// <summary>Непригодный pending граф представлен null, поиск до публикации не начинается.</summary>
    private readonly Func<GraphSnapshot?> _graph;
    /// <summary>Тот же O(1) overlay, который использует обычная маршрутизация.</summary>
    private readonly INetworkAvailability? _availability;
    /// <summary>Binding не меняется в течение lifetime broker.</summary>
    private readonly string _binding;
    /// <summary>Проверенные ограничения и размер страницы.</summary>
    private readonly EntityNavigationOptions _options;
    /// <summary>Slots живут до terminal handoff.</summary>
    private readonly Dictionary<RequestKey, Query> _active = [];
    /// <summary>Только готовые к работе запросы; ожидание чтения страницы не тратит work budget.</summary>
    private readonly LinkedList<Query> _schedule = [];
    /// <summary>Глобальная очередь, не больше одной страницы каждого запроса.</summary>
    private readonly LinkedList<Pending> _events = [];
    /// <summary>Ограниченное окно terminal metadata.</summary>
    private readonly Dictionary<RequestKey, History> _history = [];
    /// <summary>FIFO вытеснение metadata.</summary>
    private readonly Queue<RequestKey> _historyOrder = [];
    /// <summary>Не вытесняемый watermark не разрешает повтор эффекта после потери history.</summary>
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    /// <summary>Backpressure не позволяет следующему serial обогнать ещё не принятый.</summary>
    private readonly ProducerAdmissionFence _admission;
    /// <summary>Защита единственного последовательного владельца от callback reentrancy.</summary>
    private bool _busy;
    /// <summary>Завершённый binding больше не обслуживается.</summary>
    private bool _disposed;

    /// <summary>Slots, включая terminal страницы до handoff.</summary>
    public int ActiveCount => _active.Count;
    /// <summary>Количество готовых непрочитанных страниц.</summary>
    public int EventCount => _events.Count;
    /// <summary>Количество retained terminal fingerprints.</summary>
    public int HistoryCount => _history.Count;

    /// <summary>Создаёт pure broker без I/O, таймеров или фоновых потоков.</summary>
    public EntityNavigationBroker(Func<GraphSnapshot?> graph, string bindingEpoch,
        EntityNavigationOptions? options = null, INetworkAvailability? availability = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        _options = options ?? new();
        if (_options.MaxQueries < 1 || _options.MaxEvents < 1 || _options.PageRows is < 1 or > 64 ||
            _options.MaxNodes < 1 || _options.HistoryCapacity is < 1 or > 4096 || _options.MaxProducers < 1)
            throw new ArgumentException("Invalid navigation options.", nameof(options));
        _graph = graph;
        _binding = bindingEpoch;
        _availability = availability;
        _admission = new(_watermarks, _options.MaxProducers);
    }

    /// <summary>Принимает immutable запрос без inline BFS. would_block не расходует его serial.</summary>
    public TransportAdmission Submit(EntityNavigationRequest request)
    {
        Enter();
        try { return _admission.Complete(SubmitCore(request)); }
        finally { _busy = false; }
    }

    /// <summary>Проверяет replay прежде, чем резервировать workspace slot.</summary>
    private TransportAdmission SubmitCore(EntityNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidRequest(request)) return new(AdmissionStatus.Rejected, "invalid_request", request.Key);
        var fingerprint = Fingerprint(request)!;
        if (_active.TryGetValue(request.Key, out var active))
            return Same(active.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, active.TerminalCode ?? "duplicate",
                request.Key, active.TerminalState ?? RequestState.Queued) : new(AdmissionStatus.Rejected, "request_conflict", request.Key);
        if (_history.TryGetValue(request.Key, out var history))
            return Same(history.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, history.Code, request.Key, history.State) :
                new(AdmissionStatus.Rejected, "request_conflict", request.Key);
        if (_watermarks.TryGetValue(request.Key.ProducerId, out var watermark) && request.Key.Sequence <= watermark)
            return new(AdmissionStatus.Rejected, "request_expired", request.Key);
        if (_admission.Blocks(request.Key)) return new(AdmissionStatus.WouldBlock, "producer_backpressure", request.Key);
        if (!_watermarks.ContainsKey(request.Key.ProducerId) && _watermarks.Count >= _options.MaxProducers)
            return new(AdmissionStatus.WouldBlock, "producer_capacity", request.Key);
        if (_active.Count >= _options.MaxQueries) return new(AdmissionStatus.WouldBlock, "query_capacity", request.Key);
        var query = new Query { Key = request.Key, Actor = request.Actor, ActorRevision = request.ActorRevision, Source = request.Source, Destination = request.Destination, Fingerprint = fingerprint, Access = request.Access };
        query.Schedule = _schedule.AddLast(query);
        _active.Add(request.Key, query);
        _watermarks[request.Key.ProducerId] = request.Key.Sequence;
        return new(AdmissionStatus.Accepted, "accepted", request.Key, RequestState.Queued);
    }

    /// <summary>
    /// Выполняет не более workBudget units round-robin: один ACL insert, один сосед, один predecessor
    /// либо одна bounded output page. Ни один work unit не перечисляет степень узла или relay bucket целиком.
    /// </summary>
    public int Advance(int workBudget)
    {
        Enter();
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(workBudget);
            var work = 0;
            while (work < workBudget && _events.Count < _options.MaxEvents && _schedule.First is { } first)
            {
                var query = first.Value;
                var graph = _graph();
                if (graph is null && query.Graph is null) break;
                _schedule.Remove(first);
                _schedule.AddLast(first);
                if (query.Graph is null) Capture(query, graph!);
                if (graph is null || !Current(query, graph)) Fail(query, "navigation_changed", RequestState.Failed);
                else Step(query, graph);
                work++;
            }
            return work;
        }
        finally { _busy = false; }
    }

    /// <summary>Перед handoff повторно проверяет snapshot/overlay; partial путь отзывается одним пустым terminal.</summary>
    public bool TryDequeueEvent(out EntityNavigationPage? page)
    {
        Enter();
        try
        {
            if (_events.First is not { } first) { page = null; return false; }
            var pending = first.Value;
            var query = pending.Query;
            if (pending.Page.Code is "page" or "completed")
            {
                var graph = _graph();
                if (graph is null || !Current(query, graph))
                {
                    Fail(query, "navigation_changed", RequestState.Failed);
                    page = query.Event!.Value.Page;
                    RemoveEvent(query);
                    Retire(query);
                    return true;
                }
            }
            RemoveEvent(query);
            page = pending.Page;
            if (page.Complete) Retire(query);
            else query.Schedule = _schedule.AddLast(query);
            return true;
        }
        finally { _busy = false; }
    }

    /// <summary>Отзывает workspace и unread страницу; чужое output pressure возвращает would_block без частичной отмены.</summary>
    public TransportActionResult Cancel(RequestKey key)
    {
        Enter();
        try
        {
            if (!TransportFingerprint.ValidKey(key)) return new(false, "invalid_key");
            _admission.Release(key);
            if (!_active.TryGetValue(key, out var query)) return new(false, "request_unknown");
            if (query.Event is null && _events.Count >= _options.MaxEvents) return new(false, "would_block", true);
            Fail(query, "cancelled", RequestState.Cancelled);
            return new(true, "cancelled");
        }
        finally { _busy = false; }
    }

    /// <summary>Освобождает все запросы старого binding без выдачи новых событий.</summary>
    public void Dispose()
    {
        if (_busy) throw new InvalidOperationException("Navigation is already executing.");
        if (_disposed) return;
        _disposed = true;
        _active.Clear();
        _schedule.Clear();
        _events.Clear();
        _history.Clear();
        _historyOrder.Clear();
        _watermarks.Clear();
        _admission.Clear();
    }

    /// <summary>Единые shape bounds для direct runtime и codec, без чтения графа или native объектов.</summary>
    public static bool IsValidRequest(EntityNavigationRequest? request) => request is not null &&
        TransportFingerprint.ValidKey(request.Key) && request.Actor is { Generation: > 0 } &&
        NodeFactRules.IsValidText(request.Actor.Id, 128) && request.ActorRevision > 0 &&
        ValidNode(request.Source) && ValidNode(request.Destination) &&
        EntityAccessAuthority.IsValidSnapshot(request.Access);

    /// <summary>Канонический SHA-256 всех semantic полей; порядок ACL является частью входной identity.</summary>
    public static byte[]? Fingerprint(EntityNavigationRequest? request)
    {
        if (!IsValidRequest(request)) return null;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-ENTITY-NAVIGATION-1");
            writer.Write(request!.Key.ProducerId);
            writer.Write(request.Key.Sequence);
            writer.Write(request.Actor.Id);
            writer.Write(request.Actor.Generation);
            writer.Write(request.ActorRevision);
            WriteNode(writer, request.Source);
            WriteNode(writer, request.Destination);
            writer.Write(EntityAccessAuthority.Fingerprint(request.Access)!);
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    /// <summary>Захватывает версии за O(1); загрузка ACL и обход графа выполняются последующими work units.</summary>
    private void Capture(Query query, GraphSnapshot graph)
    {
        var publication = new WeakReference<GraphSnapshot>(graph);
        query.Graph = publication;
        query.Authority = new(query.Access!, key => publication.TryGetTarget(out var current) ? current.LanIds.GetValueOrDefault(key) : null);
        query.Access = null;
        query.GraphVersion = graph.Version;
        query.InputSequence = graph.InputSequence;
        query.AvailabilityVersion = _availability?.Revision ?? 0;
    }

    /// <summary>Любая смена exact snapshot либо overlay отзывает ещё не переданный read model.</summary>
    private bool Current(Query query, GraphSnapshot graph) => query.Graph is not null &&
        query.Graph.TryGetTarget(out var captured) && ReferenceEquals(graph, captured) &&
        graph.Version == query.GraphVersion && graph.InputSequence == query.InputSequence &&
        (_availability?.Revision ?? 0) == query.AvailabilityVersion;

    /// <summary>Продвигает только один cursor; native факты и мир здесь никогда не опрашиваются.</summary>
    private void Step(Query query, GraphSnapshot graph)
    {
        switch (query.Phase)
        {
            case Phase.Authority:
                if (!query.Authority!.Ready)
                {
                    query.Authority.Advance(1);
                    if (query.Authority.ErrorCode is { } error) Fail(query, error, RequestState.Failed);
                    return;
                }
                if (!graph.Nodes.ContainsKey(query.Source) || !graph.Nodes.ContainsKey(query.Destination))
                { Fail(query, "stale_endpoint", RequestState.Failed); return; }
                if (!Online(graph, query.Source) || !Online(graph, query.Destination) ||
                    query.Source != query.Destination && !query.Authority.CanTraverse(query.Destination, query.Source))
                { Fail(query, "unreachable", RequestState.Failed); return; }
                query.Previous.Add(query.Source, null);
                query.Frontier.Enqueue(query.Source);
                if (query.Source == query.Destination)
                { query.Phase = Phase.Publish; query.PublishCursor = -1; }
                else query.Phase = Phase.Search;
                return;
            case Phase.Search:
                SearchStep(query, graph);
                return;
            case Phase.Reconstruct:
                if (query.Reconstruct == query.Source)
                {
                    query.Phase = Phase.Publish;
                    query.PublishCursor = query.ReversePath.Count - 1;
                    query.Authority = null;
                    query.Previous = [];
                    query.Frontier = [];
                    return;
                }
                query.ReversePath.Add(query.Reconstruct!);
                query.Reconstruct = query.Previous[query.Reconstruct!];
                return;
            case Phase.Publish:
                Publish(query);
                return;
        }
    }

    /// <summary>Рассматривает одно физическое ребро либо один quantum сосед без materialized relay clique.</summary>
    private void SearchStep(Query query, GraphSnapshot graph)
    {
        if (query.Current is null)
        {
            if (!query.Frontier.TryDequeue(out var current)) { Fail(query, "unreachable", RequestState.Failed); return; }
            query.Current = current;
            query.EdgeCursor = 0;
            query.RelayCursor = -1;
            return;
        }
        if (graph.Adjacency.TryGetValue(query.Current, out var incident) && query.EdgeCursor < incident.Count)
        {
            var edge = incident[query.EdgeCursor++];
            Visit(query, graph, edge.From == query.Current ? edge.To : edge.From);
            return;
        }
        if (!query.RelaysExpanded && (_availability?.BackboneEnabled ?? true) &&
            graph.Nodes[query.Current].State.Position is not null &&
            graph.Nodes[query.Current].State.Network?.IsNtnetRelay == true && Availability(graph, query.Current).RelayOperational)
        {
            if (query.RelayCursor < 0) query.RelayCursor = 0;
            if (query.RelayCursor < graph.RelayBucket.Count)
            {
                var relay = graph.RelayBucket[query.RelayCursor++];
                if (Availability(graph, relay).RelayOperational) Visit(query, graph, relay);
                return;
            }
            query.RelaysExpanded = true;
        }
        query.Current = null;
    }

    /// <summary>Exact ACL/availability поколения проверяются до добавления predecessor и frontier.</summary>
    private void Visit(Query query, GraphSnapshot graph, NodeKey next)
    {
        if (query.Previous.ContainsKey(next) || !Online(graph, next) || !query.Authority!.CanTraverse(next, query.Source)) return;
        if (query.Previous.Count >= _options.MaxNodes) { Fail(query, "navigation_capacity", RequestState.Failed); return; }
        query.Previous.Add(next, query.Current);
        if (next == query.Destination)
        { query.Reconstruct = next; query.Phase = Phase.Reconstruct; return; }
        query.Frontier.Enqueue(next);
    }

    /// <summary>Публикует bounded страницу в прямом порядке и паркует запрос до её чтения.</summary>
    private void Publish(Query query)
    {
        var nodes = new List<NodeKey>(_options.PageRows);
        while (nodes.Count < _options.PageRows && query.PublishCursor >= 0)
            nodes.Add(query.ReversePath[query.PublishCursor--]);
        var complete = query.PublishCursor < 0;
        if (complete)
        {
            query.TerminalCode = "completed";
            query.TerminalState = RequestState.Completed;
            Stop(query);
        }
        else Pause(query);
        query.Event = _events.AddLast(new Pending(query, Page(query, nodes, complete, complete ? "completed" : "page")));
    }

    /// <summary>Единственная непрочитанная страница отзывается за O(1); workspace освобождается до terminal handoff.</summary>
    private void Fail(Query query, string code, RequestState state)
    {
        RemoveEvent(query);
        query.TerminalCode = code;
        query.TerminalState = state;
        Stop(query);
        query.Event = _events.AddLast(new Pending(query, Page(query, [], true, code)));
    }

    /// <summary>Создаёт страницу без ссылки на большой исходный ACL.</summary>
    private EntityNavigationPage Page(Query query, IEnumerable<NodeKey> nodes, bool complete, string code) =>
        new(_binding, query.Key, query.Actor, query.ActorRevision,
            query.Source, query.Destination, query.GraphVersion, query.InputSequence,
            query.AvailabilityVersion, query.NextPage++, nodes, complete, code);

    /// <summary>Удаляет round-robin node за O(1).</summary>
    private void Pause(Query query)
    {
        if (query.Schedule is { } schedule) _schedule.Remove(schedule);
        query.Schedule = null;
    }

    /// <summary>Прекращает поиск и освобождает все большие структуры, сохраняя только scalar query metadata.</summary>
    private void Stop(Query query)
    {
        Pause(query);
        query.Authority = null;
        query.Access = null;
        query.Previous = [];
        query.Frontier = [];
        query.ReversePath = [];
        query.Current = null;
        query.Reconstruct = null;
    }

    /// <summary>Удаляет единственную queued страницу владельца за O(1).</summary>
    private void RemoveEvent(Query query)
    {
        if (query.Event is { } pending) _events.Remove(pending);
        query.Event = null;
    }

    /// <summary>Освобождает slot, оставляя лишь bounded fingerprint и неизымаемый producer watermark.</summary>
    private void Retire(Query query)
    {
        Stop(query);
        _active.Remove(query.Key);
        _history.Add(query.Key, new(query.Fingerprint, query.TerminalState!.Value, query.TerminalCode!));
        _historyOrder.Enqueue(query.Key);
        while (_historyOrder.Count > _options.HistoryCapacity) _history.Remove(_historyOrder.Dequeue());
    }

    /// <summary>Availability неизвестного exact поколения всегда fail-closed.</summary>
    private NodeAvailability Availability(GraphSnapshot graph, NodeKey key) => _availability?.Get(key) ??
        (graph.Nodes.TryGetValue(key, out var fact) ? new(fact.State.Online, fact.State.Network?.RelayOperational ?? true) : new(false, false, false));

    /// <summary>Обычный вход требует и наличия, и online каждого узла.</summary>
    private bool Online(GraphSnapshot graph, NodeKey key)
    {
        var state = Availability(graph, key);
        return state.Present && state.Online;
    }

    /// <summary>Проверяет bounded opaque identity exact поколения.</summary>
    private static bool ValidNode(NodeKey? key) => key is { Generation: > 0 } && NodeFactRules.IsValidText(key.NodeId, 128);

    /// <summary>Кодирует обе части key без неоднозначных разделителей.</summary>
    private static void WriteNode(BinaryWriter writer, NodeKey key) { writer.Write(key.NodeId); writer.Write(key.Generation); }

    /// <summary>Сравнивает deterministic fingerprints фиксированной длины.</summary>
    private static bool Same(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>Проверяет lifetime и запрещает повторный вход через provider callback.</summary>
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy) throw new InvalidOperationException("Navigation is already executing.");
        _busy = true;
    }
}
