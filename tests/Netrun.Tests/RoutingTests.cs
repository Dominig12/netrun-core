using Netrun.Core;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет routing отдельно от построения mesh, включая независимый all-pairs oracle.</summary>
public sealed class RoutingTests
{
    /// <summary>Создаёт immutable узел; relay обозначается native-флагом, а не только NodeType.</summary>
    private static NodeFact Node(string id, bool online = true, bool relay = false, bool operational = true, ulong generation = 1) =>
        new(id, generation, 1, new(id, relay ? "gateway" : "server", new(1, 1, 1), "s", online,
            new(null, null, IsNtnetRelay: relay, RelayOperational: operational)));

    /// <summary>Идентичность current generation для прямых запросов.</summary>
    private static NodeKey Key(string id, ulong generation = 1) => new(id, generation);

    /// <summary>Строит маленький explicit graph fixture; internal constructor доступен только тестовой сборке.</summary>
    private static GraphSnapshot Graph(NodeFact[] facts, params (string From, string To, int Cost)[] links)
    {
        var nodes = facts.ToDictionary(node => Key(node.NodeId, node.Generation));
        var addresses = nodes.ToDictionary(pair => pair.Value.State.Address, pair => pair.Key, StringComparer.Ordinal);
        var edges = links.Select(link => new PhysicalEdge(addresses[link.From], addresses[link.To], PhysicalLinkKind.Apc, 1, link.Cost)).ToArray();
        var adjacency = nodes.Keys.ToDictionary(key => key, _ => new List<PhysicalEdge>());
        foreach (var edge in edges)
        {
            adjacency[edge.From].Add(edge);
            adjacency[edge.To].Add(edge);
        }
        foreach (var (key, incident) in adjacency)
            incident.Sort((a, b) => StringComparer.Ordinal.Compare(
                nodes[a.From == key ? a.To : a.From].State.Address,
                nodes[b.From == key ? b.To : b.From].State.Address));
        var relays = nodes.Keys.Where(key => nodes[key].State.Network!.IsNtnetRelay)
            .OrderBy(key => nodes[key].State.Address, StringComparer.Ordinal).ToArray();
        return new GraphSnapshot(1, 1, nodes, addresses, edges, adjacency, relays, [], [], 3);
    }

    /// <summary>Маршрутизация минимизирует сумму стоимостей, а не hops; physical paths остаются неориентированными.</summary>
    [Fact]
    public void WeightedShortestPathCanHaveMoreHopsAndWorksInReverse()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C"), Node("D"), Node("X")],
            ("A", "X", 10), ("X", "D", 10), ("A", "B", 1), ("B", "C", 1), ("C", "D", 1));
        var router = new NetworkRouter();
        var forward = router.FindRoute(graph, Key("A"), Key("D"));
        Assert.Equal(RouteStatus.Found, forward.Status);
        Assert.Equal(3, forward.LatencyDs);
        Assert.Equal([Key("A"), Key("B"), Key("C"), Key("D")], forward.Path);
        Assert.Equal([Key("D"), Key("C"), Key("B"), Key("A")], router.FindRoute(graph, Key("D"), Key("A")).Path);
    }

    /// <summary>Равный cost сохраняет первый predecessor, выбранный ordinal address priority, а не insertion order.</summary>
    [Fact]
    public void EqualCostAlternativesUseDeterministicAddressOrder()
    {
        var graph = Graph([Node("D"), Node("C"), Node("B"), Node("A")],
            ("A", "C", 1), ("C", "D", 1), ("A", "B", 1), ("B", "D", 1));
        Assert.Equal([Key("A"), Key("B"), Key("D")], new NetworkRouter().FindRoute(graph, Key("A"), Key("D")).Path);
    }

    /// <summary>Discovery видит offline final, но не проводит через него; offline source знает только самого себя.</summary>
    [Fact]
    public void OfflineDiscoveryAndMachinePoliciesRemainDistinct()
    {
        var graph = Graph([Node("A"), Node("B", online: false), Node("C")], ("A", "B", 1), ("B", "C", 1));
        var router = new NetworkRouter();
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("A"), Key("B")).Status);
        Assert.Equal(RouteStatus.Found, router.FindRoute(graph, Key("A"), Key("B"), RouteKind.Discovery).Status);
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("A"), Key("C"), RouteKind.Discovery).Status);
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("B"), Key("A"), RouteKind.Discovery).Status);
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("B"), Key("B")).Status);
        var self = router.FindRoute(graph, Key("B"), Key("B"), RouteKind.Discovery);
        Assert.Equal(RouteStatus.Found, self.Status);
        Assert.Equal(0, self.LatencyDs);
        Assert.Equal([Key("B")], self.Path);
    }

    /// <summary>Реальные quantum relays соединяют islands; backbone overlay инвалидирует маршрут без graph rebuild.</summary>
    [Fact]
    public void QuantumBucketAndBackboneUseAvailabilityRevision()
    {
        var graph = Graph([Node("A"), Node("R1", relay: true), Node("R2", relay: true), Node("B")],
            ("A", "R1", 1), ("R2", "B", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        var router = new NetworkRouter(availability);
        var route = router.FindRoute(graph, Key("A"), Key("B"));
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.Equal(5, route.LatencyDs);
        Assert.Equal([Key("A"), Key("R1"), Key("R2"), Key("B")], route.Path);
        Assert.Same(route, router.FindRoute(graph, Key("A"), Key("B")));
        Assert.True(availability.SetBackbone(false));
        Assert.False(availability.SetBackbone(false));
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("A"), Key("B")).Status);
        availability.SetBackbone(true);
        Assert.Equal(RouteStatus.Found, router.FindRoute(graph, Key("A"), Key("B")).Status);
        Assert.Equal(1UL, router.CacheHits);
        Assert.Equal(3UL, router.CacheMisses);
    }

    /// <summary>Временно offline operational relay доступен как discovery destination, но не как транзит к другому устройству.</summary>
    [Fact]
    public void RemoteOperationalRelayCanBeDiscoveredWhileOffline()
    {
        var graph = Graph([Node("A"), Node("R1", relay: true), Node("R2", online: false, relay: true), Node("B")],
            ("A", "R1", 1), ("R2", "B", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        var router = new NetworkRouter(availability);
        var destination = router.FindRoute(graph, Key("A"), Key("R2"), RouteKind.Discovery);
        Assert.Equal(RouteStatus.Found, destination.Status);
        Assert.Equal(4, destination.LatencyDs);
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("A"), Key("R2")).Status);
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("A"), Key("B"), RouteKind.Discovery).Status);
        availability.UpdateFact(Node("R2", online: false, relay: true, operational: false));
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("A"), Key("R2"), RouteKind.Discovery).Status);
    }

    /// <summary>Bucket расширяется один раз даже при полном обходе ста relays и неуспешном поиске изолированной цели.</summary>
    [Fact]
    public void UniformQuantumCostIsEvaluatedOncePerSearch()
    {
        var facts = Enumerable.Range(0, 100).Select(i => Node("R" + i.ToString("D3"), relay: true)).Append(Node("Z")).ToArray();
        var graph = Graph(facts);
        var costs = new TestCosts();
        var router = new NetworkRouter(costs: costs);
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, Key("R000"), Key("Z")).Status);
        Assert.Equal(1, costs.RelayReads);
        router.FindRoute(graph, Key("R000"), Key("Z"));
        Assert.Equal(1, costs.RelayReads);
    }

    /// <summary>Одинаковые Version/InputSequence разных builder не приводят к collision cache identity.</summary>
    [Fact]
    public void CacheUsesGraphObjectIdentityRatherThanVersionAlone()
    {
        var first = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var second = Graph([Node("A"), Node("B")], ("A", "B", 9));
        var router = new NetworkRouter();
        Assert.Equal(1, router.FindRoute(first, Key("A"), Key("B")).LatencyDs);
        Assert.Equal(9, router.FindRoute(second, Key("A"), Key("B")).LatencyDs);
        Assert.Equal(1, router.FindRoute(first, Key("A"), Key("B")).LatencyDs);
        Assert.Equal(2UL, router.CacheMisses);
        Assert.Equal(1UL, router.CacheHits);
    }

    /// <summary>Удалённое или заменённое поколение не превращается в разрешённый offline discovery endpoint.</summary>
    [Fact]
    public void OverlayReplacementAndRemovalRejectStaleEndpoints()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        var router = new NetworkRouter(availability);
        Assert.Equal(RouteStatus.Found, router.FindRoute(graph, Key("A"), Key("B")).Status);
        availability.UpdateFact(Node("B", generation: 2));
        Assert.False(availability.Get(Key("B")).Present);
        Assert.Equal(RouteStatus.StaleEndpoint, router.FindRoute(graph, Key("A"), Key("B"), RouteKind.Discovery).Status);
        Assert.False(availability.Remove(Key("B")));
        Assert.False(availability.UpdateFact(Node("B", generation: 1)));
        var replacementGraph = Graph([Node("A"), Node("B", generation: 2)], ("A", "B", 1));
        Assert.Equal(RouteStatus.Found, router.FindRoute(replacementGraph, Key("A"), Key("B", 2)).Status);
        Assert.True(availability.Remove(Key("B", 2)));
        Assert.Equal(RouteStatus.StaleEndpoint, router.FindRoute(replacementGraph, Key("A"), Key("B", 2), RouteKind.Discovery).Status);
        Assert.Equal(1, availability.Count);
    }

    /// <summary>Изменение cost revision исключает старый cache результат; произвольные нечисловые/нулевые веса отвергаются.</summary>
    [Fact]
    public void CostPolicyRevisionInvalidatesCachedPath()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 2));
        var costs = new TestCosts();
        var router = new NetworkRouter(costs: costs);
        Assert.Equal(2, router.FindRoute(graph, Key("A"), Key("B")).LatencyDs);
        costs.SetFactor(4);
        Assert.Equal(8, router.FindRoute(graph, Key("A"), Key("B")).LatencyDs);
        Assert.Equal(2UL, router.CacheMisses);
    }

    /// <summary>Положительность и конечность пользовательского веса проверяются до использования PriorityQueue.</summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidCustomCostsFailExplicitly(double factor)
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var costs = new TestCosts();
        costs.SetFactor(factor);
        Assert.Equal("invalid_route_cost", Assert.Throws<RoutingException>(() =>
            new NetworkRouter(costs: costs).FindRoute(graph, Key("A"), Key("B"))).Code);
    }

    /// <summary>Положительные и отрицательные ответы разделяют один настоящий LRU limit, hit меняет eviction order.</summary>
    [Fact]
    public void CacheIsBoundedAndIncludesNegativeResults()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")], ("A", "B", 1));
        var router = new NetworkRouter(options: new(CacheCapacity: 2));
        router.FindRoute(graph, Key("A"), Key("B"));
        var negative = router.FindRoute(graph, Key("A"), Key("C"));
        Assert.Same(negative, router.FindRoute(graph, Key("A"), Key("C")));
        router.FindRoute(graph, Key("A"), Key("B"));
        router.FindRoute(graph, Key("B"), Key("A"));
        router.FindRoute(graph, Key("A"), Key("C"));
        Assert.Equal(2, router.CacheCount);
        Assert.Equal(2UL, router.CacheHits);
        Assert.Equal(4UL, router.CacheMisses);
        for (var i = 0; i < 1000; i++)
            Assert.Equal(RouteStatus.StaleEndpoint, router.FindRoute(graph, Key("A"), Key("missing-" + i)).Status);
        Assert.Equal(2, router.CacheCount);
    }

    /// <summary>Cached path не отдаёт массив, изменяемый другим потребителем библиотеки.</summary>
    [Fact]
    public void ResultPathIsReadOnlyAndCacheCanBeDisabled()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var router = new NetworkRouter(options: new(CacheCapacity: 0));
        var route = router.FindRoute(graph, Key("A"), Key("B"));
        Assert.Throws<NotSupportedException>(() => ((IList<NodeKey>)route.Path).Clear());
        router.FindRoute(graph, Key("A"), Key("B"));
        Assert.Equal(0, router.CacheCount);
        Assert.Equal(0UL, router.CacheHits);
        Assert.Equal(2UL, router.CacheMisses);
    }

    /// <summary>Overlay хранит одно поколение, reset атомарен, повтор неизменившихся фактов не инвалидирует кеш.</summary>
    [Fact]
    public void AvailabilityUpdatesAreBoundedVersionedAndAtomic()
    {
        var availability = new NetworkAvailability([Node("A")], maxNodes: 1);
        var revision = availability.Revision;
        Assert.False(availability.UpdateFact(Node("A")));
        Assert.Equal(revision, availability.Revision);
        Assert.True(availability.UpdateFact(Node("A", online: false)));
        Assert.True(availability.Revision > revision);
        Assert.Equal("availability_capacity", Assert.Throws<RoutingException>(() => availability.UpdateFact(Node("B"))).Code);
        var current = availability.Revision;
        Assert.Equal("availability_capacity", Assert.Throws<RoutingException>(() => availability.Reset([Node("B"), Node("C")])).Code);
        Assert.Equal(current, availability.Revision);
        Assert.True(availability.Get(Key("A")).Present);
        availability.Reset([Node("B")]);
        Assert.False(availability.Get(Key("A")).Present);
        Assert.True(availability.Get(Key("B")).Present);
        Assert.Equal(1, availability.Count);
    }

    /// <summary>Workspace/queue ограничения проверяются явно; ни очередь, ни LRU не растут неограниченно.</summary>
    [Fact]
    public void QueryAndConfigurationLimitsFailWithSafeCodes()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C"), Node("D")], ("A", "B", 1), ("A", "C", 1));
        Assert.Equal("route_node_capacity", Assert.Throws<RoutingException>(() =>
            new NetworkRouter(options: new(MaxNodes: 2)).FindRoute(graph, Key("A"), Key("D"))).Code);
        Assert.Equal("route_queue_capacity", Assert.Throws<RoutingException>(() =>
            new NetworkRouter(options: new(MaxQueueEntries: 1)).FindRoute(graph, Key("A"), Key("D"))).Code);
        Assert.Equal("invalid_router_options", Assert.Throws<RoutingException>(() => new NetworkRouter(options: new(CacheCapacity: -1))).Code);
        Assert.Equal("invalid_route_kind", Assert.Throws<RoutingException>(() => new NetworkRouter().FindRoute(graph, Key("A"), Key("B"), (RouteKind)100)).Code);
    }

    /// <summary>Все пары обоих режимов совпадают с независимым directed Floyd-Warshall при online/offline/quantum/backbone комбинациях.</summary>
    [Fact]
    public void RoutesMatchIndependentFloydWarshallOracle()
    {
        var random = new Random(123456);
        var facts = Enumerable.Range(0, 9).Select(i => Node(i.ToString(), online: i % 4 != 1,
            relay: i % 3 == 0, operational: i != 6)).ToArray();
        var links = new List<(string, string, int)>();
        for (var i = 0; i < facts.Length; i++)
            for (var j = i + 1; j < facts.Length; j++)
                if (random.NextDouble() < 0.35)
                    links.Add((facts[i].NodeId, facts[j].NodeId, random.Next(1, 8)));
        var graph = Graph(facts, links.ToArray());
        var availability = new NetworkAvailability(facts);
        var router = new NetworkRouter(availability, options: new(CacheCapacity: 7));
        foreach (var backbone in new[] { true, false })
        {
            availability.SetBackbone(backbone);
            foreach (var mode in new[] { RouteKind.Machine, RouteKind.Discovery })
            {
                var expected = Floyd(graph, facts, mode, backbone);
                for (var from = 0; from < facts.Length; from++)
                    for (var to = 0; to < facts.Length; to++)
                    {
                        var result = router.FindRoute(graph, Key(facts[from].NodeId), Key(facts[to].NodeId), mode);
                        if (double.IsPositiveInfinity(expected[from, to]))
                            Assert.Equal(RouteStatus.Unreachable, result.Status);
                        else
                        {
                            Assert.Equal(RouteStatus.Found, result.Status);
                            Assert.Equal(expected[from, to], result.LatencyDs);
                            Assert.Equal(Key(facts[from].NodeId), result.Path[0]);
                            Assert.Equal(Key(facts[to].NodeId), result.Path[^1]);
                        }
                    }
            }
        }
        Assert.Equal(7, router.CacheCount);
    }

    /// <summary>Строит полный directed cost matrix с разрешением offline только как конечного destination.</summary>
    private static double[,] Floyd(GraphSnapshot graph, NodeFact[] facts, RouteKind mode, bool backbone)
    {
        var count = facts.Length;
        var costs = new double[count, count];
        for (var i = 0; i < count; i++)
            for (var j = 0; j < count; j++)
            {
                costs[i, j] = double.PositiveInfinity;
                if (i == j)
                {
                    if (mode == RouteKind.Discovery || facts[i].State.Online)
                        costs[i, j] = 0;
                    continue;
                }
                if (!facts[i].State.Online || mode == RouteKind.Machine && !facts[j].State.Online)
                    continue;
                var edge = graph.PhysicalEdges.FirstOrDefault(edge =>
                    edge.From.NodeId == facts[i].NodeId && edge.To.NodeId == facts[j].NodeId ||
                    edge.To.NodeId == facts[i].NodeId && edge.From.NodeId == facts[j].NodeId);
                if (edge is not null)
                    costs[i, j] = edge.LatencyDs;
                if (backbone && facts[i].State.Network is { IsNtnetRelay: true, RelayOperational: true } &&
                    facts[j].State.Network is { IsNtnetRelay: true, RelayOperational: true })
                    costs[i, j] = Math.Min(costs[i, j], graph.RelayLatencyDs);
            }
        for (var via = 0; via < count; via++)
            for (var from = 0; from < count; from++)
                for (var to = 0; to < count; to++)
                    costs[from, to] = Math.Min(costs[from, to], costs[from, via] + costs[via, to]);
        return costs;
    }

    /// <summary>Тестовая ревизуемая policy меняет только веса; счётчик подтверждает единственное bucket expansion.</summary>
    private sealed class TestCosts : IRouteCostPolicy
    {
        /// <summary>Текущий масштаб объявленных положительных стоимостей.</summary>
        private double _factor = 1;
        /// <inheritdoc />
        public ulong Revision { get; private set; }
        /// <summary>Число обращений к единой quantum стоимости.</summary>
        public int RelayReads { get; private set; }
        /// <summary>Меняет вес и его версию одним действием.</summary>
        public void SetFactor(double factor) { _factor = factor; Revision++; }
        /// <inheritdoc />
        public double PhysicalCost(PhysicalEdge edge) => edge.LatencyDs * _factor;
        /// <inheritdoc />
        public double RelayCost(GraphSnapshot graph) { RelayReads++; return graph.RelayLatencyDs * _factor; }
    }
}
