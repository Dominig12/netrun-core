using System.Runtime.CompilerServices;
using Netrun.Core;
using Netrun.Core.Projection;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет persistent baseline/delta stream, явный application ACK и fine-grained route authority при leaf churn.</summary>
public sealed class GraphSynchronizationTests
{
    /// <summary>Live source facts, независимые от frozen GraphSnapshot и retained client read model.</summary>
    private sealed class World : INetworkWorldView
    {
        /// <summary>Current exact physical instances.</summary>
        internal Dictionary<string, NodeFact> CurrentNodes { get; } = new(StringComparer.Ordinal);
        /// <summary>Один связанный native floor stack.</summary>
        internal Dictionary<string, SectorFact> CurrentSectors { get; } = new(StringComparer.Ordinal)
        {
            ["s"] = new("s", 1, 1, new([1]))
        };
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => CurrentNodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors => CurrentSectors;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services { get; } = new Dictionary<string, ServiceFact>();
    }

    /// <summary>Настоящие topology/availability/tree dependencies; тест не реализует route solver повторно.</summary>
    private sealed class Scene : IDisposable
    {
        /// <summary>Mutable published facts.</summary>
        internal World World { get; } = new();
        /// <summary>Retained stable binding builder.</summary>
        internal NetworkTopology Topology { get; } = new();
        /// <summary>Current ready graph либо null для pending.</summary>
        internal GraphSnapshot? Graph;
        /// <summary>Current availability overlay, который использует SourceRouteTreeProvider.</summary>
        internal NetworkAvailability Availability { get; }
        /// <summary>Проверяемый O(touched) proof ledger.</summary>
        internal GraphAuthorityTracker Authority { get; }
        /// <summary>Проверяемый persistent graph stream.</summary>
        internal GraphSynchronizationBroker Broker { get; }
        /// <summary>Один source tree на query без pair-route enumeration.</summary>
        internal SourceRouteTreeProvider Trees { get; }
        /// <summary>Монотонная structural input boundary fixture.</summary>
        private ulong _sequence = 1;

        /// <summary>Один APC и указанное число обычных degree-one endpoints.</summary>
        internal Scene(int leaves = 3, GraphSynchronizationOptions? options = null)
        {
            World.CurrentNodes["apc"] = Node("apc", "A", 1, "apc");
            for (var i = 0; i < leaves; i++) World.CurrentNodes[Leaf(i)] = Node(Leaf(i), Address(i), i + 2);
            Graph = Topology.Build(World.Nodes, World.Sectors, _sequence);
            Availability = new(World.Nodes.Values);
            Authority = new(World);
            Authority.Reset();
            Trees = new(Availability, options: new(CacheCapacity: 0));
            Broker = new(() => Graph, Authority, "binding", options);
        }

        /// <summary>Изменяет exact node fact и proof до новой structural публикации.</summary>
        internal void Change(string id, Func<NodeFact, NodeFact> update, bool rebuild = true)
        {
            var before = World.CurrentNodes[id];
            var after = update(before);
            World.CurrentNodes[id] = after;
            Authority.ObserveNodeChange(before, after);
            Availability.UpdateFact(after);
            if (rebuild) Graph = Topology.Build(World.Nodes, World.Sectors, ++_sequence);
        }

        /// <summary>Unrelated leaf move меняет graph identity, но не global infrastructure proofs.</summary>
        internal void Move(int index, int x) => Change(Leaf(index), before => before with
        {
            Revision = before.Revision + 1,
            State = before.State with { Position = new(x, 1, 1) }
        });

        /// <summary>Online-only fact не перестраивает structural GraphSnapshot.</summary>
        internal void Online(string id, bool online) => Change(id, before => before with
        {
            Revision = before.Revision + 1,
            State = before.State with { Online = online }
        }, false);

        /// <summary>Transient generation появляется только в последующем target, без вмешательства в уже pinned snapshot.</summary>
        internal void AddLeaf(string id)
        {
            var node = Node(id, "Z-" + id, 20);
            World.CurrentNodes.Add(id, node);
            Authority.ObserveNodeChange(null, node);
            Availability.UpdateFact(node);
            Graph = Topology.Build(World.Nodes, World.Sectors, ++_sequence);
        }

        /// <summary>Удаление exact generation остаётся видимым как tombstone relative ACKed baseline.</summary>
        internal void Remove(string id)
        {
            var before = World.CurrentNodes[id];
            World.CurrentNodes.Remove(id);
            Authority.ObserveNodeChange(before, null);
            Availability.Remove(new(id, before.Generation));
            Graph = Topology.Build(World.Nodes, World.Sectors, ++_sequence);
        }

        /// <inheritdoc />
        public void Dispose() => Broker.Dispose();
    }

    /// <summary>Независимая native read-model форма проверяет operation ordering и absence ghost rows после coalescing.</summary>
    private sealed class Client
    {
        /// <summary>Последние applied exact node rows.</summary>
        internal Dictionary<NodeKey, GraphSynchronizationNodeUpsert> Nodes { get; } = [];
        /// <summary>Opaque edge ID lookup не требует полного обхода adjacency.</summary>
        internal Dictionary<string, GraphSynchronizationEdgeUpsert> Edges { get; } = new(StringComparer.Ordinal);

        /// <summary>Моделирует только bounded application страницы; network solve здесь отсутствует.</summary>
        internal void Apply(GraphSynchronizationPage page)
        {
            Assert.InRange(page.Operations.Count, 0, 32);
            foreach (var operation in page.Operations)
                switch (operation)
                {
                    case GraphSynchronizationEdgeRemove edge:
                        Assert.True(Edges.Remove(edge.EdgeId));
                        break;
                    case GraphSynchronizationNodeRemove node:
                        Assert.DoesNotContain(Edges.Values, edge => edge.Edge.From == node.Node || edge.Edge.To == node.Node);
                        Assert.True(Nodes.Remove(node.Node));
                        break;
                    case GraphSynchronizationNodeUpsert node: Nodes[node.Node.Key] = node; break;
                    case GraphSynchronizationEdgeUpsert edge:
                        Assert.Contains(edge.Edge.From, Nodes.Keys);
                        Assert.Contains(edge.Edge.To, Nodes.Keys);
                        Edges[edge.EdgeId] = edge;
                        break;
                }
        }
    }

    /// <summary>Canonical test producer остаётся тем же между explicit start/continue/cancel.</summary>
    private static RequestKey Key(ulong sequence = 1, string producer = "graph") => new(producer, sequence);
    /// <summary>Однозначные ID и addresses позволяют проверять address ordering без culture.</summary>
    private static string Leaf(int index) => "leaf-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>APC имеет меньший address, затем идут обычные endpoints.</summary>
    private static string Address(int index) => "B-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Full valid topology fact с optional physical forwarding role.</summary>
    private static NodeFact Node(string id, string address, int x, string type = "server", bool relay = false) =>
        new(id, 1, 1, new(address, type, new(x, 1, 1), "s", true, new("home", "home", IsNtnetRelay: relay)));

    /// <summary>Берёт уже готовую page без implicit Advance или ACK.</summary>
    private static GraphSynchronizationPage Take(GraphSynchronizationBroker broker)
    {
        Assert.True(broker.TryDequeueEvent(out var page));
        return Assert.IsType<GraphSynchronizationPage>(page);
    }

    /// <summary>Полностью применяет один frozen batch, сохраняя one-inflight application rendezvous.</summary>
    private static List<GraphSynchronizationPage> Batch(Scene scene, Client client, RequestKey? key = null)
    {
        var pages = new List<GraphSynchronizationPage>();
        for (var guard = 0; guard < 4000; guard++)
        {
            Assert.Equal(1, scene.Broker.Advance(1));
            var page = Take(scene.Broker);
            pages.Add(page);
            client.Apply(page);
            Assert.True(scene.Broker.Continue(key ?? Key(), page.PageIndex).Accepted);
            if (page.BatchComplete) return pages;
        }
        throw new InvalidOperationException("Bounded graph batch did not finish.");
    }

    /// <summary>Duplex credit window выдаёт четыре страницы без ACK и принимает только выданный cumulative prefix.</summary>
    [Fact]
    public void FourPageWindowBoundsProductionAndCumulativeAcknowledgement()
    {
        using var scene = new Scene(6, new(PageOperations: 1, MaxInflightPages: 4));
        var client = new Client();
        Assert.Equal(AdmissionStatus.Accepted, scene.Broker.Start(Key()).Status);
        Assert.Equal(4, scene.Broker.Advance(100));
        Assert.Equal(4, scene.Broker.InflightPageCount);
        Assert.Equal(4, scene.Broker.EventCount);
        Assert.Equal(0, scene.Broker.Advance(100));
        Assert.Equal("page_not_delivered", scene.Broker.Continue(Key(), 4).Code);
        for (var index = 1; index <= 2; index++)
        {
            var page = Take(scene.Broker);
            Assert.Equal((ulong)index, page.PageIndex);
            client.Apply(page);
        }
        Assert.Equal("page_not_delivered", scene.Broker.Continue(Key(), 3).Code);
        Assert.Equal(0, scene.Broker.BaselineRowCount);
        Assert.True(scene.Broker.Continue(Key(), 2).Accepted);
        Assert.Equal(2, scene.Broker.BaselineRowCount);
        Assert.Equal(2, scene.Broker.Advance(100));
        Assert.Equal(4, scene.Broker.InflightPageCount);
        Assert.Equal(4, scene.Broker.EventCount);
        ulong last = 2;
        var complete = false;
        for (var guard = 0; guard < 10 && !complete; guard++)
        {
            while (scene.Broker.TryDequeueEvent(out var page))
            {
                Assert.NotNull(page);
                Assert.Equal(++last, page.PageIndex);
                client.Apply(page);
                complete |= page.BatchComplete;
            }
            Assert.True(scene.Broker.Continue(Key(), last).Accepted);
            Assert.Equal("duplicate", scene.Broker.Continue(Key(), last).Code);
            Assert.InRange(scene.Broker.Advance(100), 0, 4);
        }
        Assert.True(complete);
        Assert.Equal(client.Nodes.Count + client.Edges.Count, scene.Broker.BaselineRowCount);
        Assert.Equal(0, scene.Broker.TargetRowCount);
        Assert.Equal(0, scene.Broker.InflightPageCount);
    }

    /// <summary>Последняя страница batch — barrier; новый target не захватывается после частичного cumulative ACK.</summary>
    [Fact]
    public void FinalPageAcknowledgementSeparatesFrozenBatches()
    {
        using var scene = new Scene(1, new(PageOperations: 1, MaxInflightPages: 4));
        scene.Broker.Start(Key());
        Assert.Equal(3, scene.Broker.Advance(100));
        var first = Take(scene.Broker);
        var second = Take(scene.Broker);
        var last = Take(scene.Broker);
        Assert.True(last.BatchComplete);
        scene.Move(0, 20);
        Assert.True(scene.Broker.Continue(Key(), second.PageIndex).Accepted);
        Assert.Equal(0, scene.Broker.Advance(100));
        Assert.Equal(3, scene.Broker.TargetRowCount);
        Assert.True(scene.Broker.Continue(Key(), last.PageIndex).Accepted);
        Assert.Equal(0, scene.Broker.TargetRowCount);
        Assert.InRange(scene.Broker.Advance(100), 1, 4);
        var delta = Take(scene.Broker);
        Assert.Equal(GraphSynchronizationPhase.Delta, delta.Phase);
        Assert.Equal(last.GraphVersion, delta.FromGraphVersion);
        Assert.True(delta.GraphVersion > first.GraphVersion);
    }

    /// <summary>Отмена отзывает все retained pages и rows; только выданный terminal ACK освобождает stream.</summary>
    [Fact]
    public void CancellationDropsWindowReferencesAndRequiresTerminalHandoff()
    {
        using var scene = new Scene(6, new(PageOperations: 1, MaxInflightPages: 4));
        scene.Broker.Start(Key());
        scene.Broker.Advance(100);
        var obsolete = Take(scene.Broker);
        Assert.True(scene.Broker.Cancel(Key()).Accepted);
        Assert.Equal(0, scene.Broker.TargetRowCount);
        Assert.Equal(0, scene.Broker.BaselineRowCount);
        Assert.Equal(1, scene.Broker.InflightPageCount);
        Assert.Equal(1, scene.Broker.EventCount);
        Assert.Equal("page_unknown", scene.Broker.Continue(Key(), obsolete.PageIndex).Code);
        Assert.Equal("page_not_delivered", scene.Broker.Continue(Key(), 5).Code);
        var terminal = Take(scene.Broker);
        Assert.Equal(5UL, terminal.PageIndex);
        Assert.Equal("cancelled", terminal.Code);
        Assert.True(scene.Broker.Continue(Key(), terminal.PageIndex).Accepted);
        Assert.Equal(0, scene.Broker.ActiveCount);
        Assert.Equal(0, scene.Broker.InflightPageCount);
        Assert.Equal(0, scene.Broker.EventCount);
        scene.Broker.ConfigurePageWindow(4);
        scene.Broker.Start(Key(2));
        Assert.Throws<InvalidOperationException>(() => scene.Broker.ConfigurePageWindow(1));
    }

    /// <summary>Source ledger разделяет endpoint revisions и transit proofs, используя current world даже без graph rebuild.</summary>
    [Fact]
    public void AuthorityTracksCurrentLeafFactsSeparatelyFromInfrastructureAndAvailability()
    {
        using var scene = new Scene();
        var infrastructure = scene.Authority.InfrastructureStamp;
        var availability = scene.Authority.AvailabilityInfrastructureStamp;
        var oldGraph = scene.Graph;
        scene.Online(Leaf(0), false);
        Assert.Same(oldGraph, scene.Graph);
        Assert.Equal(1UL, scene.Graph!.Nodes[new(Leaf(0), 1)].Revision);
        Assert.Equal(2UL, scene.Authority.NodeRevision(new(Leaf(0), 1)));
        Assert.True(scene.Authority.IsEndpoint(new(Leaf(0), 1)));
        Assert.False(scene.Authority.IsEndpoint(new("apc", 1)));
        Assert.Equal(infrastructure, scene.Authority.InfrastructureStamp);
        Assert.Equal(availability, scene.Authority.AvailabilityInfrastructureStamp);
        scene.Move(1, 100);
        Assert.Equal(infrastructure, scene.Authority.InfrastructureStamp);
        scene.Online("apc", false);
        Assert.True(scene.Authority.AvailabilityInfrastructureStamp > availability);
        var prior = scene.Authority.AvailabilityInfrastructureStamp;
        scene.Authority.SetBackbone(false);
        Assert.True(scene.Authority.AvailabilityInfrastructureStamp > prior);
        scene.Change("apc", node => node with { Revision = node.Revision + 1, State = node.State with { Position = new(2, 1, 1) } });
        Assert.True(scene.Authority.InfrastructureStamp > infrastructure);
    }

    /// <summary>Перед handoff/ACK ни cursor, ни output pressure не меняют baseline; выдача и ACK имеют разные idempotent границы.</summary>
    [Fact]
    public void BaselineAdvancesOnlyAfterDeliveredPageApplicationAck()
    {
        using var scene = new Scene(3);
        var client = new Client();
        Assert.Equal(AdmissionStatus.Accepted, scene.Broker.Start(Key()).Status);
        Assert.Equal(0, scene.Broker.Advance(0));
        Assert.Equal(1, scene.Broker.Advance(100));
        Assert.Equal(0, scene.Broker.BaselineRowCount);
        Assert.Equal("page_not_delivered", scene.Broker.Continue(Key(), 1).Code);
        Assert.Equal(0, scene.Broker.Advance(100));
        var page = Take(scene.Broker);
        Assert.False(scene.Broker.TryDequeueEvent(out _));
        Assert.Equal("page_unknown", scene.Broker.Continue(Key(), page.PageIndex + 1).Code);
        client.Apply(page);
        Assert.True(scene.Broker.Continue(Key(), page.PageIndex).Accepted);
        Assert.Equal(7, scene.Broker.BaselineRowCount);
        Assert.Equal(0, scene.Broker.TargetRowCount);
        Assert.Equal("duplicate", scene.Broker.Continue(Key(), page.PageIndex).Code);
        Assert.Equal(0, scene.Broker.Advance(100));
        Assert.Equal(1, scene.Broker.ActiveCount);
    }

    /// <summary>Постоянные unrelated moves не перезапускают initial snapshot; затем передаются только final coalesced leaf изменения.</summary>
    [Fact]
    public void SnapshotFinishesDuringContinuousLeafMovesThenSendsSmallDelta()
    {
        using var scene = new Scene(40);
        var client = new Client();
        scene.Broker.Start(Key());
        var initial = scene.Graph!.Version;
        var pages = new List<GraphSynchronizationPage>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, scene.Broker.Advance(1));
            var page = Take(scene.Broker);
            pages.Add(page);
            scene.Move(39, 100 + i);
            client.Apply(page);
            scene.Broker.Continue(Key(), page.PageIndex);
        }
        Assert.All(pages, page => Assert.Equal(initial, page.GraphVersion));
        Assert.True(pages[^1].BatchComplete);
        Assert.Equal(41, client.Nodes.Count);
        Assert.Equal(40, client.Edges.Count);
        Assert.Equal(0, scene.Broker.TargetRowCount);
        var delta = Assert.Single(Batch(scene, client));
        Assert.Equal(GraphSynchronizationPhase.Delta, delta.Phase);
        Assert.Equal(initial, delta.FromGraphVersion);
        Assert.Equal(scene.Graph!.Version, delta.GraphVersion);
        Assert.Equal(2, delta.Operations.Count);
        Assert.Equal(4UL, client.Nodes[new(Leaf(39), 1)].NodeRevision);
        Assert.Equal(0, scene.Broker.Advance(100));
    }

    /// <summary>Online-only fact выдаёт новый node_revision даже при неизменном GraphSnapshot и structural version.</summary>
    [Fact]
    public void OnlineOnlyChangesProduceCurrentWorldRevisionDelta()
    {
        using var scene = new Scene(1);
        var client = new Client();
        scene.Broker.Start(Key());
        Batch(scene, client);
        var graph = scene.Graph;
        scene.Online(Leaf(0), false);
        var page = Assert.Single(Batch(scene, client));
        Assert.Same(graph, scene.Graph);
        Assert.Equal(page.FromGraphVersion, page.GraphVersion);
        var operation = Assert.IsType<GraphSynchronizationNodeUpsert>(Assert.Single(page.Operations));
        Assert.Equal(2UL, operation.NodeRevision);
        Assert.Equal(1UL, graph!.Nodes[operation.Node.Key].Revision);
        Assert.True(operation.IsEndpoint);
    }

    /// <summary>Sent transient add остаётся frozen до ACK; следующий delta удаляет edge перед node без ghost в client baseline.</summary>
    [Fact]
    public void TransientSentAdditionDeletedBeforeAckIsEventuallyRemoved()
    {
        using var scene = new Scene(0, new(PageOperations: 1));
        var client = new Client();
        scene.Broker.Start(Key());
        Batch(scene, client);
        scene.AddLeaf("temporary");
        scene.Broker.Advance(1);
        var nodePage = Take(scene.Broker);
        Assert.IsType<GraphSynchronizationNodeUpsert>(Assert.Single(nodePage.Operations));
        scene.Remove("temporary");
        client.Apply(nodePage);
        scene.Broker.Continue(Key(), nodePage.PageIndex);
        var edgePage = Assert.Single(Batch(scene, client));
        Assert.IsType<GraphSynchronizationEdgeUpsert>(Assert.Single(edgePage.Operations));
        var removed = Batch(scene, client);
        Assert.IsType<GraphSynchronizationEdgeRemove>(Assert.Single(removed[0].Operations));
        Assert.IsType<GraphSynchronizationNodeRemove>(Assert.Single(removed[1].Operations));
        Assert.Single(client.Nodes);
        Assert.Empty(client.Edges);
    }

    /// <summary>Address rename повторно выдаёт incident edges с прежним stable ID даже если endpoint orientation/cost не меняются.</summary>
    [Fact]
    public void NodeRenameEmitsIncidentEdgeRefreshWithStableIdentity()
    {
        using var scene = new Scene(1);
        var client = new Client();
        scene.Broker.Start(Key());
        Batch(scene, client);
        var id = Assert.Single(client.Edges.Keys);
        scene.Change(Leaf(0), node => node with { Revision = 2, State = node.State with { Address = "Y" } });
        var page = Assert.Single(Batch(scene, client));
        Assert.IsType<GraphSynchronizationNodeUpsert>(page.Operations[0]);
        Assert.Equal(id, Assert.IsType<GraphSynchronizationEdgeUpsert>(page.Operations[1]).EdgeId);
        scene.Change(Leaf(0), node => node with { Revision = 3, State = node.State with { Address = "0" } });
        var flipped = Assert.Single(Batch(scene, client));
        Assert.Equal(id, Assert.IsType<GraphSynchronizationEdgeUpsert>(flipped.Operations[1]).EdgeId);
        Assert.Equal(new NodeKey(Leaf(0), 1), client.Edges[id].Edge.From);
    }

    /// <summary>Capacity failure/Cancel освобождают frozen target, а terminal ACK позволяет новый stream без смены binding.</summary>
    [Fact]
    public void CancelAndTerminalHistoryKeepMemoryAndProducerBarriersBounded()
    {
        using var scene = new Scene(2, new(MaxNodes: 2, HistoryCapacity: 1, MaxProducers: 1));
        scene.Broker.Start(Key());
        scene.Broker.Advance(1);
        var failure = Take(scene.Broker);
        Assert.Equal("graph_sync_capacity", failure.Code);
        Assert.Empty(failure.Operations);
        Assert.Equal(0, scene.Broker.TargetRowCount);
        scene.Broker.Continue(Key(), failure.PageIndex);
        scene.Remove(Leaf(1));
        Assert.Equal(AdmissionStatus.Accepted, scene.Broker.Start(Key(2)).Status);
        scene.Broker.Advance(1);
        Assert.True(scene.Broker.Cancel(Key(2)).Accepted);
        Assert.Equal(0, scene.Broker.TargetRowCount);
        Assert.Equal(0, scene.Broker.BaselineRowCount);
        var cancelled = Take(scene.Broker);
        Assert.Equal("cancelled", cancelled.Code);
        scene.Broker.Continue(Key(2), cancelled.PageIndex);
        Assert.Equal(0, scene.Broker.ActiveCount);
        Assert.Equal("request_expired", scene.Broker.Start(Key()).Code);
        Assert.Equal("producer_capacity", scene.Broker.Start(Key(producer: "other")).Code);
        Assert.Equal(AdmissionStatus.Accepted, scene.Broker.Start(Key(3)).Status);
        Assert.Equal("stream_capacity", scene.Broker.Start(Key(4)).Code);
    }

    /// <summary>Legacy source-tree API сохраняет старую семантику; authority-enabled дерево не пересчитывает пути на каждой странице.</summary>
    [Fact]
    public void RouteTreeFinishesUnderUnrelatedMovesWithCapturedEndpointRevisions()
    {
        using var scene = new Scene(40);
        using var broker = new ProjectionBroker(() => scene.Graph, scene.Trees, "binding", new(PageRows: 8),
            () => scene.Availability.Revision, scene.Authority);
        var source = new NodeKey(Leaf(0), 1);
        broker.Submit(new(Key(), ProjectionKind.Routes, source));
        var pages = new List<ProjectionPage>();
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(1, broker.Advance(1));
            scene.Move(39, 100 + i);
            Assert.True(broker.TryDequeueEvent(out var page));
            pages.Add(page!);
        }
        Assert.True(pages[^1].Complete);
        Assert.Equal("completed", pages[^1].Code);
        Assert.Equal(1UL, scene.Trees.CacheMisses);
        Assert.All(pages, page => Assert.Equal(1UL, page.SourceNodeRevision));
        var rows = pages.SelectMany(page => page.Rows).Cast<RouteRow>().ToDictionary(row => row.Key);
        Assert.Equal(1UL, rows[new(Leaf(39), 1)].NodeRevision);
        Assert.True(scene.Authority.NodeRevision(new(Leaf(39), 1)) > rows[new(Leaf(39), 1)].NodeRevision);
        Assert.Equal(scene.Authority.NodeRevision(new(Leaf(1), 1)), rows[new(Leaf(1), 1)].NodeRevision);
    }

    /// <summary>Source revision или transit availability меняют authority всего дерева, а не маскируются как unrelated leaf update.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceOrTransitChangeRevokesStampedRoutes(bool sourceChange)
    {
        using var scene = new Scene(3);
        using var broker = new ProjectionBroker(() => scene.Graph, scene.Trees, "binding", new(PageRows: 1),
            () => scene.Availability.Revision, scene.Authority);
        broker.Submit(new(Key(), ProjectionKind.Routes, new(Leaf(0), 1)));
        broker.Advance(1);
        if (sourceChange) scene.Move(0, 100);
        else scene.Online("apc", false);
        Assert.True(broker.TryDequeueEvent(out var terminal));
        Assert.Equal("projection_changed", terminal!.Code);
        Assert.Empty(terminal.Rows);
    }

    /// <summary>Online-only source fact proof берётся из world одновременно с solve, хотя structural graph хранит прежнюю revision.</summary>
    [Fact]
    public void RouteProofsSampleWorldRevisionAtSolveRatherThanStructuralFacts()
    {
        using var scene = new Scene(2);
        scene.Online(Leaf(0), false);
        Assert.Equal(1UL, scene.Graph!.Nodes[new(Leaf(0), 1)].Revision);
        using var broker = new ProjectionBroker(() => scene.Graph, scene.Trees, "binding", availabilityRevision: () => scene.Availability.Revision,
            authority: scene.Authority);
        broker.Submit(new(Key(), ProjectionKind.Routes, new(Leaf(0), 1), RouteKind.Discovery));
        Assert.Equal(1, broker.Advance(1));
        Assert.True(broker.TryDequeueEvent(out var page));
        Assert.Equal(2UL, page!.SourceNodeRevision);
        Assert.Equal(2UL, Assert.IsType<RouteRow>(Assert.Single(page.Rows)).NodeRevision);
    }

    /// <summary>Секторы и настоящая relay role меняют infrastructure proof, простая catalog revision этого не делает.</summary>
    [Fact]
    public void SectorGeometryAndRealRelayRoleHaveExplicitGlobalProofChanges()
    {
        using var scene = new Scene();
        var original = scene.World.CurrentSectors["s"];
        var stamp = scene.Authority.InfrastructureStamp;
        scene.Authority.ObserveSectorChange(original, original with { Revision = 2 });
        Assert.Equal(stamp, scene.Authority.InfrastructureStamp);
        scene.Authority.ObserveSectorChange(original, original with { Revision = 2, Levels = new([1, 2]) });
        Assert.True(scene.Authority.InfrastructureStamp > stamp);
        stamp = scene.Authority.InfrastructureStamp;
        scene.Change(Leaf(2), node => node with { Revision = 2, State = node.State with { Network = node.State.Network! with { IsNtnetRelay = true } } });
        Assert.True(scene.Authority.InfrastructureStamp > stamp);
        Assert.False(scene.Authority.IsEndpoint(new(Leaf(2), 1)));
        var availability = scene.Authority.AvailabilityInfrastructureStamp;
        scene.Online(Leaf(2), false);
        Assert.True(scene.Authority.AvailabilityInfrastructureStamp > availability);
    }

    /// <summary>Page cancellation не удерживает старый GraphSnapshot после появления нового current graph.</summary>
    [Fact]
    public void CancelReleasesPinnedTargetBeforeTerminalAck()
    {
        using var scene = new Scene();
        var weak = BeginPinnedTarget(scene);
        Collect();
        Assert.True(Alive(weak));
        scene.Broker.Cancel(Key());
        Collect();
        Assert.False(Alive(weak));
        Assert.Equal(0, scene.Broker.TargetRowCount);
    }

    /// <summary>NoInlining не позволяет JIT test helper сохранить сильную ссылку при GC-проверке.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<GraphSnapshot> BeginPinnedTarget(Scene scene)
    {
        var weak = new WeakReference<GraphSnapshot>(scene.Graph!);
        scene.Broker.Start(Key());
        scene.Broker.Advance(1);
        scene.Move(0, 100);
        return weak;
    }
    /// <summary>Чтение weak target не переносит temporary strong reference через следующий GC.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Alive(WeakReference<GraphSnapshot> weak) => weak.TryGetTarget(out _);
    /// <summary>Стандартный deterministic collection barrier для проверки resource lifetime.</summary>
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
