using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Projection;

/// <summary>
/// Последовательный bounded broker одноразовых UI таблиц. Полные routes вычисляются одним source tree,
/// строки выдаются небольшими slices, а смена публикации отзывает незавершённую клиентскую таблицу.
/// </summary>
public sealed class ProjectionBroker : IDisposable
{
    /// <summary>Один bounded cursor; structural snapshot удерживается только weak-ссылкой.</summary>
    private sealed class Query
    {
        /// <summary>Исходный immutable semantic body для корреляции и позднего отзыва.</summary>
        internal required ProjectionRequest Request;
        /// <summary>Детерминированная identity содержимого без world revisions.</summary>
        internal required byte[] Fingerprint;
        /// <summary>Weak identity публикации; одинаковые номера разных snapshots не считаются одной таблицей.</summary>
        internal WeakReference<GraphSnapshot>? Graph;
        /// <summary>Версия structural публикации при первом work unit.</summary>
        internal ulong GraphVersion;
        /// <summary>Граница входного журнала structural публикации.</summary>
        internal ulong InputSequence;
        /// <summary>Захваченная ревизия overlay, стабильная для всех страниц запроса.</summary>
        internal ulong AvailabilityVersion;
        /// <summary>Captured global proofs для revised Routes без зависимости от unrelated graph publication.</summary>
        internal ulong InfrastructureStamp, AvailabilityInfrastructureStamp;
        /// <summary>Source endpoint proof; source movement отзывает всё дерево, target movement — только соответствующую row authority.</summary>
        internal ulong SourceNodeRevision;
        /// <summary>World revisions sampled AT solve; page generation никогда не relabel-ит старый путь current revision.</summary>
        internal readonly Dictionary<NodeKey, ulong> NodeRevisions = [];
        /// <summary>Собственные address-sorted keys, без полных facts или ссылок на GraphSnapshot.</summary>
        internal NodeKey[] Keys = [];
        /// <summary>Одно immutable дерево Routes до окончания генерации строк; Graph его не использует.</summary>
        internal SourceRouteTree? Tree;
        /// <summary>Ровно число уже учтённых cursor keys и tree entries; early failure не уменьшает чужой memory quota.</summary>
        internal int RetainedNodes;
        /// <summary>Следующий NodeKey для GraphNodeRow или RouteRow.</summary>
        internal int NodeCursor;
        /// <summary>Следующий физический кабель после всех узлов Graph.</summary>
        internal int EdgeCursor;
        /// <summary>Следующий положительный номер страницы; отозванные unread страницы не переиспользуют номера.</summary>
        internal ulong NextPageIndex = 1;
        /// <summary>O(1) удаление query из round-robin без stale scheduler entries.</summary>
        internal LinkedListNode<Query>? Schedule;
        /// <summary>Terminal состояние после enqueue последней страницы; slot жив до её handoff.</summary>
        internal RequestState? TerminalState;
        /// <summary>Безопасный terminal code, возвращаемый duplicate admission до handoff.</summary>
        internal string? TerminalCode;
    }

    /// <summary>Pending страница с единственным bounded query context; сама страница не удерживает workspace.</summary>
    private sealed record PendingPage(Query Query, ProjectionPage Page);
    /// <summary>Terminal replay record содержит лишь fingerprint и скалярный результат.</summary>
    private sealed record History(byte[] Fingerprint, RequestState State, string Code);

    /// <summary>Null publication паркует работу и готовые успешные страницы.</summary>
    private readonly Func<GraphSnapshot?> _graph;
    /// <summary>Общий bounded source-tree cache без повторных pair запросов.</summary>
    private readonly SourceRouteTreeProvider _trees;
    /// <summary>Optional O(1) ревизия того же overlay, который использует source-tree provider.</summary>
    private readonly Func<ulong>? _availabilityRevision;
    /// <summary>Optional fine-grained authority; null сохраняет прежний strict graph/AV контракт API.</summary>
    private readonly IGraphAuthority? _authority;
    /// <summary>Binding всех выдаваемых read-only событий.</summary>
    private readonly string _binding;
    /// <summary>Проверенные верхние границы queries, страниц, cursor keys и replay metadata.</summary>
    private readonly ProjectionOptions _options;
    /// <summary>Принятые запросы до handoff terminal страницы, включая уже сгенерированные таблицы.</summary>
    private readonly Dictionary<RequestKey, Query> _active = [];
    /// <summary>Round-robin только ещё генерирующих запросов; каждый slice не превышает PageRows.</summary>
    private readonly LinkedList<Query> _schedule = [];
    /// <summary>Глобальная bounded очередь immutable страниц.</summary>
    private readonly LinkedList<PendingPage> _events = [];
    /// <summary>Ограниченное окно terminal fingerprints; payload и таблицы сюда не попадают.</summary>
    private readonly Dictionary<RequestKey, History> _history = [];
    /// <summary>FIFO вытеснение истории за O(1).</summary>
    private readonly Queue<RequestKey> _historyOrder = [];
    /// <summary>Максимальный принятый serial producer за binding; не вытесняется вместе с history.</summary>
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    /// <summary>Один blocked head на producer сохраняет порядок admission через независимые input frames.</summary>
    private readonly ProducerAdmissionFence _admission;
    /// <summary>Число cursor keys и tree entries живых workspace; output rows учитываются отдельно.</summary>
    private int _retainedNodes;
    /// <summary>Запрет повторного входа через внешние callbacks; объект обслуживается одним владельцем.</summary>
    private bool _busy;
    /// <summary>Закрытый binding больше не принимает work или запросы.</summary>
    private bool _disposed;

    /// <summary>Query slots остаются занятыми до фактического handoff terminal.</summary>
    public int ActiveCount => _active.Count;
    /// <summary>Количество страниц, ожидающих потребителя; не превышает MaxEvents.</summary>
    public int EventCount => _events.Count;
    /// <summary>Количество cursor keys, tree entries и optional revision proofs; не превышает 3 * MaxQueries * MaxNodes.</summary>
    public int RetainedNodeCount => _retainedNodes;
    /// <summary>Количество terminal replay records в bounded окне.</summary>
    public int HistoryCount => _history.Count;
    /// <summary>Число зарегистрированных producers, включая blocked identity с watermark ноль.</summary>
    public int ProducerCount => _watermarks.Count;

    /// <summary>
    /// Фиксирует зависимости без I/O или фоновых задач. Runtime передаёт availabilityRevision для O(1) отзыва route таблиц.
    /// Без accessor Graph содержит только structural facts с ревизией ноль, а Routes проверяет ревизию через общий tree cache.
    /// </summary>
    public ProjectionBroker(Func<GraphSnapshot?> graph, SourceRouteTreeProvider trees, string bindingEpoch,
        ProjectionOptions? options = null, Func<ulong>? availabilityRevision = null, IGraphAuthority? authority = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(trees);
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        _options = options ?? new();
        if (_options.MaxQueries < 1 || _options.PageRows is < 1 or > 64 ||
            _options.MaxEvents < 1 || _options.HistoryCapacity is < 1 or > 4096 ||
            _options.MaxProducers < 1 || _options.MaxNodes < 1 ||
            _options.MaxEdges < 0)
            throw new ArgumentException("Invalid projection options.", nameof(options));
        _graph = graph;
        _trees = trees;
        _binding = bindingEpoch;
        _admission = new(_watermarks, _options.MaxProducers);
        _availabilityRevision = availabilityRevision;
        _authority = authority;
    }

    /// <summary>Резервирует bounded query slot без inline source-tree поиска; would_block не потребляет producer serial.</summary>
    public TransportAdmission Submit(ProjectionRequest request)
    {
        Enter();
        try { return _admission.Complete(SubmitCore(request)); }
        finally { _busy = false; }
    }

    /// <summary>Проверяет replay/fence перед резервированием domain capacity.</summary>
    private TransportAdmission SubmitCore(ProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidRequest(request)) return new(AdmissionStatus.Rejected, "invalid_request", request.Key);
        var fingerprint = Fingerprint(request);
        if (_active.TryGetValue(request.Key, out var active))
            return Same(active.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, active.TerminalCode ?? "duplicate",
                request.Key, active.TerminalState ?? RequestState.Queued) : new(AdmissionStatus.Rejected, "request_conflict", request.Key);
        if (_history.TryGetValue(request.Key, out var history))
            return Same(history.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, history.Code, request.Key, history.State) :
                new(AdmissionStatus.Rejected, "request_conflict", request.Key);
        if (_watermarks.TryGetValue(request.Key.ProducerId, out var watermark) && request.Key.Sequence <= watermark)
            return new(AdmissionStatus.Rejected, "request_expired", request.Key);
        if (_admission.Blocks(request.Key)) return new(AdmissionStatus.WouldBlock, "producer_backpressure", request.Key);
        if (!_watermarks.ContainsKey(request.Key.ProducerId) && _watermarks.Count == _options.MaxProducers)
            return new(AdmissionStatus.WouldBlock, "producer_capacity", request.Key);
        if (_active.Count == _options.MaxQueries)
            return new(AdmissionStatus.WouldBlock, "query_capacity", request.Key);
        var query = new Query { Request = request, Fingerprint = fingerprint };
        query.Schedule = _schedule.AddLast(query);
        _active.Add(request.Key, query);
        _watermarks[request.Key.ProducerId] = request.Key.Sequence;
        return new(AdmissionStatus.Accepted, "accepted", request.Key, RequestState.Queued);
    }

    /// <summary>
    /// Выполняет не более workBudget page slices; каждый выдаёт максимум PageRows строк.
    /// Первый slice дополнительно строит одно полное source tree либо сортирует bounded NodeKeys.
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
                var graph = _graph();
                if (graph is null) break;
                var query = first.Value;
                _schedule.Remove(first);
                _schedule.AddLast(first);
                if (query.Graph is null)
                {
                    if (!Capture(query, graph)) { work++; continue; }
                }
                if (!Current(query, graph))
                {
                    Fail(query, "projection_changed", RequestState.Failed);
                    work++;
                    continue;
                }
                Publish(query, graph);
                work++;
            }
            return work;
        }
        finally { _busy = false; }
    }

    /// <summary>Перед handoff проверяет publication identity и overlay revision; stale unread страницы заменяет одним пустым terminal.</summary>
    public bool TryDequeueEvent(out ProjectionPage? page)
    {
        Enter();
        try
        {
            var scan = _events.Count;
            while (scan-- > 0 && _events.First is { } first)
            {
                var pending = first.Value;
                if (pending.Page.Code is "page" or "completed")
                {
                    var graph = _graph();
                    if (graph is null)
                    {
                        _events.Remove(first);
                        _events.AddLast(first);
                        continue;
                    }
                    if (!Current(pending.Query, graph))
                    {
                        RemovePages(pending.Query.Request.Key);
                        page = Terminal(pending.Query, "projection_changed", RequestState.Failed);
                        Retire(pending.Query);
                        return true;
                    }
                }
                _events.Remove(first);
                page = pending.Page;
                if (page.Complete) Retire(pending.Query);
                return true;
            }
            page = null;
            return false;
        }
        finally { _busy = false; }
    }

    /// <summary>Отзывает unread страницы и возвращает один cancelled terminal; при чужом output pressure операция не мутирует query.</summary>
    public TransportActionResult Cancel(RequestKey key)
    {
        Enter();
        try
        {
            if (!TransportFingerprint.ValidKey(key)) return new(false, "invalid_key");
            _admission.Release(key);
            if (!_active.TryGetValue(key, out var query)) return new(false, "request_unknown");
            if (_events.Count == _options.MaxEvents && !_events.Any(pending => pending.Query.Request.Key == key))
                return new(false, "would_block", true);
            Fail(query, "cancelled", RequestState.Cancelled);
            return new(true, "cancelled");
        }
        finally { _busy = false; }
    }

    /// <summary>Освобождает все cursor keys, страницы и replay metadata binding без выдачи новых событий.</summary>
    public void Dispose()
    {
        if (_busy) throw new InvalidOperationException("Projection is already executing.");
        if (_disposed) return;
        _disposed = true;
        _active.Clear();
        _schedule.Clear();
        _events.Clear();
        _history.Clear();
        _historyOrder.Clear();
        _watermarks.Clear();
        _admission.Clear();
        _retainedNodes = 0;
    }

    /// <summary>Проверяет semantic body без изменения состояния; direct API применяет те же bounds, что wire codec.</summary>
    public static bool IsValidRequest(ProjectionRequest? request) => request is not null &&
        TransportFingerprint.ValidKey(request.Key) && Enum.IsDefined(request.Kind) && Enum.IsDefined(request.Mode) &&
        (request.Kind == ProjectionKind.Graph ? request.Source is null :
            request.Source is { Generation: > 0 } && NodeFactRules.IsValidText(request.Source.NodeId, 128));

    /// <summary>Захватывает weak publication identity и bounded sorted keys; source дерево строится ровно один раз на query.</summary>
    private bool Capture(Query query, GraphSnapshot graph)
    {
        query.Graph = new(graph);
        query.GraphVersion = graph.Version;
        query.InputSequence = graph.InputSequence;
        query.AvailabilityVersion = _availabilityRevision?.Invoke() ?? 0;
        query.InfrastructureStamp = _authority?.InfrastructureStamp ?? 0;
        query.AvailabilityInfrastructureStamp = _authority?.AvailabilityInfrastructureStamp ?? 0;
        if (graph.Nodes.Count > _options.MaxNodes || graph.PhysicalEdges.Count > _options.MaxEdges)
        {
            Fail(query, "projection_capacity", RequestState.Failed);
            return false;
        }
        if (query.Request.Kind == ProjectionKind.Routes)
        {
            try { query.Tree = _trees.Build(graph, query.Request.Source!, query.Request.Mode); }
            catch (RoutingException) { Fail(query, "routing_unavailable", RequestState.Failed); return false; }
            if (query.Tree.Status != RouteStatus.Found)
            {
                Fail(query, "source_unavailable", RequestState.Failed);
                return false;
            }
            if (_availabilityRevision is not null && query.AvailabilityVersion != query.Tree.AvailabilityVersion)
            {
                Fail(query, "projection_changed", RequestState.Failed);
                return false;
            }
            query.AvailabilityVersion = query.Tree.AvailabilityVersion;
            query.Keys = query.Tree.Entries.Keys.OrderBy(key => graph.Nodes[key].State.Address, StringComparer.Ordinal).ToArray();
            if (_authority is not null)
            {
                query.SourceNodeRevision = _authority.NodeRevision(query.Request.Source!);
                if (query.SourceNodeRevision == 0 || query.InfrastructureStamp == 0 || query.AvailabilityInfrastructureStamp == 0)
                {
                    Fail(query, "source_unavailable", RequestState.Failed);
                    return false;
                }
                foreach (var key in query.Keys) query.NodeRevisions.Add(key, _authority.NodeRevision(key));
                query.Keys = query.Keys.Where(key => query.NodeRevisions[key] > 0).ToArray();
            }
            query.RetainedNodes = query.Keys.Length + query.Tree.Entries.Count;
            query.RetainedNodes += query.NodeRevisions.Count;
        }
        else
        {
            query.Keys = graph.AddressIndex.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
            query.RetainedNodes = query.Keys.Length;
        }
        _retainedNodes += query.RetainedNodes;
        return true;
    }

    /// <summary>Проверяет exact snapshot и причинную revision; optional fallback Routes обращается к bounded source-tree cache.</summary>
    private bool Current(Query query, GraphSnapshot graph)
    {
        if (query.Request.Kind == ProjectionKind.Routes && _authority is not null)
            return query.InfrastructureStamp == _authority.InfrastructureStamp &&
                query.AvailabilityInfrastructureStamp == _authority.AvailabilityInfrastructureStamp &&
                query.SourceNodeRevision > 0 && query.SourceNodeRevision == _authority.NodeRevision(query.Request.Source!);
        if (query.Graph is null || !query.Graph.TryGetTarget(out var captured) || !ReferenceEquals(captured, graph) ||
            graph.Version != query.GraphVersion || graph.InputSequence != query.InputSequence)
            return false;
        // Graph содержит только structural строки: av-only churn не должен бесконечно срывать большой graph snapshot.
        // Его captured AvailabilityVersion является metadata, а не разрешением использовать route authority.
        if (query.Request.Kind == ProjectionKind.Graph) return true;
        if (_availabilityRevision is not null) return _availabilityRevision() == query.AvailabilityVersion;
        try { return _trees.Build(graph, query.Request.Source!, query.Request.Mode).AvailabilityVersion == query.AvailabilityVersion; }
        catch (RoutingException) { return false; }
    }

    /// <summary>Материализует одну маленькую страницу, сохраняя фазу nodes → edges и не создавая relay clique.</summary>
    private void Publish(Query query, GraphSnapshot graph)
    {
        var rows = new List<ProjectionRow>(_options.PageRows);
        while (rows.Count < _options.PageRows && query.NodeCursor < query.Keys.Length)
        {
            var key = query.Keys[query.NodeCursor++];
            if (query.Request.Kind == ProjectionKind.Routes)
            {
                var entry = query.Tree!.Entries[key];
                rows.Add(new RouteRow(key, entry.Previous, entry.LatencyDs, entry.Quantum, query.NodeRevisions.GetValueOrDefault(key)));
            }
            else
            {
                var state = graph.Nodes[key].State;
                graph.Bindings.TryGetValue(key, out var anchor);
                graph.LanIds.TryGetValue(key, out var lan);
                rows.Add(new GraphNodeRow(key, state.Address, anchor, lan,
                    state.Position is not null && state.Network?.IsNtnetRelay == true));
            }
        }
        if (query.Request.Kind == ProjectionKind.Graph)
            while (rows.Count < _options.PageRows && query.EdgeCursor < graph.PhysicalEdges.Count)
                rows.Add(new GraphEdgeRow(graph.PhysicalEdges[query.EdgeCursor++]));
        var complete = query.NodeCursor == query.Keys.Length &&
            (query.Request.Kind == ProjectionKind.Routes || query.EdgeCursor == graph.PhysicalEdges.Count);
        var page = Page(query, rows, complete, complete ? "completed" : "page");
        _events.AddLast(new PendingPage(query, page));
        if (complete)
        {
            query.TerminalCode = "completed";
            query.TerminalState = RequestState.Completed;
            Stop(query);
        }
    }

    /// <summary>Отзывает все ещё не переданные страницы и помещает один безопасный terminal в освобождённый output slot.</summary>
    private void Fail(Query query, string code, RequestState state)
    {
        RemovePages(query.Request.Key);
        _events.AddLast(new PendingPage(query, Terminal(query, code, state)));
    }

    /// <summary>Прекращает генерацию и освобождает workspace; slot остаётся занятым до handoff terminal.</summary>
    private ProjectionPage Terminal(Query query, string code, RequestState state)
    {
        query.TerminalCode = code;
        query.TerminalState = state;
        Stop(query);
        return Page(query, [], true, code);
    }

    /// <summary>Создаёт независимую страницу и увеличивает её номер; число страниц ограничено bounded rows и queries.</summary>
    private ProjectionPage Page(Query query, IEnumerable<ProjectionRow> rows, bool complete, string code) =>
        new(_binding, query.Request.Key, query.Request.Kind, query.GraphVersion, query.InputSequence,
            query.AvailabilityVersion, query.Request.Source, query.NextPageIndex++, rows, complete, code,
            query.InfrastructureStamp, query.AvailabilityInfrastructureStamp, query.SourceNodeRevision);

    /// <summary>Убирает round-robin node и все большие временные структуры без удержания прежнего дерева в queued event.</summary>
    private void Stop(Query query)
    {
        if (query.Schedule is { } schedule) _schedule.Remove(schedule);
        query.Schedule = null;
        _retainedNodes -= query.RetainedNodes;
        query.RetainedNodes = 0;
        query.Keys = [];
        query.Tree = null;
        query.NodeRevisions.Clear();
    }

    /// <summary>На фактической terminal передаче освобождает active slot и оставляет лишь bounded replay запись.</summary>
    private void Retire(Query query)
    {
        Stop(query);
        _active.Remove(query.Request.Key);
        _history.Add(query.Request.Key, new History(query.Fingerprint, query.TerminalState!.Value, query.TerminalCode!));
        _historyOrder.Enqueue(query.Request.Key);
        while (_historyOrder.Count > _options.HistoryCapacity) _history.Remove(_historyOrder.Dequeue());
    }

    /// <summary>Bounded scan глобального output удаляет только страницы отзываемого query.</summary>
    private void RemovePages(RequestKey key)
    {
        var node = _events.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.Query.Request.Key == key) _events.Remove(node);
            node = next;
        }
    }

    /// <summary>SHA-256 полного logical body; граф и availability не входят в request identity и не разрешают повтор ключа.</summary>
    private static byte[] Fingerprint(ProjectionRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-PROJECTION-1");
            writer.Write(request.Key.ProducerId);
            writer.Write(request.Key.Sequence);
            writer.Write((int)request.Kind);
            writer.Write((int)request.Mode);
            writer.Write(request.Source is not null);
            if (request.Source is { } source) { writer.Write(source.NodeId); writer.Write(source.Generation); }
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    /// <summary>Сравнивает фиксированные fingerprints без process-local hash identity.</summary>
    private static bool Same(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>Гарантирует последовательное владение при callback-based graph и availability зависимостях.</summary>
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy) throw new InvalidOperationException("Projection is already executing.");
        _busy = true;
    }
}
