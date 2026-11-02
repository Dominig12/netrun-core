using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Netrun.Core.Topology;

namespace Netrun.Core.Routing;

/// <summary>Одна достижимая вершина полного дерева; исходный узел имеет null predecessor и нулевую стоимость.</summary>
/// <param name="Previous">Предыдущее точное поколение на пути от Source; граф и native-объекты не удерживаются.</param>
/// <param name="LatencyDs">Конечная накопленная задержка в децисекундах.</param>
/// <param name="Quantum">Путь содержит хотя бы один виртуальный NTnet relay переход, включая предыдущие сегменты.</param>
public sealed record SourceRouteEntry(NodeKey? Previous, double LatencyDs, bool Quantum);

/// <summary>Лимиты полного поиска и его LRU; одно дерево занимает не более MaxNodes записей.</summary>
/// <param name="CacheCapacity">Общий максимум положительных и отрицательных деревьев; ноль отключает кеш.</param>
/// <param name="MaxNodes">Максимум узлов исходной публикации и длины материализуемого пути.</param>
/// <param name="MaxQueueEntries">Максимум одновременно ожидающих кандидатов Dijkstra, включая устаревшие приоритеты.</param>
public sealed record SourceTreeOptions(int CacheCapacity = 16, int MaxNodes = 10_000, int MaxQueueEntries = 100_000);

/// <summary>
/// Неизменяемое полное дерево одного источника. Хранит только поколения и скаляры,
/// поэтому кеш дерева не удерживает GraphSnapshot или мир. Отсутствие цели в Entries означает недостижимость.
/// </summary>
public sealed class SourceRouteTree
{
    /// <summary>Точное поколение исходного узла, включая случай StaleEndpoint.</summary>
    public NodeKey Source { get; }
    /// <summary>Found означает допустимый источник; StaleEndpoint и Unreachable имеют пустой Entries.</summary>
    public RouteStatus Status { get; }
    /// <summary>Версия структурного графа, использованного при полном поиске.</summary>
    public ulong GraphVersion { get; }
    /// <summary>Граница world inputs опубликованного графа; не является версией availability overlay.</summary>
    public ulong InputSequence { get; }
    /// <summary>Ревизия текущей доступности; ноль при чтении только неизменяемых flags графа.</summary>
    public ulong AvailabilityVersion { get; }
    /// <summary>Собственная read-only копия достижимых поколений с predecessor, стоимостью и признаком quantum пути.</summary>
    public IReadOnlyDictionary<NodeKey, SourceRouteEntry> Entries { get; }

    /// <summary>Копирует завершённый workspace; последующий поиск не может изменить уже выданное дерево.</summary>
    internal SourceRouteTree(NodeKey source, RouteStatus status, ulong graphVersion, ulong inputSequence,
        ulong availabilityVersion, Dictionary<NodeKey, SourceRouteEntry> entries)
    {
        Source = source;
        Status = status;
        GraphVersion = graphVersion;
        InputSequence = inputSequence;
        AvailabilityVersion = availabilityVersion;
        Entries = new ReadOnlyDictionary<NodeKey, SourceRouteEntry>(new Dictionary<NodeKey, SourceRouteEntry>(entries));
    }

    /// <summary>
    /// Материализует только запрошенный путь в собственной read-only копии; неизвестная цель даёт пустой список.
    /// Обход ограничен числом Entries, а нарушенная цепочка явно отвергается безопасным кодом.
    /// </summary>
    public IReadOnlyList<NodeKey> GetPath(NodeKey destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!Entries.ContainsKey(destination))
            return Array.Empty<NodeKey>();
        var path = new List<NodeKey>();
        var current = destination;
        for (var visited = 0; visited < Entries.Count; visited++)
        {
            if (!Entries.TryGetValue(current, out var entry))
                throw new RoutingException("invalid_route_tree");
            path.Add(current);
            if (entry.Previous is null)
            {
                if (current != Source)
                    throw new RoutingException("invalid_route_tree");
                path.Reverse();
                return path.AsReadOnly();
            }
            current = entry.Previous;
        }
        throw new RoutingException("invalid_route_tree");
    }
}

/// <summary>
/// Последовательный полный weighted Dijkstra за O((V+E+R) log V) вместо отдельных поисков ко всем целям.
/// Политики forwarding и равных стоимостей совпадают с NetworkRouter; I/O и публикация топологии отсутствуют.
/// </summary>
public sealed class SourceRouteTreeProvider
{
    /// <summary>Токен weak graph identity; сам не содержит обратной ссылки на GraphSnapshot.</summary>
    private sealed class GraphIdentity { }

    /// <summary>Полная причинная идентичность поиска без strong-ссылки на структурный snapshot.</summary>
    private readonly record struct CacheKey(GraphIdentity Graph, ulong Availability, ulong Costs, NodeKey Source, RouteKind Mode);

    /// <summary>Значение LRU с независимым immutable результатом.</summary>
    private sealed record CacheItem(CacheKey Key, SourceRouteTree Tree);

    /// <summary>Сначала конечная стоимость, затем ordinal адрес: такой же порядок извлечения, как у NetworkRouter.</summary>
    private readonly record struct QueuePriority(double Cost, string Address) : IComparable<QueuePriority>
    {
        /// <summary>Фиксирует воспроизводимый первый predecessor при равной стоимости без сравнения NodeId.</summary>
        public int CompareTo(QueuePriority other)
        {
            var comparison = Cost.CompareTo(other.Cost);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(Address, other.Address);
        }
    }

    /// <summary>Optional текущая доступность; null выбирает flags immutable graph.</summary>
    private readonly INetworkAvailability? _availability;
    /// <summary>Optional чистая ревизуемая политика положительных стоимостей.</summary>
    private readonly IRouteCostPolicy? _costs;
    /// <summary>Постоянные ограничения памяти поиска и числа кешированных деревьев.</summary>
    private readonly SourceTreeOptions _options;
    /// <summary>Weak-key индекс назначает identity, не продлевая жизнь старого графа.</summary>
    private readonly ConditionalWeakTable<GraphSnapshot, GraphIdentity> _graphIdentities = new();
    /// <summary>O(1) индекс элементов LRU, включая пустые ответы для stale/offline источников.</summary>
    private readonly Dictionary<CacheKey, LinkedListNode<CacheItem>> _cache = [];
    /// <summary>MRU находится в начале; вытеснение не требует обхода всех деревьев.</summary>
    private readonly LinkedList<CacheItem> _lru = [];
    /// <summary>Защита от повторного Build из пользовательской policy; объект принадлежит одному последовательному владельцу.</summary>
    private bool _building;

    /// <summary>Число повторно выданных immutable деревьев, включая отрицательные результаты.</summary>
    public ulong CacheHits { get; private set; }
    /// <summary>Число выполненных поисков, включая запросы при отключённом кеше.</summary>
    public ulong CacheMisses { get; private set; }
    /// <summary>Текущее число кешированных деревьев, ограниченное SourceTreeOptions.CacheCapacity.</summary>
    public int CacheCount => _cache.Count;

    /// <summary>Фиксирует политики и лимиты; их содержимое должно быть стабильным в пределах одного Build.</summary>
    public SourceRouteTreeProvider(INetworkAvailability? availability = null, IRouteCostPolicy? costs = null,
        SourceTreeOptions? options = null)
    {
        _availability = availability;
        _costs = costs;
        _options = options ?? new SourceTreeOptions();
        if (_options.CacheCapacity < 0 || _options.MaxNodes < 1 || _options.MaxQueueEntries < 1)
            throw new RoutingException("invalid_router_options");
    }

    /// <summary>
    /// Строит все допустимые пути одной публикации. Discovery включает offline цели без forwarding через них;
    /// offline Discovery источник знает только себя, а Machine источник обязан быть online.
    /// </summary>
    public SourceRouteTree Build(GraphSnapshot graph, NodeKey source, RouteKind mode = RouteKind.Discovery)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(mode))
            throw new RoutingException("invalid_route_kind");
        if (graph.Nodes.Count > _options.MaxNodes)
            throw new RoutingException("route_node_capacity");
        if (_building)
            throw new RoutingException("route_reentrant");
        _building = true;
        try
        {
            var availability = _availability?.Revision ?? 0;
            var costs = _costs?.Revision ?? 0;
            var identity = _graphIdentities.GetValue(graph, static _ => new GraphIdentity());
            var key = new CacheKey(identity, availability, costs, source, mode);
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                CacheHits++;
                return hit.Value.Tree;
            }
            CacheMisses++;
            var result = Search(graph, source, mode, availability);
            if ((_availability?.Revision ?? 0) != availability || (_costs?.Revision ?? 0) != costs)
                throw new RoutingException("route_state_changed");
            Remember(key, result);
            return result;
        }
        finally { _building = false; }
    }

    /// <summary>Обходит reachable graph один раз; uniform quantum bucket расширяется только от самого дешёвого допустимого relay.</summary>
    private SourceRouteTree Search(GraphSnapshot graph, NodeKey source, RouteKind mode, ulong availabilityVersion)
    {
        if (!graph.Nodes.ContainsKey(source))
            return Result(RouteStatus.StaleEndpoint, []);
        var sampled = new Dictionary<NodeKey, NodeAvailability>();
        var sourceState = State(source);
        if (!sourceState.Present)
            return Result(RouteStatus.StaleEndpoint, []);
        if (!sourceState.Online && mode == RouteKind.Machine)
            return Result(RouteStatus.Unreachable, []);

        var entries = new Dictionary<NodeKey, SourceRouteEntry> { [source] = new(null, 0, false) };
        if (!sourceState.Online)
            return Result(RouteStatus.Found, entries);
        var settled = new HashSet<NodeKey>();
        var queue = new PriorityQueue<NodeKey, QueuePriority>();
        queue.Enqueue(source, new QueuePriority(0, graph.Nodes[source].State.Address));
        var relaysExpanded = false;
        var backboneEnabled = _availability?.BackboneEnabled ?? true;
        while (queue.TryDequeue(out var current, out var priority))
        {
            if (settled.Contains(current) || priority.Cost != entries[current].LatencyDs)
                continue;
            settled.Add(current);
            var currentState = State(current);
            if (!currentState.Present || !currentState.Online)
                continue;
            foreach (var edge in graph.Adjacency[current])
            {
                var neighbor = edge.From == current ? edge.To : edge.From;
                if (settled.Contains(neighbor))
                    continue;
                var state = State(neighbor);
                if (!state.Present || mode == RouteKind.Machine && !state.Online)
                    continue;
                Relax(current, neighbor, priority.Cost, _costs?.PhysicalCost(edge) ?? edge.LatencyDs, false);
            }
            if (relaysExpanded || !backboneEnabled || !IsRelay(current) || !currentState.RelayOperational)
                continue;
            relaysExpanded = true;
            var relayCost = PositiveCost(_costs?.RelayCost(graph) ?? graph.RelayLatencyDs);
            foreach (var relay in graph.RelayBucket)
            {
                if (settled.Contains(relay))
                    continue;
                var state = State(relay);
                if (!state.Present || !state.RelayOperational || mode == RouteKind.Machine && !state.Online)
                    continue;
                Relax(current, relay, priority.Cost, relayCost, true);
            }
        }
        return Result(RouteStatus.Found, entries);

        // Одна стабильная выборка на поколение; удалённое поколение не становится offline discovery целью.
        NodeAvailability State(NodeKey key)
        {
            if (!sampled.TryGetValue(key, out var value))
            {
                var state = graph.Nodes[key].State;
                value = _availability?.Get(key) ?? new NodeAvailability(state.Online, state.Network?.RelayOperational ?? true);
                sampled.Add(key, value);
            }
            return value;
        }

        // Сохранённый relay флаг без физического положения не добавляет членство в quantum bucket.
        bool IsRelay(NodeKey key) => graph.Nodes[key].State.Position is not null &&
            graph.Nodes[key].State.Network?.IsNtnetRelay == true;

        // Равная стоимость не меняет predecessor или Quantum; первый выбор совпадает с pair router.
        void Relax(NodeKey from, NodeKey to, double currentCost, double edgeCost, bool quantum)
        {
            var total = currentCost + PositiveCost(edgeCost);
            if (!double.IsFinite(total))
                throw new RoutingException("invalid_route_cost");
            if (entries.TryGetValue(to, out var prior) && prior.LatencyDs <= total)
                return;
            if (queue.Count >= _options.MaxQueueEntries)
                throw new RoutingException("route_queue_capacity");
            entries[to] = new SourceRouteEntry(from, total, quantum || entries[from].Quantum);
            queue.Enqueue(to, new QueuePriority(total, graph.Nodes[to].State.Address));
        }

        // Копирование на явной границе публикации отделяет результат от workspace Dijkstra.
        SourceRouteTree Result(RouteStatus status, Dictionary<NodeKey, SourceRouteEntry> result) =>
            new(source, status, graph.Version, graph.InputSequence, availabilityVersion, result);
    }

    /// <summary>Не допускает NaN, Infinity или неположительную стоимость в приоритеты и итоговые скаляры.</summary>
    private static double PositiveCost(double cost)
    {
        if (!double.IsFinite(cost) || cost <= 0)
            throw new RoutingException("invalid_route_cost");
        return cost;
    }

    /// <summary>Добавляет одно дерево и вытесняет LRU сверх общего лимита; graph и workspace здесь не удерживаются.</summary>
    private void Remember(CacheKey key, SourceRouteTree tree)
    {
        if (_options.CacheCapacity == 0)
            return;
        var node = _lru.AddFirst(new CacheItem(key, tree));
        _cache.Add(key, node);
        if (_cache.Count > _options.CacheCapacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _cache.Remove(last.Value.Key);
        }
    }
}
