using System.Runtime.CompilerServices;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Communication;

/// <summary>
/// Явный communications channel: обычный Machine путь имеет абсолютный приоритет.
/// При его отсутствии augmented Dijkstra использует исходные nodes/cables, implicit NTnet bucket
/// и только configured native edges, без полного P² meta-graph между портами.
/// </summary>
public sealed class CommunicationRouter : ICommunicationRouteProvider
{
    /// <summary>Разрешённые families обоих концов; service ID никогда сам по себе не включает обычный graph overlay.</summary>
    private static readonly HashSet<string> Families = new(StringComparer.Ordinal)
    {
        "telecomms.origin", "telecomms.receiver", "telecomms.relay", "pda.messenger",
        "communications.gateway", "communications.console"
    };

    /// <summary>Weak-key token не удерживает GraphSnapshot внутри LRU.</summary>
    private sealed class GraphIdentity { }

    /// <summary>Причинный cache key native channel; owner identity и family покрывают service rebinding.</summary>
    private readonly record struct CacheKey(GraphIdentity Graph, ulong Availability, ulong Ports, ulong Costs,
        int? Frequency, ServiceKey Source, ServiceKey Destination, NodeKey From, NodeKey To, string SourceFamily, string DestinationFamily);

    /// <summary>Стабильный результат поиска и ключ его LRU slot.</summary>
    private sealed record CacheEntry(CacheKey Key, RouteResult Route);

    /// <summary>
    /// Явная C# policy: сначала latency, затем меньше privileged native hops.
    /// При полном равенстве predecessor сохраняется; physical segment выигрывает у равного native bridge.
    /// </summary>
    private readonly record struct Score(double Cost, int NativeHops) : IComparable<Score>
    {
        /// <summary>Полный порядок, сохраняющий положительность первого компонента Dijkstra.</summary>
        public int CompareTo(Score other)
        {
            var comparison = Cost.CompareTo(other.Cost);
            return comparison != 0 ? comparison : NativeHops.CompareTo(other.NativeHops);
        }
    }

    /// <summary>После полного score равенства heap использует ordinal node address.</summary>
    private readonly record struct Priority(Score Score, string Address) : IComparable<Priority>
    {
        /// <summary>Детерминированный tie-break, независимый от catalog или registry insertion order.</summary>
        public int CompareTo(Priority other)
        {
            var comparison = Score.CompareTo(other.Score);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(Address, other.Address);
        }
    }

    /// <summary>Версионированные native ports, принадлежащие владельцу runtime.</summary>
    private readonly CommunicationPortCatalog _catalog;
    /// <summary>Optional live availability; без overlay используются immutable graph flags.</summary>
    private readonly INetworkAvailability? _availability;
    /// <summary>Обычный Machine router; caller может разделять его bounded кеш с общим транспортом.</summary>
    private readonly IRouteProvider _ordinary;
    /// <summary>Те же typed costs должны применяться injected ordinary provider; null использует declared graph costs.</summary>
    private readonly IRouteCostPolicy? _costs;
    /// <summary>Фиксированные latency и пределы рабочего пространства.</summary>
    private readonly CommunicationOptions _options;
    /// <summary>Уникальная identity publication без удержания старых полных графов.</summary>
    private readonly ConditionalWeakTable<GraphSnapshot, GraphIdentity> _identities = new();
    /// <summary>O(1) lookup положительных и отрицательных overlay answers.</summary>
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = [];
    /// <summary>MRU/LRU цепочка, не хранящая native facts или request payload.</summary>
    private readonly LinkedList<CacheEntry> _lru = [];
    /// <summary>Защищает query от повторного входа через внешнюю typed dependency.</summary>
    private bool _finding;

    /// <summary>Число использованных fallback cache entries; обычные пути кеширует ordinary provider.</summary>
    public ulong CacheHits { get; private set; }
    /// <summary>Число выполненных augmented searches.</summary>
    public ulong CacheMisses { get; private set; }
    /// <summary>Bounded число retained fallback answers.</summary>
    public int CacheCount => _cache.Count;

    /// <summary>Фиксирует catalog и политики; вызов этого provider является отдельным явным channel opt-in.</summary>
    public CommunicationRouter(CommunicationPortCatalog catalog, INetworkAvailability? availability = null,
        IRouteProvider? ordinary = null, IRouteCostPolicy? costs = null, CommunicationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _availability = availability;
        _costs = costs;
        _options = options ?? new CommunicationOptions();
        if (!double.IsFinite(_options.NativeLatencyDs) || _options.NativeLatencyDs <= 0 || _options.CacheCapacity < 0 ||
            _options.MaxNodes < 1 || _options.MaxQueueEntries < 1)
            throw new CommunicationException("invalid_communication_options");
        _ordinary = ordinary ?? new NetworkRouter(availability, costs, new RouterOptions(
            _options.CacheCapacity, _options.MaxNodes, _options.MaxQueueEntries));
    }

    /// <inheritdoc />
    public RouteResult FindRoute(GraphSnapshot graph, ServiceKey source, ServiceKey destination, int? frequency,
        INetworkWorldView world)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (frequency is <= 0)
            throw new CommunicationException("invalid_communication_frequency");
        if (graph.Nodes.Count > _options.MaxNodes)
            throw new CommunicationException("communication_node_capacity");
        if (_finding)
            throw new CommunicationException("communication_reentrant");
        _finding = true;
        try
        {
            var availability = _availability?.Revision ?? 0;
            var costs = _costs?.Revision ?? 0;
            var from = Resolve(world, source);
            var to = Resolve(world, destination);
            if (from is null || to is null || !CurrentOwner(graph, world, from.Owner) || !CurrentOwner(graph, world, to.Owner))
                return Failed(RouteStatus.StaleEndpoint);
            if (!from.Online || !to.Online ||
                !world.Nodes[from.Owner.NodeId].State.Online || !world.Nodes[to.Owner.NodeId].State.Online)
                return Failed(RouteStatus.Unreachable);
            var ordinary = _ordinary.FindRoute(graph, from.Owner, to.Owner, RouteKind.Machine);
            if ((_availability?.Revision ?? 0) != availability || (_costs?.Revision ?? 0) != costs)
                throw new CommunicationException("communication_state_changed");
            if (ordinary.Status != RouteStatus.Unreachable)
                return ordinary;
            if (!Families.Contains(from.Family) || !Families.Contains(to.Family))
                return Failed(RouteStatus.Unreachable);
            var ports = _catalog.Snapshot;
            var key = new CacheKey(_identities.GetValue(graph, static _ => new GraphIdentity()), availability,
                ports.Revision, costs, frequency, source, destination, from.Owner, to.Owner, from.Family, to.Family);
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                CacheHits++;
                return hit.Value.Route;
            }
            CacheMisses++;
            var result = Search(graph, from.Owner, to.Owner, frequency, ports, availability);
            if ((_availability?.Revision ?? 0) != availability || (_costs?.Revision ?? 0) != costs || _catalog.Revision != ports.Revision)
                throw new CommunicationException("communication_state_changed");
            Remember(key, result);
            return result;

            // Unsupported/unknown services получают безопасный status без произвольных native lookup или payload.
            RouteResult Failed(RouteStatus status) => new(status, [], 0, graph.Version, availability);
        }
        finally { _finding = false; }
    }

    /// <summary>Читает exact service generation; mutable registry и авторизация не заимствуются как результат.</summary>
    private static ServiceFact? Resolve(INetworkWorldView world, ServiceKey key) =>
        key.Generation > 0 && NodeFactRules.IsValidText(key.ServiceId, 128) && world.Services.TryGetValue(key.ServiceId, out var service) &&
        service.Generation == key.Generation ? service : null;

    /// <summary>Сервис обязан ссылаться на то же физическое поколение в graph и текущем world.</summary>
    private static bool CurrentOwner(GraphSnapshot graph, INetworkWorldView world, NodeKey? owner) =>
        owner is not null && owner.Generation > 0 && NodeFactRules.IsValidText(owner.NodeId, 128) && graph.Nodes.ContainsKey(owner) &&
        world.Nodes.TryGetValue(owner.NodeId, out var fact) && fact.Generation == owner.Generation;

    /// <summary>Один augmented поиск, где память линейна по исходным узлам/queue и configured native out-degree.</summary>
    private RouteResult Search(GraphSnapshot graph, NodeKey source, NodeKey destination, int? frequency,
        CommunicationPortSnapshot ports, ulong availabilityVersion)
    {
        var sampled = new Dictionary<NodeKey, NodeAvailability>();
        if (!State(source).Present || !State(destination).Present)
            return Failed(RouteStatus.StaleEndpoint);
        if (!State(source).Online || !State(destination).Online)
            return Failed(RouteStatus.Unreachable);
        var distances = new Dictionary<NodeKey, Score> { [source] = new(0, 0) };
        var previous = new Dictionary<NodeKey, NodeKey>();
        var settled = new HashSet<NodeKey>();
        var queue = new PriorityQueue<NodeKey, Priority>();
        queue.Enqueue(source, new Priority(new Score(0, 0), graph.Nodes[source].State.Address));
        var relaysExpanded = false;
        while (queue.TryDequeue(out var current, out var priority))
        {
            if (settled.Contains(current) || priority.Score != distances[current])
                continue;
            settled.Add(current);
            if (current == destination)
            {
                var path = new List<NodeKey> { current };
                while (previous.TryGetValue(current, out var parent))
                {
                    current = parent;
                    path.Add(current);
                }
                path.Reverse();
                return new RouteResult(RouteStatus.Found, path, priority.Score.Cost, graph.Version, availabilityVersion);
            }
            var state = State(current);
            if (!state.Present || !state.Online)
                continue;
            foreach (var edge in graph.Adjacency[current])
            {
                var neighbor = edge.From == current ? edge.To : edge.From;
                Relax(current, neighbor, priority.Score, _costs?.PhysicalCost(edge) ?? edge.LatencyDs, native: false);
            }
            if (!relaysExpanded && (_availability?.BackboneEnabled ?? true) && state.RelayOperational &&
                graph.Nodes[current].State is { Position: not null, Network.IsNtnetRelay: true })
            {
                relaysExpanded = true;
                var relayCost = PositiveCost(_costs?.RelayCost(graph) ?? graph.RelayLatencyDs);
                foreach (var relay in graph.RelayBucket)
                    if (State(relay).RelayOperational)
                        Relax(current, relay, priority.Score, relayCost, native: false);
            }
            if (!ports.Ports.TryGetValue(current, out var port) || !Eligible(port, frequency))
                continue;
            foreach (var target in port.ConfiguredDirectedLinks)
            {
                if (!graph.Nodes.ContainsKey(target) || !ports.Ports.TryGetValue(target, out var peer) || !Eligible(peer, frequency))
                    continue;
                if (port.NativeZ != peer.NativeZ && !port.LongRange && !peer.LongRange)
                    continue;
                var allowed = port.Kind == CommunicationPortKind.Relay && peer.Kind == CommunicationPortKind.Hub && port.Receiving ||
                    port.Kind == CommunicationPortKind.Hub && peer.Kind == CommunicationPortKind.Relay && peer.Broadcasting;
                if (allowed)
                    Relax(current, target, priority.Score, _options.NativeLatencyDs, native: true);
            }
        }
        return Failed(RouteStatus.Unreachable);

        // Полный node Online нужен и для native intermediate; hardware Operational сам по себе не снимает offline_until.
        NodeAvailability State(NodeKey key)
        {
            if (!sampled.TryGetValue(key, out var state))
            {
                var node = graph.Nodes[key].State;
                state = _availability?.Get(key) ?? new NodeAvailability(node.Online, node.Network?.RelayOperational ?? true);
                sampled.Add(key, state);
            }
            return state;
        }

        // Полное score равенство оставляет predecessor; вторичный native hop score заменяет равный privileged segment физическим.
        void Relax(NodeKey from, NodeKey to, Score previousScore, double cost, bool native)
        {
            if (settled.Contains(to) || !State(to).Present || !State(to).Online)
                return;
            var score = new Score(previousScore.Cost + PositiveCost(cost), previousScore.NativeHops + (native ? 1 : 0));
            if (!double.IsFinite(score.Cost))
                throw new RoutingException("invalid_route_cost");
            if (distances.TryGetValue(to, out var old) && old.CompareTo(score) <= 0)
                return;
            if (queue.Count >= _options.MaxQueueEntries)
                throw new CommunicationException("communication_queue_capacity");
            distances[to] = score;
            previous[to] = from;
            queue.Enqueue(to, new Priority(score, graph.Nodes[to].State.Address));
        }

        // Ни NaN, ни Infinity не используются для сетевого отказа.
        RouteResult Failed(RouteStatus status) => new(status, [], 0, graph.Version, availabilityVersion);
    }

    /// <summary>Оба native switches, hardware и оба listening filters применяются независимо от общего graph.</summary>
    private static bool Eligible(CommunicationPortFact port, int? frequency) => port.On && port.Toggled && port.Operational &&
        (frequency is null || port.ListeningFrequencies.Count == 0 || port.ListeningFrequencies.Contains(frequency.Value));

    /// <summary>Тот же strict positive finite контракт, что у обычного router.</summary>
    private static double PositiveCost(double cost) => double.IsFinite(cost) && cost > 0 ? cost : throw new RoutingException("invalid_route_cost");

    /// <summary>Общий bounded LRU для найденных и отсутствующих fallback путей.</summary>
    private void Remember(CacheKey key, RouteResult route)
    {
        if (_options.CacheCapacity == 0)
            return;
        _cache.Add(key, _lru.AddFirst(new CacheEntry(key, route)));
        if (_cache.Count > _options.CacheCapacity)
        {
            var oldest = _lru.Last!;
            _lru.RemoveLast();
            _cache.Remove(oldest.Value.Key);
        }
    }
}
