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
    /// Weighted Dijkstra с ранней остановкой на цели через общий <see cref="RouteSearch"/>; политика
    /// forwarding, uniform quantum bucket и равных стоимостей совпадает с полным деревом источника.
    /// Offline discovery destinations могут получить predecessor, но никогда не продолжают обход.
    /// </summary>
    private RouteResult Search(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode, ulong availabilityVersion)
    {
        if (!graph.Nodes.ContainsKey(source) || !graph.Nodes.ContainsKey(destination))
            return Failed(RouteStatus.StaleEndpoint);
        var sourceState = RouteSearch.Availability(graph, _availability, source);
        var destinationState = RouteSearch.Availability(graph, _availability, destination);
        if (!sourceState.Present || !destinationState.Present)
            return Failed(RouteStatus.StaleEndpoint);
        if (mode == RouteKind.Machine && (!sourceState.Online || !destinationState.Online))
            return Failed(RouteStatus.Unreachable);
        if (source == destination)
            return new RouteResult(RouteStatus.Found, [source], 0, graph.Version, availabilityVersion);
        if (!sourceState.Online)
            return Failed(RouteStatus.Unreachable);

        var entries = new Dictionary<NodeKey, SourceRouteEntry>();
        if (!RouteSearch.Run(graph, source, mode, destination, _availability, _costs, _options.MaxQueueEntries, entries))
            return Failed(RouteStatus.Unreachable);
        // Материализуем путь только до settled цели; source имеет null predecessor и завершает цепочку.
        var path = new List<NodeKey>();
        for (NodeKey? cursor = destination; cursor is { } node; cursor = entries[node].Previous)
            path.Add(node);
        path.Reverse();
        return new RouteResult(RouteStatus.Found, path, entries[destination].LatencyDs, graph.Version, availabilityVersion);

        // Отказ передаётся статусом и пустым путём, без NaN/Infinity в диагностике.
        RouteResult Failed(RouteStatus status) => new(status, [], 0, graph.Version, availabilityVersion);
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
