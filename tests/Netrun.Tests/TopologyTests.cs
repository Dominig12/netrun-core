using Netrun.Core;
using Netrun.Core.Topology;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет геометрию, derived bindings, immutable publication и точность indexed mesh независимым oracle.</summary>
public sealed class TopologyTests
{
    /// <summary>Создаёт сырой native-факт; адрес специально независим от логического ID.</summary>
    private static NodeFact Node(string id, string address, int x, int y, int z = 1, string type = "server",
        string? area = "home", string? supplied = null, bool relay = false, ulong generation = 1,
        string department = "general", bool online = true) => new(id, generation, 1,
        new(address, type, new(x, y, z), "s", online, new(area, supplied ?? area, department, IsNtnetRelay: relay)));

    /// <summary>Индексирует существующие узлы без вычисления кабелей или связности на стороне источника.</summary>
    private static Dictionary<string, NodeFact> World(params NodeFact[] nodes) => nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);

    /// <summary>Задаёт исходный порядок этажей; helper не сортирует числовые Z.</summary>
    private static Dictionary<string, SectorFact> Sectors(params int[] levels) => new(StringComparer.Ordinal)
    {
        ["s"] = new("s", 1, 1, new ValueList<int>(levels.Length == 0 ? [1] : levels))
    };

    /// <summary>Создаёт ключ текущего поколения для удобства проверок публичных индексов.</summary>
    private static NodeKey Key(string id, ulong generation = 1) => new(id, generation);

    /// <summary>Связанные физические этажи измеряются порядком stack, а стоимость — тайлами на ds.</summary>
    [Fact]
    public void GeometryUsesOrderedStackRatherThanNumericZ()
    {
        var apc = Node("apc", "Z", 10, 10, 100, "apc");
        var leaf = Node("leaf", "A", 10, 10, 1);
        var graph = new NetworkTopology(new(LinkTilesPerDs: 1)).Build(World(apc, leaf), Sectors(100, 50, 1), 17);
        var edge = Assert.Single(graph.PhysicalEdges);
        Assert.Equal(2, edge.Distance);
        Assert.Equal(2, edge.LatencyDs);
        Assert.Equal(Key("leaf"), edge.From);
        Assert.Equal(Key("apc"), edge.To);
        Assert.Equal(17UL, graph.InputSequence);
        Assert.Equal(1UL, graph.Version);
    }

    /// <summary>APC питаемой native area побеждает ближайший физический APC чужой области, даже будучи offline.</summary>
    [Fact]
    public void SuppliedAreaWinsOverDistanceAndOnlineDoesNotRebind()
    {
        var native = Node("native", "B", 100, 100, type: "apc", area: "mounted-elsewhere", supplied: "home", online: false, department: "science");
        var foreign = Node("foreign", "A", 2, 1, type: "apc", supplied: "other");
        var graph = new NetworkTopology().Build(World(native, foreign, Node("leaf", "C", 1, 1)), Sectors(), 1);
        Assert.Equal(Key("native"), graph.Bindings[Key("leaf")]);
        Assert.Equal("dept_s:science", graph.LanIds[Key("leaf")]);
    }

    /// <summary>В равной геометрии выигрывает ordinal address, независимо от NodeId и порядка Dictionary.</summary>
    [Fact]
    public void NearestTieUsesAddressRatherThanLogicalId()
    {
        var graph = new NetworkTopology().Build(World(
            Node("a-id", "Z", 10, 10, type: "apc"), Node("z-id", "A", 10, 10, type: "apc"),
            Node("leaf", "M", 11, 10)), Sectors(), 1);
        Assert.Equal(Key("z-id"), graph.Bindings[Key("leaf")]);
    }

    /// <summary>Stable сохраняет привязку внутри area; Nearest является отдельной явно выбранной политикой.</summary>
    [Fact]
    public void StableAndAdaptiveBindingsHaveExplicitDifferentBehavior()
    {
        var first = Node("first", "A", 1, 1, type: "apc");
        var leaf = Node("leaf", "L", 100, 1);
        var stable = new NetworkTopology();
        var nearest = new NetworkTopology(new(BindingPolicy: BindingPolicy.Nearest));
        stable.Build(World(first, leaf), Sectors(), 1);
        nearest.Build(World(first, leaf), Sectors(), 1);
        var second = Node("second", "B", 99, 1, type: "apc");
        var moved = leaf with { State = leaf.State with { Position = new(98, 1, 1) } };
        Assert.Equal(Key("first"), stable.Build(World(first, second, moved), Sectors(), 2).Bindings[Key("leaf")]);
        Assert.Equal(Key("second"), nearest.Build(World(first, second, moved), Sectors(), 2).Bindings[Key("leaf")]);
        var crossedArea = moved with { State = moved.State with { Network = moved.State.Network! with { BaseAreaId = "next-area" } } };
        Assert.Equal(Key("second"), stable.Build(World(first, second, crossedArea), Sectors(), 3).Bindings[Key("leaf")]);
    }

    /// <summary>Пересозданный APC с прежним адресом не сохраняет identity прежнего anchor.</summary>
    [Fact]
    public void ReplacementGenerationInvalidatesOldBinding()
    {
        var builder = new NetworkTopology();
        var old = Node("apc", "A", 1, 1, type: "apc");
        var leaf = Node("leaf", "L", 100, 1);
        builder.Build(World(old, leaf), Sectors(), 1);
        var replacement = old with { Generation = 2 };
        var near = Node("near", "B", 99, 1, type: "apc");
        var graph = builder.Build(World(replacement, near, leaf), Sectors(), 2);
        Assert.Equal(Key("near"), graph.Bindings[Key("leaf")]);
        Assert.DoesNotContain(Key("apc", 1), graph.Nodes.Keys);
    }

    /// <summary>Bootstrap seed используется только при первом наблюдении поколения; последующее изменение seed не командует перепривязкой.</summary>
    [Fact]
    public void InitialAnchorIsOneTimeAndMustReferToValidApcGeneration()
    {
        var first = Node("first", "A", 1, 1, type: "apc");
        var near = Node("near", "B", 99, 1, type: "apc");
        var leaf = Node("leaf", "L", 100, 1);
        leaf = leaf with { State = leaf.State with { Network = leaf.State.Network! with { InitialAnchor = Key("first") } } };
        var builder = new NetworkTopology();
        Assert.Equal(Key("first"), builder.Build(World(first, near, leaf), Sectors(), 1).Bindings[Key("leaf")]);
        leaf = leaf with { State = leaf.State with { Network = leaf.State.Network! with { InitialAnchor = Key("near") } } };
        Assert.Equal(Key("first"), builder.Build(World(first, near, leaf), Sectors(), 2).Bindings[Key("leaf")]);
        var invalidSeed = leaf with { State = leaf.State with { Network = leaf.State.Network! with { InitialAnchor = Key("first", 2) } } };
        Assert.Equal(Key("near"), new NetworkTopology().Build(World(first, near, invalidSeed), Sectors(), 1).Bindings[Key("leaf")]);
    }

    /// <summary>Nullspace узлы остаются в реестре; только реальные расположенные relays входят в implicit bucket.</summary>
    [Fact]
    public void RelayBucketIsImplicitAndNullPositionNodesStayIsolated()
    {
        var apc = Node("apc", "A", 1, 1, type: "apc");
        var relay = Node("relay", "R", 2, 1, type: "gateway", relay: true, online: false);
        var ordinary = Node("gateway", "G", 3, 1, type: "gateway");
        var absent = Node("absent", "N", 1, 1, relay: true);
        absent = absent with { State = absent.State with { Position = null, SectorId = null } };
        var graph = new NetworkTopology().Build(World(apc, relay, ordinary, absent), Sectors(), 1);
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(Key("relay"), Assert.Single(graph.RelayBucket));
        Assert.Empty(graph.Adjacency[Key("absent")]);
        Assert.DoesNotContain(Key("absent"), graph.LanIds.Keys);
        Assert.Equal(Key("apc"), graph.Bindings[Key("relay")]);
        Assert.Equal(2, graph.PhysicalEdges.Count);
        Assert.All(graph.PhysicalEdges, edge => Assert.Equal(PhysicalLinkKind.Endpoint, edge.Kind));
    }

    /// <summary>Relay bucket соединяет разные секторы логически, сохраняя O(R) индекс без R² физических рёбер.</summary>
    [Fact]
    public void GlobalRelaysWithoutApcsDoNotMaterializePairEdges()
    {
        var nodes = World(Enumerable.Range(0, 30).Select(i =>
        {
            var node = Node("r" + i, i.ToString("D3"), i + 1, 1, z: i < 15 ? 1 : 2, type: "gateway", relay: true);
            return i < 15 ? node : node with { State = node.State with { SectorId = "s2" } };
        }).ToArray());
        var sectors = Sectors();
        sectors.Add("s2", new("s2", 1, 1, new ValueList<int>([2])));
        var graph = new NetworkTopology().Build(nodes, sectors, 1);
        Assert.Equal(30, graph.RelayBucket.Count);
        Assert.Empty(graph.PhysicalEdges);
        Assert.Empty(graph.Bindings);
        Assert.Equal(["pub:s", "pub:s2"], graph.LanIds.Values.Distinct().OrderBy(value => value, StringComparer.Ordinal));
    }

    /// <summary>Metadata-free legacy факты получают station fallback; явный внешний endpoint сохраняет ext-Z domain.</summary>
    [Fact]
    public void UnboundLanFallbackDoesNotInventPhysicalEdges()
    {
        var legacy = Node("legacy", "A", 1, 1);
        legacy = legacy with { State = legacy.State with { Network = null } };
        var external = Node("external", "B", 2, 1);
        external = external with { State = external.State with { Network = external.State.Network! with { IsStationLevel = false } } };
        var graph = new NetworkTopology().Build(World(legacy, external), Sectors(), 1);
        Assert.Equal("pub:s", graph.LanIds[Key("legacy")]);
        Assert.Equal("ext1", graph.LanIds[Key("external")]);
        Assert.Empty(graph.PhysicalEdges);
    }

    /// <summary>Snapshot не заимствует Dictionary и не отдаёт mutable arrays/dictionaries через read-only API.</summary>
    [Fact]
    public void PublishedCollectionsCannotBeMutatedOrChangedByLaterBuild()
    {
        var nodes = World(Node("a", "A", 1, 1, type: "apc"), Node("b", "B", 2, 1, relay: true));
        var builder = new NetworkTopology();
        var graph = builder.Build(nodes, Sectors(), 1);
        nodes.Clear();
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<NodeKey, NodeFact>)graph.Nodes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PhysicalEdge>)graph.PhysicalEdges).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NodeKey>)graph.RelayBucket).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PhysicalEdge>)graph.Adjacency[Key("a")]).Clear());
        var empty = builder.Build(nodes, Sectors(), 2);
        Assert.Empty(empty.Nodes);
        Assert.Equal(2UL, empty.Version);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Single(graph.PhysicalEdges);
    }

    /// <summary>Неудачная публикация сохраняет Current и прежние bindings для следующего корректного входа.</summary>
    [Fact]
    public void EdgeCapacityFailureDoesNotPublishPartialState()
    {
        var builder = new NetworkTopology(new(MaxPhysicalEdges: 1));
        var apc = Node("apc", "A", 1, 1, type: "apc");
        var leaf = Node("leaf", "L", 100, 1);
        var original = builder.Build(World(apc, leaf), Sectors(), 1);
        Assert.Equal("topology_edge_capacity", Assert.Throws<TopologyException>(() => builder.Build(
            World(apc, leaf, Node("new", "N", 99, 1, type: "apc")), Sectors(), 2)).Code);
        Assert.Same(original, builder.Current);
        var valid = builder.Build(World(apc, leaf), Sectors(), 2);
        Assert.Equal(Key("apc"), valid.Bindings[Key("leaf")]);
        Assert.Equal(2UL, valid.Version);
    }

    /// <summary>Отсутствующие/противоречивые физические факты и дубли адресов не подменяются предположениями.</summary>
    [Fact]
    public void InvalidFactsAndLimitsHaveExplicitSafeCodes()
    {
        var node = Node("a", "A", 1, 1);
        Assert.Equal("topology_missing_sector", Assert.Throws<TopologyException>(() =>
            new NetworkTopology().Build(World(node), new Dictionary<string, SectorFact>(), 1)).Code);
        Assert.Equal("topology_sector_position", Assert.Throws<TopologyException>(() =>
            new NetworkTopology().Build(World(node), Sectors(2), 1)).Code);
        Assert.Equal("topology_invalid_sector", Assert.Throws<TopologyException>(() =>
            new NetworkTopology().Build(World(node), Sectors(1, 1), 1)).Code);
        Assert.Equal("topology_duplicate_address", Assert.Throws<TopologyException>(() =>
            new NetworkTopology().Build(World(node, Node("b", "A", 2, 1)), Sectors(), 1)).Code);
        Assert.Equal("topology_node_capacity", Assert.Throws<TopologyException>(() =>
            new NetworkTopology(new(MaxNodes: 1)).Build(World(node, Node("b", "B", 2, 1)), Sectors(), 1)).Code);
        Assert.Equal("topology_input_regression", Assert.Throws<TopologyException>(() =>
        {
            var builder = new NetworkTopology();
            builder.Build(World(node), Sectors(), 2);
            builder.Build(World(node), Sectors(), 1);
        }).Code);
        TopologyOptions[] invalid = [new(ApcNeighbors: 0), new(LinkTilesPerDs: 0), new(RelayLatencyDs: 0),
            new(MaxNodes: 0), new(MaxPhysicalEdges: -1), new(BindingPolicy: (BindingPolicy)100)];
        foreach (var options in invalid)
            Assert.Equal("invalid_topology_options", Assert.Throws<TopologyException>(() => new NetworkTopology(options)).Code);
    }

    /// <summary>Balanced kd-tree совпадает с независимым полным перебором native preference/distance/address.</summary>
    [Fact]
    public void IndexedBindingsMatchBruteForceOracleAcrossAreasAndFloors()
    {
        var random = new Random(9876);
        int[] levels = [100, 1, 50];
        var apcs = Enumerable.Range(0, 80).Select(i => Node("a" + i, "A" + i.ToString("D3"), random.Next(1, 200),
            random.Next(1, 200), levels[random.Next(3)], "apc", "mounted", i % 2 == 0 ? "home" : "other")).ToArray();
        var leaves = Enumerable.Range(0, 50).Select(i => Node("l" + i, "L" + i.ToString("D3"), random.Next(1, 200),
            random.Next(1, 200), levels[random.Next(3)], area: i % 3 == 0 ? "no-native" : "home")).ToArray();
        var graph = new NetworkTopology().Build(World(apcs.Concat(leaves).Reverse().ToArray()), Sectors(levels), 1);
        foreach (var leaf in leaves)
        {
            var native = apcs.Where(apc => apc.State.Network!.SuppliedAreaId == leaf.State.Network!.BaseAreaId).ToArray();
            var expected = (native.Length > 0 ? native : apcs).OrderBy(apc => BruteDistance(leaf, apc, levels))
                .ThenBy(apc => apc.State.Address, StringComparer.Ordinal).First();
            Assert.Equal(Key(expected.NodeId), graph.Bindings[Key(leaf.NodeId)]);
        }
    }

    /// <summary>Индексированный mesh + Boruvka даёт тот же полный набор кабелей, что brute KNN + exhaustive Prim.</summary>
    [Fact]
    public void IndexedMeshMatchesIndependentBrutePrimIncludingTiesAndDisconnectedClusters()
    {
        var random = new Random(12);
        var apcs = Enumerable.Range(0, 32).Select(i => Node("n" + i, i.ToString("D3"),
            1 + i / 8 * 100 + random.Next(0, 4), 1 + random.Next(0, 4), type: "apc")).ToArray();
        var graph = new NetworkTopology(new(ApcNeighbors: 2)).Build(World(apcs.Reverse().ToArray()), Sectors(), 1);
        var expected = BruteMesh(apcs, 2);
        var actual = graph.PhysicalEdges.Select(edge => Pair(graph.Nodes[edge.From], graph.Nodes[edge.To])).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), actual.OrderBy(value => value, StringComparer.Ordinal));
        Assert.All(graph.PhysicalEdges, edge => Assert.Equal(PhysicalLinkKind.Apc, edge.Kind));
    }

    /// <summary>Независимое расстояние oracle использует линейный поиск floor positions, без production spatial index.</summary>
    private static int BruteDistance(NodeFact first, NodeFact second, int[] levels)
    {
        var left = first.State.Position!;
        var right = second.State.Position!;
        return Math.Max(Math.Max(Math.Abs(left.X - right.X), Math.Abs(left.Y - right.Y)),
            Math.Abs(Array.IndexOf(levels, left.Z) - Array.IndexOf(levels, right.Z)));
    }

    /// <summary>Канонический адрес пары позволяет сравнивать наборы без зависимости от production edge records.</summary>
    private static string Pair(NodeFact first, NodeFact second) => StringComparer.Ordinal.Compare(first.State.Address, second.State.Address) < 0
        ? first.State.Address + ">" + second.State.Address : second.State.Address + ">" + first.State.Address;

    /// <summary>Полный O(N²) тестовый oracle: direct nearest union, затем exhaustive cut-minimum Prim между компонентами.</summary>
    private static HashSet<string> BruteMesh(NodeFact[] nodes, int neighbors)
    {
        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var other in nodes.Where(other => other != node).OrderBy(other => BruteDistance(node, other, [1]))
                .ThenBy(other => other.State.Address, StringComparer.Ordinal).Take(neighbors))
                edges.Add(Pair(node, other));
        var connected = new HashSet<string>(StringComparer.Ordinal) { nodes[0].NodeId };
        Expand();
        while (connected.Count < nodes.Length)
        {
            var candidates = from first in nodes
                             where connected.Contains(first.NodeId)
                             from second in nodes
                             where !connected.Contains(second.NodeId)
                             let pair = Pair(first, second)
                             select (First: first, Second: second, Pair: pair);
            var candidate = candidates.OrderBy(candidate => BruteDistance(candidate.First, candidate.Second, [1]))
                .ThenBy(candidate => candidate.Pair, StringComparer.Ordinal).First();
            edges.Add(candidate.Pair);
            connected.Add(candidate.Second.NodeId);
            Expand();
        }
        return edges;

        // BFS замыкание текущей связной компоненты по уже созданным кабелям, без union-find production алгоритма.
        void Expand()
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var first in nodes.Where(first => connected.Contains(first.NodeId)).ToArray())
                    foreach (var second in nodes)
                        if (!connected.Contains(second.NodeId) && edges.Contains(Pair(first, second)))
                            changed |= connected.Add(second.NodeId);
            } while (changed);
        }
    }
}
