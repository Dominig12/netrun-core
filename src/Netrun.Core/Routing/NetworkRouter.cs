using System.Runtime.CompilerServices;
using Netrun.Core.Topology;

namespace Netrun.Core.Routing;

/// <summary>
/// Последовательный weighted router с bounded LRU. Запросы читают готовый immutable graph
/// и availability overlay; построение топологии, I/O и игровые эффекты здесь отсутствуют.
/// </summary>
public sealed class NetworkRouter : IRouteProvider
{
    /// <summary>Уникальный token snapshot identity без strong-ссылки на сам snapshot.</summary>
    private sealed class GraphIdentity { }

    /// <summary>Идентичность всех влияющих на маршрут входов; токен не удерживает старые graph generations.</summary>
    private readonly record struct CacheKey(GraphIdentity Graph, ulong Availability, ulong Costs,
        NodeKey Source, NodeKey Destination, RouteKind Mode);

    /// <summary>Immutable значение LRU; Path не ссылается на mutable workspace поиска.</summary>
    private sealed record CacheItem(CacheKey Key, RouteResult Result);

    /// <summary>Приоритет Dijkstra: положительный cost, затем ordinal address.</summary>
    private readonly record struct QueuePriority(double Cost, string Address) : IComparable<QueuePriority>
    {
        /// <summary>Сравнение одинаково для всех PriorityQueue nodes и не зависит от порядка Dictionary.</summary>
        public int CompareTo(QueuePriority other)
        {
            var comparison = Cost.CompareTo(other.Cost);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(Address, other.Address);
        }
    }

    /// <summary>Optional текущая доступность; null выбирает flags самого immutable GraphSnapshot.</summary>
    private readonly INetworkAvailability? _availability;
    /// <summary>Optional пользовательские положительные стоимости с явной ревизией.</summary>
    private readonly IRouteCostPolicy? _costs;
    /// <summary>Границы кеша, workspace и числа одновременно ожидающих candidates.</summary>
    private readonly RouterOptions _options;
    /// <summary>Weak-key index не удерживает старые snapshots ради graph-identity cache keys.</summary>
    private readonly ConditionalWeakTable<GraphSnapshot, GraphIdentity> _graphIdentities = new();
    /// <summary>O(1) поиск LRU node по полной причинной идентичности.</summary>
    private readonly Dictionary<CacheKey, LinkedListNode<CacheItem>> _cache = [];
    /// <summary>MRU в начале, LRU в конце; cache entry не требует полного обхода при eviction.</summary>
    private readonly LinkedList<CacheItem> _lru = [];
    /// <summary>Запрет reentrancy через пользовательские политики; не заменяет single-owner contract.</summary>
    private bool _finding;

    /// <summary>Число использованных положительных и отрицательных cache entries.</summary>
    public ulong CacheHits { get; private set; }
    /// <summary>Число cache misses, включая запросы с отключённым кешем.</summary>
    public ulong CacheMisses { get; private set; }
    /// <summary>Число удерживаемых записей; не превышает RouterOptions.CacheCapacity.</summary>
    public int CacheCount => _cache.Count;

    /// <summary>Фиксирует typed зависимости; их методы должны быть чистыми и стабильными в пределах запроса.</summary>
    public NetworkRouter(INetworkAvailability? availability = null, IRouteCostPolicy? costs = null, RouterOptions? options = null)
    {
        _availability = availability;
        _costs = costs;
        _options = options ?? new RouterOptions();
        if (_options.CacheCapacity < 0 || _options.MaxNodes < 1 || _options.MaxQueueEntries < 1)
            throw new RoutingException("invalid_router_options");
    }

    /// <inheritdoc />
    public RouteResult FindRoute(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!Enum.IsDefined(mode))
            throw new RoutingException("invalid_route_kind");
        if (graph.Nodes.Count > _options.MaxNodes)
            throw new RoutingException("route_node_capacity");
        if (_finding)
            throw new RoutingException("route_reentrant");
        _finding = true;
        try
        {
            var availability = _availability?.Revision ?? 0;
            var costs = _costs?.Revision ?? 0;
            var identity = _graphIdentities.GetValue(graph, static _ => new GraphIdentity());
            var key = new CacheKey(identity, availability, costs, source, destination, mode);
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                CacheHits++;
                return hit.Value.Result;
            }
            CacheMisses++;
            var result = Search(graph, source, destination, mode, availability);
            if ((_availability?.Revision ?? 0) != availability || (_costs?.Revision ?? 0) != costs)
                throw new RoutingException("route_state_changed");
            Remember(key, result);
            return result;
        }
        finally { _finding = false; }
    }

    /// <summary>
    /// Weighted Dijkstra с одним расширением uniform quantum bucket из самого дешёвого online operational relay.
    /// Offline discovery destinations могут получить predecessor, но никогда не продолжают обход.
    /// </summary>
    private RouteResult Search(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode, ulong availabilityVersion)
    {
        if (!graph.Nodes.ContainsKey(source) || !graph.Nodes.ContainsKey(destination))
            return Failed(RouteStatus.StaleEndpoint);
        var sampled = new Dictionary<NodeKey, NodeAvailability>();
        var sourceState = State(source);
        var destinationState = State(destination);
        if (!sourceState.Present || !destinationState.Present)
            return Failed(RouteStatus.StaleEndpoint);
        if (mode == RouteKind.Machine && (!sourceState.Online || !destinationState.Online))
            return Failed(RouteStatus.Unreachable);
        if (source == destination)
            return new RouteResult(RouteStatus.Found, [source], 0, graph.Version, availabilityVersion);
        if (!sourceState.Online)
            return Failed(RouteStatus.Unreachable);

        var distances = new Dictionary<NodeKey, double> { [source] = 0 };
        var previous = new Dictionary<NodeKey, NodeKey>();
        var settled = new HashSet<NodeKey>();
        var queue = new PriorityQueue<NodeKey, QueuePriority>();
        queue.Enqueue(source, new QueuePriority(0, graph.Nodes[source].State.Address));
        var relaysExpanded = false;
        var backboneEnabled = _availability?.BackboneEnabled ?? true;
        while (queue.TryDequeue(out var current, out var priority))
        {
            if (settled.Contains(current) || priority.Cost != distances[current])
                continue;
            settled.Add(current);
            if (current == destination)
            {
                var path = new List<NodeKey> { current };
                while (previous.TryGetValue(current, out var predecessor))
                {
                    current = predecessor;
                    path.Add(current);
                }
                path.Reverse();
                return new RouteResult(RouteStatus.Found, path, priority.Cost, graph.Version, availabilityVersion);
            }
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
                Relax(current, neighbor, priority.Cost, _costs?.PhysicalCost(edge) ?? edge.LatencyDs);
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
                Relax(current, relay, priority.Cost, relayCost);
            }
        }
        return Failed(RouteStatus.Unreachable);

        // Один snapshot доступности на NodeKey в пределах запроса, включая negative samples.
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

        // Null-position relay не является членом quantum bucket даже при сохранённом native-флаге.
        bool IsRelay(NodeKey key) => graph.Nodes[key].State.Position is not null &&
            graph.Nodes[key].State.Network?.IsNtnetRelay == true;

        // Полный равный cost не заменяет predecessor; priority address фиксирует воспроизводимый первый выбор.
        void Relax(NodeKey from, NodeKey to, double currentCost, double edgeCost)
        {
            var total = currentCost + PositiveCost(edgeCost);
            if (!double.IsFinite(total))
                throw new RoutingException("invalid_route_cost");
            if (distances.TryGetValue(to, out var prior) && prior <= total)
                return;
            if (queue.Count >= _options.MaxQueueEntries)
                throw new RoutingException("route_queue_capacity");
            distances[to] = total;
            previous[to] = from;
            queue.Enqueue(to, new QueuePriority(total, graph.Nodes[to].State.Address));
        }

        // Отказ передаётся статусом и пустым путём, без NaN/Infinity в диагностике.
        RouteResult Failed(RouteStatus status) => new(status, [], 0, graph.Version, availabilityVersion);
    }

    /// <summary>Проверяет typed cost policy до помещения значения в очередь или cache.</summary>
    private static double PositiveCost(double cost)
    {
        if (!double.IsFinite(cost) || cost <= 0)
            throw new RoutingException("invalid_route_cost");
        return cost;
    }

    /// <summary>Добавляет результат и вытесняет ровно одну старейшую запись; положительные и отрицательные ответы имеют общий лимит.</summary>
    private void Remember(CacheKey key, RouteResult result)
    {
        if (_options.CacheCapacity == 0)
            return;
        var node = _lru.AddFirst(new CacheItem(key, result));
        _cache.Add(key, node);
        if (_cache.Count > _options.CacheCapacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _cache.Remove(last.Value.Key);
        }
    }
}
