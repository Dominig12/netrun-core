using System.Globalization;
using Netrun.Core;
using Netrun.Core.Discovery;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет tree-backed discovery по независимому pair oracle и числу реальных source solves.</summary>
public sealed class DiscoveryReachabilityTests
{
    /// <summary>Опубликованные факты fixture без native references и дополнительных критериев отбора.</summary>
    private sealed class World : INetworkWorldView
    {
        /// <summary>Изменяемые узлы позволяют отдельно проверить availability и structural invalidation.</summary>
        internal Dictionary<string, NodeFact> CurrentNodes { get; } = new(StringComparer.Ordinal);
        /// <summary>Network-eligible сервисы большого семейства.</summary>
        internal Dictionary<string, ServiceFact> CurrentServices { get; } = new(StringComparer.Ordinal);
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => CurrentNodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services => CurrentServices;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors { get; } = new Dictionary<string, SectorFact>();
    }

    /// <summary>Oracle доказывает выбор fast API: попытка получить полный путь завершает тест ошибкой.</summary>
    private sealed class ReachabilityOnly : IRouteProvider, IRouteReachabilityProvider
    {
        /// <summary>Фактически выбранные discovery политики.</summary>
        internal List<RouteKind> Modes { get; } = [];
        /// <inheritdoc />
        public bool IsReachable(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine)
        {
            Modes.Add(mode);
            return true;
        }
        /// <inheritdoc />
        public RouteResult FindRoute(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine) =>
            throw new InvalidOperationException("Discovery must not materialize a path.");
    }

    /// <summary>Создаёт online/offline fact с независимыми native relay признаками.</summary>
    private static NodeFact Node(string id, bool online = true, bool relay = false, bool operational = true,
        bool placed = true, ulong generation = 1) => new(id, generation, 1,
        new(id, "server", placed ? new Position(1, 1, 1) : null, "sector", online,
            new(null, null, IsNtnetRelay: relay, RelayOperational: operational)));

    /// <summary>Стандартное точное поколение fixture.</summary>
    private static NodeKey Key(string id) => new(id, 1);

    /// <summary>Собирает undirected graph независимо от алгоритма геометрической топологии.</summary>
    private static GraphSnapshot Graph(IEnumerable<NodeFact> facts, params (string From, string To, int Cost)[] links)
    {
        var nodes = facts.ToDictionary(node => new NodeKey(node.NodeId, node.Generation));
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
        return new(1, 1, nodes, addresses, edges, adjacency, relays, [], [], 3);
    }

    /// <summary>Объявляет valid service с обычной read-only операцией.</summary>
    private static ServiceFact Service(string id, string owner, string family) =>
        new(id, 1, 1, Key(owner), family, null, true, new([new("read", OperationSafety.ReadOnly)], 64));

    /// <summary>Снимает все bounded страницы, учитывая отдельно candidate work и terminal code.</summary>
    private static (int Candidates, int Work, string Code) Run(ServiceDiscovery discovery, ulong sequence, bool communications = false)
    {
        Assert.Equal(AdmissionStatus.Accepted,
            discovery.Submit(new(new("query", sequence), new("source", 1), "authority", Communications: communications)).Status);
        var candidates = 0;
        var work = 0;
        for (var slice = 0; slice < 1000; slice++)
        {
            work += discovery.Advance(64);
            while (discovery.TryDequeueEvent(out var page))
            {
                candidates += page!.Candidates.Count;
                if (page.Complete) return (candidates, work, page.Code);
            }
        }
        throw new InvalidOperationException("Discovery did not finish within its bounded fixture.");
    }

    /// <summary>Self/offline/transit/quantum/tie/generation результаты совпадают с независимым pair router.</summary>
    [Fact]
    public void FastAdapterMatchesPairPolicyForEveryEndpointAndMode()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C"), Node("D"), Node("R1", relay: true),
            Node("R2", relay: true), Node("offline", online: false), Node("behind"),
            Node("broken", relay: true, operational: false), Node("unplaced", relay: true, placed: false)],
            ("A", "B", 1), ("A", "C", 1), ("B", "D", 1), ("C", "D", 1),
            ("D", "R1", 1), ("R2", "offline", 1), ("offline", "behind", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        var pair = new NetworkRouter(availability);
        var adapter = new SourceTreeRouteProvider(new SourceRouteTreeProvider(availability), availability);
        Check();
        availability.SetBackbone(false);
        Check();
        availability.SetBackbone(true);
        availability.UpdateFact(Node("R2", online: false, relay: true));
        availability.UpdateFact(Node("C", generation: 2));
        Check();

        // Каждый режим использует ordinal predecessor B при равном весе до D.
        Assert.Equal([Key("A"), Key("B"), Key("D")], adapter.FindRoute(graph, Key("A"), Key("D")).Path);

        // Сравнение всех пар включает несуществующий endpoint и поколение replacement вне graph.
        void Check()
        {
            var endpoints = graph.Nodes.Keys.Concat([Key("missing"), new NodeKey("C", 2)]).ToArray();
            foreach (var mode in Enum.GetValues<RouteKind>())
                foreach (var source in endpoints)
                    foreach (var destination in endpoints)
                    {
                        var expected = pair.FindRoute(graph, source, destination, mode);
                        Assert.Equal(expected.Status == RouteStatus.Found, adapter.IsReachable(graph, source, destination, mode));
                        var actual = adapter.FindRoute(graph, source, destination, mode);
                        Assert.Equal(expected.Status, actual.Status);
                        Assert.Equal(expected.LatencyDs, actual.LatencyDs);
                        Assert.Equal(expected.Path, actual.Path);
                    }
        }
    }

    /// <summary>4096 кандидатов требуют один source solve; graph identity и availability меняют кеш только один раз на query.</summary>
    [Fact]
    public void LargeFamilyReusesOneSolveAndInvalidatesForGraphAndAvailability()
    {
        const int count = 4096;
        var world = new World();
        world.CurrentNodes["origin"] = Node("origin");
        world.CurrentNodes["hub"] = Node("hub");
        world.CurrentServices["source"] = Service("source", "origin", "origin");
        var links = new List<(string From, string To, int Cost)> { ("origin", "hub", 1) };
        for (var i = 0; i < count; i++)
        {
            var id = "target-" + i.ToString("D4", CultureInfo.InvariantCulture);
            world.CurrentNodes[id] = Node(id);
            world.CurrentServices[id] = Service(id, id, "authority");
            links.Add(("hub", id, 1));
        }
        var directory = new ServiceDirectory();
        foreach (var fact in world.Services.Values) Assert.Equal(DirectoryUpdateResult.Applied, directory.Upsert(fact));
        var graph = Graph(world.Nodes.Values, links.ToArray());
        var availability = new NetworkAvailability(world.Nodes.Values);
        var trees = new SourceRouteTreeProvider(availability);
        using var discovery = new ServiceDiscovery(world, directory, () => graph,
            new SourceTreeRouteProvider(trees, availability), "binding");

        Assert.Equal((count, count, "completed"), Run(discovery, 1));
        Assert.Equal(1UL, trees.CacheMisses);
        Assert.Equal((ulong)(count - 1), trees.CacheHits);
        Assert.Equal((count, count, "completed"), Run(discovery, 2));
        Assert.Equal(1UL, trees.CacheMisses);

        // Forwarder меняет только overlay, сохраняя graph и все eligible service endpoints.
        availability.UpdateFact(Node("hub", online: false));
        Assert.Equal((0, count, "completed"), Run(discovery, 3));
        Assert.Equal(2UL, trees.CacheMisses);
        availability.UpdateFact(Node("hub"));
        Assert.Equal((count, count, "completed"), Run(discovery, 4));
        Assert.Equal(3UL, trees.CacheMisses);

        // Даже одинаковые numeric graph versions разных публикаций не переиспользуют старое дерево.
        graph = Graph(world.Nodes.Values, links.ToArray());
        Assert.Equal((count, count, "completed"), Run(discovery, 5));
        Assert.Equal(4UL, trees.CacheMisses);
        Assert.Equal(4, trees.CacheCount);
    }

    /// <summary>Discovery не обращается к path API fast provider и сохраняет различие ordinary и communication режимов.</summary>
    [Theory]
    [InlineData(false, RouteKind.Discovery)]
    [InlineData(true, RouteKind.Machine)]
    public void DiscoveryChoosesFastInterfaceWithoutMaterializingPaths(bool communications, RouteKind expected)
    {
        var world = new World();
        world.CurrentNodes["origin"] = Node("origin");
        world.CurrentNodes["target"] = Node("target");
        world.CurrentServices["source"] = Service("source", "origin", "origin");
        world.CurrentServices["target"] = Service("target", "target", "authority");
        var directory = new ServiceDirectory();
        foreach (var fact in world.Services.Values) directory.Upsert(fact);
        var graph = Graph(world.Nodes.Values);
        var routes = new ReachabilityOnly();
        using var discovery = new ServiceDiscovery(world, directory, () => graph, routes, "binding");
        Assert.Equal((1, 1, "completed"), Run(discovery, 1, communications));
        Assert.Equal(expected, Assert.Single(routes.Modes));
    }
}
