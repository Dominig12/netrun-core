using System.Runtime.CompilerServices;
using Netrun.Core;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет полный source tree независимо от построителя топологии и сравнением с pair router и Floyd-Warshall.</summary>
public sealed class SourceRouteTreeTests
{
    /// <summary>Создаёт неизменяемый факт с раздельными online, operational и расположением relay.</summary>
    private static NodeFact Node(string id, bool online = true, bool relay = false, bool operational = true,
        ulong generation = 1, bool placed = true) =>
        new(id, generation, 1, new(id, "server", placed ? new Position(1, 1, 1) : null, "s", online,
            new(null, null, IsNtnetRelay: relay, RelayOperational: operational)));

    /// <summary>Точное поколение узла, используемое индексами дерева.</summary>
    private static NodeKey Key(string id, ulong generation = 1) => new(id, generation);

    /// <summary>Строит маленький канонический undirected graph без зависимости от геометрии APC mesh.</summary>
    private static GraphSnapshot Graph(NodeFact[] facts, params (string From, string To, int Cost)[] links)
    {
        var nodes = facts.ToDictionary(node => Key(node.NodeId, node.Generation));
        var addresses = nodes.ToDictionary(pair => pair.Value.State.Address, pair => pair.Key, StringComparer.Ordinal);
        var edges = links.Select(link => new PhysicalEdge(addresses[link.From], addresses[link.To],
            PhysicalLinkKind.Apc, 1, link.Cost)).ToArray();
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
        var relays = nodes.Keys.Where(key => nodes[key].State.Position is not null &&
                nodes[key].State.Network!.IsNtnetRelay)
            .OrderBy(key => nodes[key].State.Address, StringComparer.Ordinal).ToArray();
        return new GraphSnapshot(1, 7, nodes, addresses, edges, adjacency, relays, [], [], 3);
    }

    /// <summary>Один полный поиск минимизирует веса, сохраняет ordinal tie-break и материализует лишь выбранную цель.</summary>
    [Fact]
    public void CompleteTreeUsesWeightedCostsAndOrdinalPredecessors()
    {
        var graph = Graph([Node("D"), Node("C"), Node("B"), Node("A"), Node("X"), Node("Z")],
            ("A", "C", 1), ("C", "D", 1), ("A", "B", 1), ("B", "D", 1),
            ("A", "X", 8), ("D", "X", 1));
        var tree = new SourceRouteTreeProvider().Build(graph, Key("A"));
        Assert.Equal(RouteStatus.Found, tree.Status);
        Assert.Equal(Key("A"), tree.Source);
        Assert.Equal(graph.Version, tree.GraphVersion);
        Assert.Equal(graph.InputSequence, tree.InputSequence);
        Assert.Equal(0UL, tree.AvailabilityVersion);
        Assert.Equal(5, tree.Entries.Count);
        Assert.Equal(new SourceRouteEntry(null, 0, false), tree.Entries[Key("A")]);
        Assert.Equal(new SourceRouteEntry(Key("B"), 2, false), tree.Entries[Key("D")]);
        Assert.Equal(3, tree.Entries[Key("X")].LatencyDs);
        Assert.Equal([Key("A"), Key("B"), Key("D"), Key("X")], tree.GetPath(Key("X")));
        Assert.Empty(tree.GetPath(Key("Z")));
    }

    /// <summary>Offline вершина доступна только как discovery leaf; offline источник возвращает ровно self entry.</summary>
    [Fact]
    public void OfflineLeafDoesNotForwardAndOfflineSourceKnowsOnlyItself()
    {
        var graph = Graph([Node("A"), Node("B", online: false), Node("C")], ("A", "B", 1), ("B", "C", 1));
        var provider = new SourceRouteTreeProvider();
        var discovery = provider.Build(graph, Key("A"));
        Assert.Equal([Key("A"), Key("B")], discovery.GetPath(Key("B")));
        Assert.False(discovery.Entries.ContainsKey(Key("C")));
        var machine = provider.Build(graph, Key("A"), RouteKind.Machine);
        Assert.Single(machine.Entries);
        var offline = provider.Build(graph, Key("B"));
        Assert.Equal(RouteStatus.Found, offline.Status);
        Assert.Single(offline.Entries);
        Assert.Equal([Key("B")], offline.GetPath(Key("B")));
        var rejected = provider.Build(graph, Key("B"), RouteKind.Machine);
        Assert.Equal(RouteStatus.Unreachable, rejected.Status);
        Assert.Empty(rejected.Entries);
        Assert.Empty(rejected.GetPath(Key("B")));
    }

    /// <summary>Quantum отмечается после реального виртуального перехода и наследуется последующими физическими сегментами.</summary>
    [Fact]
    public void QuantumFlagTracksActualVirtualHopsRatherThanRelayMembership()
    {
        var graph = Graph([Node("A"), Node("R1", relay: true), Node("R2", relay: true), Node("B")],
            ("A", "R1", 1), ("R2", "B", 1));
        var tree = new SourceRouteTreeProvider().Build(graph, Key("A"));
        Assert.False(tree.Entries[Key("R1")].Quantum);
        Assert.True(tree.Entries[Key("R2")].Quantum);
        Assert.True(tree.Entries[Key("B")].Quantum);
        Assert.Equal(5, tree.Entries[Key("B")].LatencyDs);
        Assert.Equal([Key("A"), Key("R1"), Key("R2"), Key("B")], tree.GetPath(Key("B")));

        var physical = Graph([Node("R1", relay: true), Node("R2", relay: true)], ("R1", "R2", 3));
        var tied = new SourceRouteTreeProvider().Build(physical, Key("R1"));
        Assert.False(tied.Entries[Key("R2")].Quantum);
        Assert.Equal(3, tied.Entries[Key("R2")].LatencyDs);
    }

    /// <summary>Discovery допускает offline operational remote relay, но не native broken, null-position или его downstream узлы.</summary>
    [Fact]
    public void RelayDestinationRequiresOperationalAndCannotForwardWhileOffline()
    {
        var graph = Graph([Node("A"), Node("R1", relay: true), Node("R2", online: false, relay: true),
            Node("R3", relay: true, operational: false), Node("R4", relay: true, placed: false), Node("B")],
            ("A", "R1", 1), ("R2", "B", 1));
        var provider = new SourceRouteTreeProvider();
        var tree = provider.Build(graph, Key("A"));
        Assert.Equal(4, tree.Entries[Key("R2")].LatencyDs);
        Assert.True(tree.Entries[Key("R2")].Quantum);
        Assert.False(tree.Entries.ContainsKey(Key("B")));
        Assert.False(tree.Entries.ContainsKey(Key("R3")));
        Assert.False(tree.Entries.ContainsKey(Key("R4")));
        Assert.False(provider.Build(graph, Key("A"), RouteKind.Machine).Entries.ContainsKey(Key("R2")));
    }

    /// <summary>Даже полный обход множества relays запрашивает uniform стоимость единожды, а кеш не запускает обход повторно.</summary>
    [Fact]
    public void QuantumBucketExpandsExactlyOnceForFullTree()
    {
        var graph = Graph(Enumerable.Range(0, 100).Select(i => Node("R" + i.ToString("D3"), relay: true)).ToArray());
        var costs = new TestCosts();
        var provider = new SourceRouteTreeProvider(costs: costs);
        var tree = provider.Build(graph, Key("R000"));
        Assert.Equal(100, tree.Entries.Count);
        Assert.Equal(1, costs.RelayReads);
        Assert.False(tree.Entries[Key("R000")].Quantum);
        Assert.True(tree.Entries[Key("R099")].Quantum);
        Assert.Same(tree, provider.Build(graph, Key("R000")));
        Assert.Equal(1, costs.RelayReads);
    }

    /// <summary>Доступность и backbone меняют ключ кеша без перестройки структурного графа.</summary>
    [Fact]
    public void AvailabilityRevisionAndBackboneInvalidateTree()
    {
        var graph = Graph([Node("A"), Node("R1", relay: true), Node("R2", relay: true), Node("B")],
            ("A", "R1", 1), ("R2", "B", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        var provider = new SourceRouteTreeProvider(availability);
        var first = provider.Build(graph, Key("A"));
        Assert.Same(first, provider.Build(graph, Key("A")));
        Assert.True(first.Entries.ContainsKey(Key("B")));
        availability.SetBackbone(false);
        var disabled = provider.Build(graph, Key("A"));
        Assert.NotEqual(first.AvailabilityVersion, disabled.AvailabilityVersion);
        Assert.False(disabled.Entries.ContainsKey(Key("B")));
        availability.SetBackbone(true);
        availability.UpdateFact(Node("R2", online: false, relay: true));
        var offline = provider.Build(graph, Key("A"));
        Assert.True(offline.Entries.ContainsKey(Key("R2")));
        Assert.False(offline.Entries.ContainsKey(Key("B")));
        Assert.True(first.Entries.ContainsKey(Key("B")));
        Assert.Equal(1UL, provider.CacheHits);
        Assert.Equal(3UL, provider.CacheMisses);
    }

    /// <summary>Поколения, отсутствующие в графе или overlay, не становятся offline discovery узлами.</summary>
    [Fact]
    public void RemovedAndReplacedGenerationsAreExcluded()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")], ("A", "B", 1), ("B", "C", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        var provider = new SourceRouteTreeProvider(availability);
        availability.UpdateFact(Node("B", generation: 2));
        var tree = provider.Build(graph, Key("A"));
        Assert.Single(tree.Entries);
        Assert.Empty(tree.GetPath(Key("B")));
        Assert.Empty(tree.GetPath(Key("B", 2)));
        Assert.False(tree.Entries.ContainsKey(Key("C")));
        foreach (var source in new[] { Key("B"), Key("B", 2), Key("missing") })
        {
            var stale = provider.Build(graph, source);
            Assert.Equal(RouteStatus.StaleEndpoint, stale.Status);
            Assert.Empty(stale.Entries);
        }
        availability.Remove(Key("A"));
        Assert.Equal(RouteStatus.StaleEndpoint, provider.Build(graph, Key("A")).Status);
    }

    /// <summary>Одинаковые числовые версии разных snapshots и изменённая cost revision не используют чужие cached веса.</summary>
    [Fact]
    public void GraphIdentityAndCostRevisionParticipateInCacheKey()
    {
        var firstGraph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var secondGraph = Graph([Node("A"), Node("B")], ("A", "B", 9));
        var costs = new TestCosts();
        var provider = new SourceRouteTreeProvider(costs: costs);
        var first = provider.Build(firstGraph, Key("A"));
        Assert.Equal(1, first.Entries[Key("B")].LatencyDs);
        Assert.Equal(9, provider.Build(secondGraph, Key("A")).Entries[Key("B")].LatencyDs);
        Assert.Same(first, provider.Build(firstGraph, Key("A")));
        costs.SetFactor(4);
        Assert.Equal(4, provider.Build(firstGraph, Key("A")).Entries[Key("B")].LatencyDs);
        Assert.Equal(3UL, provider.CacheMisses);
        Assert.Equal(1UL, provider.CacheHits);
    }

    /// <summary>Пустые ответы разделяют bounded LRU с деревьями; hit обновляет порядок вытеснения.</summary>
    [Fact]
    public void LruCapacityIncludesNegativeTreesAndCanBeDisabled()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")], ("A", "B", 1));
        var provider = new SourceRouteTreeProvider(options: new(CacheCapacity: 2));
        var first = provider.Build(graph, Key("A"));
        var stale = provider.Build(graph, Key("missing"));
        Assert.Same(first, provider.Build(graph, Key("A")));
        provider.Build(graph, Key("B"));
        Assert.NotSame(stale, provider.Build(graph, Key("missing")));
        Assert.Equal(2, provider.CacheCount);
        for (var i = 0; i < 1000; i++)
            Assert.Empty(provider.Build(graph, Key("missing-" + i)).Entries);
        Assert.Equal(2, provider.CacheCount);

        var disabled = new SourceRouteTreeProvider(options: new(CacheCapacity: 0));
        Assert.NotSame(disabled.Build(graph, Key("A")), disabled.Build(graph, Key("A")));
        Assert.Equal(0, disabled.CacheCount);
        Assert.Equal(0UL, disabled.CacheHits);
        Assert.Equal(2UL, disabled.CacheMisses);
    }

    /// <summary>Ни entries, ни материализованный путь не предоставляют mutable backing collection потребителю.</summary>
    [Fact]
    public void EntriesAndMaterializedPathsAreIndependentReadOnlyCopies()
    {
        var entries = new Dictionary<NodeKey, SourceRouteEntry>
        {
            [Key("A")] = new(null, 0, false),
            [Key("B")] = new(Key("A"), 1, false)
        };
        var tree = new SourceRouteTree(Key("A"), RouteStatus.Found, 1, 7, 0, entries);
        entries.Clear();
        Assert.Equal(2, tree.Entries.Count);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<NodeKey, SourceRouteEntry>)tree.Entries).Clear());
        var path = tree.GetPath(Key("B"));
        Assert.Throws<NotSupportedException>(() => ((IList<NodeKey>)path).Clear());
        Assert.Equal([Key("A"), Key("B")], tree.GetPath(Key("B")));
        Assert.Equal([Key("A")], tree.GetPath(Key("A")));
    }

    /// <summary>Защита materialization завершает ошибкой циклическую или оборванную внутреннюю цепочку за bounded число шагов.</summary>
    [Fact]
    public void MalformedPredecessorChainsCannotLoopOrReturnForeignRoots()
    {
        var cycle = new SourceRouteTree(Key("A"), RouteStatus.Found, 1, 1, 0, new()
        {
            [Key("A")] = new(null, 0, false),
            [Key("B")] = new(Key("C"), 1, false),
            [Key("C")] = new(Key("B"), 2, false)
        });
        Assert.Equal("invalid_route_tree", Assert.Throws<RoutingException>(() => cycle.GetPath(Key("B"))).Code);
        var broken = new SourceRouteTree(Key("A"), RouteStatus.Found, 1, 1, 0, new()
        {
            [Key("B")] = new(Key("missing"), 1, false)
        });
        Assert.Equal("invalid_route_tree", Assert.Throws<RoutingException>(() => broken.GetPath(Key("B"))).Code);
        var foreign = new SourceRouteTree(Key("A"), RouteStatus.Found, 1, 1, 0, new()
        {
            [Key("B")] = new(null, 0, false)
        });
        Assert.Equal("invalid_route_tree", Assert.Throws<RoutingException>(() => foreign.GetPath(Key("B"))).Code);
    }

    /// <summary>Удерживаемое cached дерево и weak identity не продлевают жизнь исходной публикации графа.</summary>
    [Fact]
    public void CacheDoesNotRetainGraphSnapshots()
    {
        var provider = new SourceRouteTreeProvider();
        var (reference, tree) = BuildUnretainedGraph(provider);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive);
        Assert.Equal(2, tree.Entries.Count);
        Assert.Equal(1, provider.CacheCount);
        GC.KeepAlive(tree);
        GC.KeepAlive(provider);
    }

    /// <summary>Отдельный stack frame исключает случайное удержание локального graph JIT-компилятором во время GC проверки.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Reference, SourceRouteTree Tree) BuildUnretainedGraph(SourceRouteTreeProvider provider)
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        return (new WeakReference(graph), provider.Build(graph, Key("A")));
    }

    /// <summary>Неверные параметры и границы памяти отвергаются стабильными routing codes до публикации частичного дерева.</summary>
    [Fact]
    public void ConfigurationNodeAndQueueBoundsFailExplicitly()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")], ("A", "B", 1), ("A", "C", 1));
        Assert.Equal("route_node_capacity", Assert.Throws<RoutingException>(() =>
            new SourceRouteTreeProvider(options: new(MaxNodes: 2)).Build(graph, Key("A"))).Code);
        var queueLimited = new SourceRouteTreeProvider(options: new(MaxQueueEntries: 1));
        Assert.Equal("route_queue_capacity", Assert.Throws<RoutingException>(() => queueLimited.Build(graph, Key("A"))).Code);
        Assert.Equal(0, queueLimited.CacheCount);
        foreach (var options in new[] { new SourceTreeOptions(CacheCapacity: -1), new SourceTreeOptions(MaxNodes: 0),
                     new SourceTreeOptions(MaxQueueEntries: 0) })
            Assert.Equal("invalid_router_options", Assert.Throws<RoutingException>(() => new SourceRouteTreeProvider(options: options)).Code);
        Assert.Equal("invalid_route_kind", Assert.Throws<RoutingException>(() =>
            new SourceRouteTreeProvider().Build(graph, Key("A"), (RouteKind)100)).Code);
    }

    /// <summary>Неположительные и нечисловые custom веса не попадают в очередь полного поиска.</summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidCustomCostsFailWithSafeCode(double factor)
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var costs = new TestCosts();
        costs.SetFactor(factor);
        var provider = new SourceRouteTreeProvider(costs: costs);
        Assert.Equal("invalid_route_cost", Assert.Throws<RoutingException>(() => provider.Build(graph, Key("A"))).Code);
        Assert.Equal(0, provider.CacheCount);
    }

    /// <summary>Даже сумма двух конечных положительных custom стоимостей проверяется на overflow.</summary>
    [Fact]
    public void AccumulatedCostMustRemainFinite()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")], ("A", "B", 1), ("B", "C", 1));
        var costs = new TestCosts();
        costs.SetFactor(double.MaxValue);
        Assert.Equal("invalid_route_cost", Assert.Throws<RoutingException>(() =>
            new SourceRouteTreeProvider(costs: costs).Build(graph, Key("A"))).Code);
    }

    /// <summary>Изменение входной ревизии из callback не публикует смешанное дерево и не загрязняет cache.</summary>
    [Fact]
    public void PolicyRevisionChangeDuringBuildDiscardsResult()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var costs = new TestCosts();
        costs.OnPhysicalRead = () => costs.SetFactor(2);
        var provider = new SourceRouteTreeProvider(costs: costs);
        Assert.Equal("route_state_changed", Assert.Throws<RoutingException>(() => provider.Build(graph, Key("A"))).Code);
        Assert.Equal(0, provider.CacheCount);
        costs.OnPhysicalRead = null;
        Assert.Equal(2, provider.Build(graph, Key("A")).Entries[Key("B")].LatencyDs);
    }

    /// <summary>Reentrant пользовательская policy получает явный отказ; guard освобождается после исключения.</summary>
    [Fact]
    public void ReentrantPolicyCannotCorruptProviderWorkspace()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var costs = new TestCosts();
        var provider = new SourceRouteTreeProvider(costs: costs);
        costs.OnPhysicalRead = () => provider.Build(graph, Key("A"));
        Assert.Equal("route_reentrant", Assert.Throws<RoutingException>(() => provider.Build(graph, Key("A"))).Code);
        costs.OnPhysicalRead = null;
        Assert.Equal(2, provider.Build(graph, Key("A")).Entries.Count);
    }

    /// <summary>Оба режима и backbone варианты совпадают со всеми pair routes и независимой матрицей кратчайших путей.</summary>
    [Fact]
    public void FullTreesMatchFloydWarshallAndPairRouterForEverySource()
    {
        var random = new Random(768521);
        var facts = Enumerable.Range(0, 10).Select(i => Node(i.ToString(), online: i % 4 != 1,
            relay: i % 3 == 0, operational: i != 6)).ToArray();
        var links = new List<(string From, string To, int Cost)>();
        for (var i = 0; i < facts.Length; i++)
            for (var j = i + 1; j < facts.Length; j++)
                if (random.NextDouble() < 0.4)
                    links.Add((facts[i].NodeId, facts[j].NodeId, random.Next(1, 8)));
        var graph = Graph(facts, links.ToArray());
        var availability = new NetworkAvailability(facts);
        var trees = new SourceRouteTreeProvider(availability, options: new(CacheCapacity: 3));
        var router = new NetworkRouter(availability);
        foreach (var backbone in new[] { true, false })
        {
            availability.SetBackbone(backbone);
            foreach (var mode in new[] { RouteKind.Machine, RouteKind.Discovery })
            {
                var expected = Floyd(graph, facts, mode, backbone);
                for (var from = 0; from < facts.Length; from++)
                {
                    var tree = trees.Build(graph, Key(facts[from].NodeId), mode);
                    Assert.Equal(availability.Revision, tree.AvailabilityVersion);
                    for (var to = 0; to < facts.Length; to++)
                    {
                        var key = Key(facts[to].NodeId);
                        var route = router.FindRoute(graph, tree.Source, key, mode);
                        if (double.IsPositiveInfinity(expected[from, to]))
                        {
                            Assert.False(tree.Entries.ContainsKey(key));
                            Assert.Equal(RouteStatus.Unreachable, route.Status);
                            Assert.Empty(tree.GetPath(key));
                        }
                        else
                        {
                            Assert.Equal(RouteStatus.Found, tree.Status);
                            Assert.Equal(RouteStatus.Found, route.Status);
                            Assert.Equal(expected[from, to], tree.Entries[key].LatencyDs);
                            Assert.Equal(route.LatencyDs, tree.Entries[key].LatencyDs);
                            Assert.Equal(route.Path, tree.GetPath(key));
                        }
                    }
                }
            }
        }
        Assert.Equal(3, trees.CacheCount);
    }

    /// <summary>Независимый directed all-pairs oracle разрешает offline узлу быть целью, но не началом ребра.</summary>
    private static double[,] Floyd(GraphSnapshot graph, NodeFact[] facts, RouteKind mode, bool backbone)
    {
        var count = facts.Length;
        var costs = new double[count, count];
        for (var from = 0; from < count; from++)
            for (var to = 0; to < count; to++)
            {
                costs[from, to] = double.PositiveInfinity;
                if (from == to)
                {
                    if (mode == RouteKind.Discovery || facts[from].State.Online)
                        costs[from, to] = 0;
                    continue;
                }
                if (!facts[from].State.Online || mode == RouteKind.Machine && !facts[to].State.Online)
                    continue;
                var fromKey = Key(facts[from].NodeId, facts[from].Generation);
                var toKey = Key(facts[to].NodeId, facts[to].Generation);
                foreach (var edge in graph.PhysicalEdges)
                    if (edge.From == fromKey && edge.To == toKey || edge.From == toKey && edge.To == fromKey)
                        costs[from, to] = Math.Min(costs[from, to], edge.LatencyDs);
                if (backbone && graph.RelayBucket.Contains(fromKey) && graph.RelayBucket.Contains(toKey) &&
                    facts[from].State.Network!.RelayOperational && facts[to].State.Network!.RelayOperational)
                    costs[from, to] = Math.Min(costs[from, to], graph.RelayLatencyDs);
            }
        for (var via = 0; via < count; via++)
            for (var from = 0; from < count; from++)
                for (var to = 0; to < count; to++)
                    costs[from, to] = Math.Min(costs[from, to], costs[from, via] + costs[via, to]);
        return costs;
    }

    /// <summary>Ревизуемые веса и диагностические callbacks для проверки одноразового bucket и причинной стабильности.</summary>
    private sealed class TestCosts : IRouteCostPolicy
    {
        /// <summary>Множитель физических и quantum стоимостей, изменяемый только вместе с Revision.</summary>
        private double _factor = 1;
        /// <inheritdoc />
        public ulong Revision { get; private set; }
        /// <summary>Количество обращений к единой стоимости implicit bucket.</summary>
        public int RelayReads { get; private set; }
        /// <summary>Optional callback намеренно моделирует некорректную меняющую состояние пользовательскую policy.</summary>
        public Action? OnPhysicalRead { get; set; }
        /// <summary>Атомарно для последовательного теста меняет масштаб и версию.</summary>
        public void SetFactor(double factor) { _factor = factor; Revision++; }
        /// <inheritdoc />
        public double PhysicalCost(PhysicalEdge edge)
        {
            OnPhysicalRead?.Invoke();
            return edge.LatencyDs * _factor;
        }
        /// <inheritdoc />
        public double RelayCost(GraphSnapshot graph) { RelayReads++; return graph.RelayLatencyDs * _factor; }
    }
}
