using Netrun.Core;
using Netrun.Core.Communication;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет отдельную native capability без добавления edges в обычный physical graph.</summary>
public sealed class CommunicationsTests
{
    /// <summary>Синтетический node не становится NTnet relay из-за native telecomms port role.</summary>
    private static NodeFact Node(string id, ulong generation = 1) => new(id, generation, 1,
        new(id, "server", new(1, 1, 1), "s", true, new(null, null)));

    /// <summary>Точное поколение физического link endpoint.</summary>
    private static NodeKey Key(string id, ulong generation = 1) => new(id, generation);

    /// <summary>Создаёт допустимый native port с явным направлением configured links.</summary>
    private static CommunicationPortFact Port(string id, CommunicationPortKind kind, string[] links,
        bool receiving = true, bool broadcasting = true, int z = 1, bool longRange = false,
        int[]? frequencies = null, ulong generation = 1) => new(Key(id, generation), kind, true, true, true, z,
        longRange, receiving, broadcasting, new ValueList<int>(frequencies ?? []),
        new ValueList<NodeKey>(links.Select(link => Key(link))));

    /// <summary>Текущий world reader с изменяемой тестовой registry для проверки revocation между запросами.</summary>
    private sealed class World : INetworkWorldView
    {
        /// <summary>Актуальные physical facts, независимые от immutable graph fixture.</summary>
        internal readonly Dictionary<string, NodeFact> CurrentNodes;
        /// <summary>Текущие source/destination service instances.</summary>
        internal readonly Dictionary<string, ServiceFact> CurrentServices;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => CurrentNodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors { get; } = new Dictionary<string, SectorFact>();
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services => CurrentServices;

        /// <summary>Не создаёт native links: service families только включают отдельную capability provider.</summary>
        internal World(NodeFact[] facts, string source, string destination)
        {
            CurrentNodes = facts.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            CurrentServices = new(StringComparer.Ordinal)
            {
                ["source"] = new("source", 1, 1, Key(source), "telecomms.origin", null, true, new ValueList<OperationContract>([])),
                ["destination"] = new("destination", 1, 1, Key(destination), "telecomms.receiver", null, true, new ValueList<OperationContract>([]))
            };
        }
    }

    /// <summary>Строит explicit physical fixture; native configured links появляются только через отдельный catalog.</summary>
    private static GraphSnapshot Graph(NodeFact[] facts, params (string From, string To, int Cost)[] links)
    {
        var nodes = facts.ToDictionary(fact => Key(fact.NodeId, fact.Generation));
        var addresses = nodes.ToDictionary(pair => pair.Value.State.Address, pair => pair.Key, StringComparer.Ordinal);
        var edges = links.Select(link => new PhysicalEdge(addresses[link.From], addresses[link.To], PhysicalLinkKind.Endpoint, 1, link.Cost)).ToArray();
        var adjacency = nodes.Keys.ToDictionary(key => key, _ => new List<PhysicalEdge>());
        foreach (var edge in edges)
        {
            adjacency[edge.From].Add(edge);
            adjacency[edge.To].Add(edge);
        }
        return new GraphSnapshot(1, 1, nodes, addresses, edges, adjacency, [], [], [], 3);
    }

    /// <summary>Два физически разорванных сегмента a—r и h—d с одним native r→h bridge.</summary>
    private static (GraphSnapshot Graph, World World, CommunicationPortCatalog Ports) Fixture(bool ordinaryPath = false)
    {
        NodeFact[] nodes = [Node("a"), Node("r"), Node("h"), Node("d")];
        var graph = ordinaryPath ? Graph(nodes, ("a", "r", 1), ("h", "d", 1), ("a", "d", 20)) :
            Graph(nodes, ("a", "r", 1), ("h", "d", 1));
        var ports = new CommunicationPortCatalog();
        ports.Replace([Port("r", CommunicationPortKind.Relay, ["h"]), Port("h", CommunicationPortKind.Hub, [])]);
        return (graph, new World(nodes, "a", "d"), ports);
    }

    /// <summary>Удобный вызов явного communications channel; обычный router такую capability не получает.</summary>
    private static RouteResult Find(CommunicationRouter router, GraphSnapshot graph, World world, int? frequency = null) =>
        router.FindRoute(graph, new("source", 1), new("destination", 1), frequency, world);

    /// <summary>Relay receiving и hub broadcasting — разные направления, каждое требует собственного configured link.</summary>
    [Fact]
    public void NativeLinksAreDirectionalAndRequireTheirOwnRelaySwitch()
    {
        var (graph, world, ports) = Fixture();
        var router = new CommunicationRouter(ports);
        var forward = Find(router, graph, world);
        Assert.Equal(RouteStatus.Found, forward.Status);
        Assert.Equal(5, forward.LatencyDs);
        Assert.Equal([Key("a"), Key("r"), Key("h"), Key("d")], forward.Path);
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, new("destination", 1), new("source", 1), null, world).Status);
        ports.Upsert(Port("h", CommunicationPortKind.Hub, ["r"]));
        ports.Upsert(Port("r", CommunicationPortKind.Relay, ["h"], broadcasting: false));
        Assert.Equal(RouteStatus.Unreachable, router.FindRoute(graph, new("destination", 1), new("source", 1), null, world).Status);
        ports.Upsert(Port("r", CommunicationPortKind.Relay, ["h"]));
        Assert.Equal(RouteStatus.Found, router.FindRoute(graph, new("destination", 1), new("source", 1), null, world).Status);
        ports.Upsert(Port("r", CommunicationPortKind.Relay, ["h"], receiving: false));
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
    }

    /// <summary>Отключение любого native switch/hardware конца закрывает bridge без graph rebuild.</summary>
    [Theory]
    [InlineData(false, "on")]
    [InlineData(false, "toggled")]
    [InlineData(false, "operational")]
    [InlineData(true, "on")]
    [InlineData(true, "toggled")]
    [InlineData(true, "operational")]
    public void BothNativePortsMustBeEnabledAndOperational(bool target, string setting)
    {
        var (graph, world, ports) = Fixture();
        var owner = target ? Key("h") : Key("r");
        var port = ports.Snapshot.Ports[owner];
        ports.Upsert(setting switch
        {
            "on" => port with { On = false },
            "toggled" => port with { Toggled = false },
            _ => port with { Operational = false }
        });
        Assert.Equal(RouteStatus.Unreachable, Find(new CommunicationRouter(ports), graph, world).Status);
    }

    /// <summary>Hardware Operational не отменяет full node Online; offline intermediate не forwarding.</summary>
    [Fact]
    public void OfflineUntilOnIntermediatePortCannotBeBypassed()
    {
        var (graph, world, ports) = Fixture();
        var availability = new NetworkAvailability(world.Nodes.Values);
        var router = new CommunicationRouter(ports, availability);
        Assert.Equal(RouteStatus.Found, Find(router, graph, world).Status);
        availability.UpdateFact(world.Nodes["r"] with { State = world.Nodes["r"].State with { Online = false } });
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
        availability.UpdateFact(world.Nodes["r"]);
        Assert.Equal(RouteStatus.Found, Find(router, graph, world).Status);
    }

    /// <summary>Применяются оба listening filters; null control frequency обходит только фильтры, не остальные native gates.</summary>
    [Fact]
    public void FrequencyMustPassBothEndpointsUnlessControlFrame()
    {
        var (graph, world, ports) = Fixture();
        ports.Upsert(Port("r", CommunicationPortKind.Relay, ["h"], frequencies: [1459]));
        ports.Upsert(Port("h", CommunicationPortKind.Hub, [], frequencies: [1461]));
        var router = new CommunicationRouter(ports);
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world, 1459).Status);
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world, 1461).Status);
        Assert.Equal(RouteStatus.Found, Find(router, graph, world).Status);
        ports.Upsert(Port("h", CommunicationPortKind.Hub, [], frequencies: [1459]));
        Assert.Equal(RouteStatus.Found, Find(router, graph, world, 1459).Status);
        ports.Upsert(Port("r", CommunicationPortKind.Relay, ["h"]));
        ports.Upsert(Port("h", CommunicationPortKind.Hub, []));
        Assert.Equal(RouteStatus.Found, Find(router, graph, world, int.MaxValue).Status);
    }

    /// <summary>Различные NativeZ требуют LongRange хотя бы одного конца; native Z=0 не заменяется physical turf Z.</summary>
    [Fact]
    public void CrossNativeZRequiresExplicitLongRange()
    {
        var (graph, world, ports) = Fixture();
        ports.Upsert(Port("h", CommunicationPortKind.Hub, [], z: 2));
        var router = new CommunicationRouter(ports);
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
        ports.Upsert(Port("h", CommunicationPortKind.Hub, [], z: 2, longRange: true));
        Assert.Equal(RouteStatus.Found, Find(router, graph, world).Status);
        ports.Upsert(Port("h", CommunicationPortKind.Hub, [], z: 0));
        ports.Upsert(Port("r", CommunicationPortKind.Relay, ["h"], z: 0));
        Assert.Equal(RouteStatus.Found, Find(router, graph, world).Status);
    }

    /// <summary>Доступный ordinary путь всегда предпочтителен даже более дешёвому native bridge; общий graph не меняется.</summary>
    [Fact]
    public void OrdinaryMachinePathHasAbsolutePriority()
    {
        var (graph, world, ports) = Fixture(ordinaryPath: true);
        var router = new CommunicationRouter(ports);
        var result = Find(router, graph, world);
        Assert.Equal(20, result.LatencyDs);
        Assert.Equal([Key("a"), Key("d")], result.Path);
        Assert.Equal(0UL, router.CacheMisses);
        Assert.Equal(3, graph.PhysicalEdges.Count);
        world.CurrentServices["source"] = world.CurrentServices["source"] with { Family = "research.client" };
        Assert.Equal(20, Find(router, graph, world).LatencyDs);
    }

    /// <summary>При latency равенстве физический A→X→B сегмент выигрывает у native A→B через native-hop secondary score.</summary>
    [Fact]
    public void EqualLatencyPrefersFewerNativeHopsWithoutPairRouteSearches()
    {
        NodeFact[] nodes = [Node("A"), Node("X"), Node("B"), Node("C"), Node("D")];
        var graph = Graph(nodes, ("A", "X", 1), ("X", "B", 2), ("C", "D", 1));
        var world = new World(nodes, "A", "D");
        var ports = new CommunicationPortCatalog();
        ports.Replace([Port("A", CommunicationPortKind.Relay, ["B"]),
            Port("B", CommunicationPortKind.Hub, ["C"]), Port("C", CommunicationPortKind.Relay, [])]);
        var result = Find(new CommunicationRouter(ports), graph, world);
        Assert.Equal(7, result.LatencyDs);
        Assert.Equal([Key("A"), Key("X"), Key("B"), Key("C"), Key("D")], result.Path);
    }

    /// <summary>Ни arbitrary service family, ни stale service/node generation не получают привилегированный fallback.</summary>
    [Fact]
    public void BothServiceFamiliesAndExactGenerationsAreRequired()
    {
        var (graph, world, ports) = Fixture();
        var router = new CommunicationRouter(ports);
        var source = world.CurrentServices["source"];
        world.CurrentServices["source"] = source with { Family = "research.client" };
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
        world.CurrentServices["source"] = source;
        var destination = world.CurrentServices["destination"];
        world.CurrentServices["destination"] = destination with { Family = "atmos.device" };
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
        world.CurrentServices["destination"] = destination with { Generation = 2 };
        Assert.Equal(RouteStatus.StaleEndpoint, Find(router, graph, world).Status);
        world.CurrentServices["destination"] = destination;
        world.CurrentNodes["a"] = Node("a", generation: 2);
        Assert.Equal(RouteStatus.StaleEndpoint, Find(router, graph, world).Status);
    }

    /// <summary>Catalog revocation/generation invalidates cached bridge; повтор owner address не восстанавливает старые configured references.</summary>
    [Fact]
    public void PortRevocationAndReplacementInvalidateFallbackCache()
    {
        var (graph, world, ports) = Fixture();
        var router = new CommunicationRouter(ports);
        Assert.Equal(RouteStatus.Found, Find(router, graph, world).Status);
        Find(router, graph, world);
        Assert.Equal(1UL, router.CacheHits);
        ports.Remove(Key("h"));
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
        ports.Upsert(Port("h", CommunicationPortKind.Hub, [], generation: 2));
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
        Assert.False(ports.Upsert(Port("h", CommunicationPortKind.Hub, [])));
        Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world).Status);
        Assert.Equal(3UL, router.CacheMisses);
    }

    /// <summary>Частота является cache identity, а positive и negative результаты разделяют один bounded LRU.</summary>
    [Fact]
    public void FrequencyAndPortRevisionArePartOfBoundedCacheIdentity()
    {
        var (graph, world, ports) = Fixture();
        var router = new CommunicationRouter(ports, options: new(CacheCapacity: 2));
        for (var frequency = 1; frequency <= 10; frequency++)
            Assert.Equal(RouteStatus.Found, Find(router, graph, world, frequency).Status);
        Assert.Equal(2, router.CacheCount);
        ports.Remove(Key("h"));
        for (var frequency = 1; frequency <= 10; frequency++)
            Assert.Equal(RouteStatus.Unreachable, Find(router, graph, world, frequency).Status);
        Assert.Equal(2, router.CacheCount);
        Assert.Equal(20UL, router.CacheMisses);
    }

    /// <summary>Reader и set lists неизменяемы; reorder того же bounded множества не инвалидирует Revision.</summary>
    [Fact]
    public void CatalogSnapshotsAreImmutableAndSetOrderIsCanonical()
    {
        var ports = new CommunicationPortCatalog();
        var first = Port("r", CommunicationPortKind.Relay, ["z", "a"], frequencies: [2, 1]);
        ports.Upsert(first);
        var snapshot = ports.Snapshot;
        var revision = ports.Revision;
        Assert.False(ports.Upsert(Port("r", CommunicationPortKind.Relay, ["a", "z"], frequencies: [1, 2])));
        Assert.Equal(revision, ports.Revision);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<NodeKey, CommunicationPortFact>)snapshot.Ports).Clear());
        Assert.False((object)snapshot.Ports[Key("r")].ConfiguredDirectedLinks is IList<NodeKey>);
        ports.Remove(Key("r"));
        Assert.Single(snapshot.Ports);
        Assert.Empty(ports.Snapshot.Ports);
    }

    /// <summary>Недопустимый raw catalog не публикует часть Replace и не раскрывает входные identifiers.</summary>
    [Fact]
    public void CatalogAndQueryLimitsFailExplicitlyAndAtomically()
    {
        var ports = new CommunicationPortCatalog(maxPorts: 1, maxLinksPerPort: 1);
        var relay = Port("r", CommunicationPortKind.Relay, ["h"]);
        Assert.True(CommunicationPortCatalog.IsValid(relay));
        Assert.False(CommunicationPortCatalog.IsValid(null));
        Assert.False(CommunicationPortCatalog.IsValid(relay with { ListeningFrequencies = new ValueList<int>([1, 1]) }));
        ports.Upsert(relay);
        var revision = ports.Revision;
        Assert.Equal("communication_port_capacity", Assert.Throws<CommunicationException>(() =>
            ports.Replace([relay, Port("h", CommunicationPortKind.Hub, [])])).Code);
        Assert.Equal(revision, ports.Revision);
        Assert.Single(ports.Snapshot.Ports);
        Assert.Equal("invalid_communication_port", Assert.Throws<CommunicationException>(() =>
            ports.Upsert(relay with { ListeningFrequencies = new ValueList<int>([1, 1]) })).Code);
        Assert.Equal("invalid_communication_port", Assert.Throws<CommunicationException>(() =>
            ports.Upsert(relay with { NativeZ = -1 })).Code);
        Assert.Equal("invalid_communication_port", Assert.Throws<CommunicationException>(() =>
            ports.Upsert(Port("r", CommunicationPortKind.Relay, ["h", "z"]))).Code);
        Assert.Equal("invalid_communication_options", Assert.Throws<CommunicationException>(() =>
            new CommunicationRouter(ports, options: new(NativeLatencyDs: double.NaN))).Code);
        var fixture = Fixture();
        Assert.Equal("invalid_communication_frequency", Assert.Throws<CommunicationException>(() =>
            Find(new CommunicationRouter(fixture.Ports), fixture.Graph, fixture.World, 0)).Code);
    }
}
