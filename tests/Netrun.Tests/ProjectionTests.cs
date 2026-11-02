using System.Runtime.CompilerServices;
using Netrun.Core;
using Netrun.Core.Projection;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет bounded одноразовые UI проекции, отзыв partial tables и единственное source-tree вычисление.</summary>
public sealed class ProjectionTests
{
    /// <summary>Создаёт immutable topology fact без native ссылок.</summary>
    private static NodeFact Node(string id, bool online = true, bool relay = false) =>
        new(id, 1, 1, new(id, "server", new(1, 1, 1), "s", online, new(null, null, IsNtnetRelay: relay)));

    /// <summary>Точное поколение fixture узла.</summary>
    private static NodeKey Key(string id) => new(id, 1);

    /// <summary>Новый serial одного fixture producer.</summary>
    private static RequestKey Request(ulong sequence, string producer = "ui") => new(producer, sequence);

    /// <summary>Канонический маленький граф с полными structural rows, но без материализованных quantum рёбер.</summary>
    private static GraphSnapshot Graph(NodeFact[] facts, params (string From, string To, int Cost)[] links)
    {
        var nodes = facts.ToDictionary(node => Key(node.NodeId));
        var addresses = nodes.ToDictionary(pair => pair.Value.State.Address, pair => pair.Key, StringComparer.Ordinal);
        var edges = links.Select(link => new PhysicalEdge(addresses[link.From], addresses[link.To], PhysicalLinkKind.Apc, 1, link.Cost)).ToArray();
        var adjacency = nodes.Keys.ToDictionary(key => key, _ => new List<PhysicalEdge>());
        foreach (var edge in edges) { adjacency[edge.From].Add(edge); adjacency[edge.To].Add(edge); }
        foreach (var (key, incident) in adjacency)
            incident.Sort((a, b) => StringComparer.Ordinal.Compare(
                nodes[a.From == key ? a.To : a.From].State.Address,
                nodes[b.From == key ? b.To : b.From].State.Address));
        var relays = nodes.Keys.Where(key => nodes[key].State.Network!.IsNtnetRelay)
            .OrderBy(key => nodes[key].State.Address, StringComparer.Ordinal).ToArray();
        var bindings = new Dictionary<NodeKey, NodeKey>();
        if (nodes.ContainsKey(Key("A")) && nodes.ContainsKey(Key("R1"))) bindings.Add(Key("A"), Key("R1"));
        var lans = nodes.Keys.ToDictionary(key => key, key => "test/" + key.NodeId);
        return new GraphSnapshot(1, 7, nodes, addresses, edges, adjacency, relays, bindings, lans, 3);
    }

    /// <summary>Вынимает уже подготовленные immutable страницы без скрытого продвижения работы.</summary>
    private static List<ProjectionPage> Drain(ProjectionBroker broker)
    {
        var pages = new List<ProjectionPage>();
        while (broker.TryDequeueEvent(out var page)) pages.Add(page!);
        return pages;
    }

    /// <summary>Graph выдаёт все address-sorted nodes перед физическими edges и не разворачивает relay clique.</summary>
    [Fact]
    public void GraphPagesContainNodesThenPhysicalEdgesWithStableMetadata()
    {
        var graph = Graph([Node("R2", relay: true), Node("B"), Node("R1", relay: true), Node("A")],
            ("A", "R1", 1), ("R2", "B", 1));
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(PageRows: 2));
        var request = new ProjectionRequest(Request(1), ProjectionKind.Graph);
        Assert.Equal(AdmissionStatus.Accepted, broker.Submit(request).Status);
        Assert.Equal(3, broker.Advance(10));
        Assert.Equal(1, broker.ActiveCount);
        Assert.Equal(0, broker.RetainedNodeCount);
        var pages = Drain(broker);
        Assert.Equal(3, pages.Count);
        Assert.Equal(new ulong[] { 1, 2, 3 }, pages.Select(page => page.PageIndex));
        Assert.All(pages, page =>
        {
            Assert.Equal("binding", page.BindingEpoch);
            Assert.Equal(ProjectionKind.Graph, page.Kind);
            Assert.Equal(graph.Version, page.GraphVersion);
            Assert.Equal(graph.InputSequence, page.GraphInputSequence);
            Assert.Equal(0UL, page.AvailabilityVersion);
            Assert.Null(page.Source);
            Assert.Equal(2, page.Rows.Count);
        });
        Assert.All(pages.Take(2), page => Assert.False(page.Complete));
        Assert.True(pages[^1].Complete);
        Assert.Equal("completed", pages[^1].Code);
        var rows = pages.SelectMany(page => page.Rows).ToArray();
        Assert.Equal(new[] { "A", "B", "R1", "R2" }, rows.Take(4).Cast<GraphNodeRow>().Select(row => row.Address));
        Assert.Equal(graph.PhysicalEdges, rows.Skip(4).Cast<GraphEdgeRow>().Select(row => row.Edge));
        Assert.Equal(Key("R1"), ((GraphNodeRow)rows[0]).Anchor);
        Assert.Equal("test/A", ((GraphNodeRow)rows[0]).LanId);
        Assert.False(((GraphNodeRow)rows[0]).IsRelay);
        Assert.True(((GraphNodeRow)rows[2]).IsRelay);
        Assert.Equal(0, broker.ActiveCount);
        Assert.Equal(1, broker.HistoryCount);
        Assert.Equal(AdmissionStatus.Duplicate, broker.Submit(request).Status);
        Assert.Equal(0, broker.Advance(10));
    }

    /// <summary>Routes перечисляет только reached entries одного source-tree, сохраняя predecessor, веса и quantum флаг.</summary>
    [Fact]
    public void RoutePagesUseOneSourceTreeAndMatchPairRouter()
    {
        var graph = Graph([Node("A"), Node("R1", relay: true), Node("R2", relay: true), Node("B"), Node("X")],
            ("A", "R1", 1), ("R2", "B", 1));
        var trees = new SourceRouteTreeProvider(options: new(CacheCapacity: 0));
        using var broker = new ProjectionBroker(() => graph, trees, "binding", new(PageRows: 1), () => 0);
        broker.Submit(new(Request(1), ProjectionKind.Routes, Key("A")));
        Assert.Equal(4, broker.Advance(20));
        var pages = Drain(broker);
        Assert.Equal(1UL, trees.CacheMisses);
        Assert.Equal(0UL, trees.CacheHits);
        Assert.Equal(0, broker.RetainedNodeCount);
        var rows = pages.SelectMany(page => page.Rows).Cast<RouteRow>().ToDictionary(row => row.Key);
        Assert.Equal(4, rows.Count);
        Assert.False(rows.ContainsKey(Key("X")));
        Assert.Null(rows[Key("A")].Previous);
        Assert.False(rows[Key("R1")].Quantum);
        Assert.True(rows[Key("B")].Quantum);
        var router = new NetworkRouter();
        foreach (var row in rows.Values)
        {
            var route = router.FindRoute(graph, Key("A"), row.Key, RouteKind.Discovery);
            Assert.Equal(route.LatencyDs, row.LatencyDs);
            Assert.Equal(route.Path.Count > 1 ? route.Path[^2] : null, row.Previous);
        }
        Assert.All(pages, page => Assert.Equal(Key("A"), page.Source));
        Assert.True(pages[^1].Complete);
    }

    /// <summary>Offline discovery источник имеет self row; Machine и stale источник завершаются явным пустым отказом.</summary>
    [Fact]
    public void RouteSourcePoliciesProduceTypedTerminalPages()
    {
        var graph = Graph([Node("A", online: false), Node("B")], ("A", "B", 1));
        using var broker = new ProjectionBroker(() => graph, new(), "binding");
        broker.Submit(new(Request(1), ProjectionKind.Routes, Key("A")));
        broker.Advance(1);
        var self = Assert.Single(Drain(broker));
        Assert.Equal(new RouteRow(Key("A"), null, 0, false), Assert.Single(self.Rows));
        foreach (var request in new[] { new ProjectionRequest(Request(2), ProjectionKind.Routes, Key("A"), RouteKind.Machine),
                     new ProjectionRequest(Request(3), ProjectionKind.Routes, Key("missing")) })
        {
            broker.Submit(request);
            broker.Advance(1);
            var rejected = Assert.Single(Drain(broker));
            Assert.True(rejected.Complete);
            Assert.Equal("source_unavailable", rejected.Code);
            Assert.Empty(rejected.Rows);
            Assert.Equal(0, broker.RetainedNodeCount);
        }
    }

    /// <summary>Смена graph object даже с прежними номерами отзывает unread completed страницу до handoff.</summary>
    [Fact]
    public void ReplacementSnapshotCannotLeakStaleCompletion()
    {
        GraphSnapshot? graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        using var broker = new ProjectionBroker(() => graph, new(), "binding");
        var request = new ProjectionRequest(Request(1), ProjectionKind.Graph);
        broker.Submit(request);
        broker.Advance(1);
        Assert.Equal(1, broker.EventCount);
        graph = Graph([Node("A"), Node("B")], ("A", "B", 9));
        var changed = Assert.Single(Drain(broker));
        Assert.True(changed.Complete);
        Assert.Equal("projection_changed", changed.Code);
        Assert.Empty(changed.Rows);
        Assert.Equal(0, broker.ActiveCount);
        Assert.Equal("projection_changed", broker.Submit(request).Code);
    }

    /// <summary>Уже выданный partial page не делает последующие старые строки допустимыми после смены публикации.</summary>
    [Fact]
    public void MidQueryGraphChangeRetractsUnreadPagesAndSignalsDiscard()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")]);
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(PageRows: 1));
        broker.Submit(new(Request(1), ProjectionKind.Graph));
        broker.Advance(1);
        Assert.False(Assert.Single(Drain(broker)).Complete);
        broker.Advance(1);
        graph = Graph([Node("A"), Node("B"), Node("D")]);
        Assert.Equal(1, broker.Advance(1));
        var terminal = Assert.Single(Drain(broker));
        Assert.Equal("projection_changed", terminal.Code);
        Assert.Empty(terminal.Rows);
        Assert.Equal(0, broker.RetainedNodeCount);
        Assert.Equal(0, broker.Advance(20));
    }

    /// <summary>Availability-only churn отзывает Routes, но не прерывает structural Graph, где online flags отсутствуют.</summary>
    [Theory]
    [InlineData(ProjectionKind.Graph)]
    [InlineData(ProjectionKind.Routes)]
    public void AvailabilityChangeRevokesRoutesButPreservesStructuralGraphPages(ProjectionKind kind)
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        using var broker = new ProjectionBroker(() => graph, new(availability), "binding", new(PageRows: 1), () => availability.Revision);
        broker.Submit(new(Request(1), kind, kind == ProjectionKind.Routes ? Key("A") : null));
        broker.Advance(1);
        availability.UpdateFact(Node("B", online: false));
        var first = Assert.Single(Drain(broker));
        Assert.True(first.AvailabilityVersion < availability.Revision);
        if (kind == ProjectionKind.Routes)
        {
            Assert.Equal("projection_changed", first.Code);
            Assert.Empty(first.Rows);
            Assert.True(first.Complete);
        }
        else
        {
            Assert.Equal("page", first.Code);
            Assert.Single(first.Rows);
            Assert.False(first.Complete);
            broker.Advance(10);
            var remaining = Drain(broker);
            Assert.Equal("completed", remaining[^1].Code);
            Assert.True(remaining[^1].Complete);
            Assert.Equal(3, first.Rows.Count + remaining.Sum(page => page.Rows.Count));
        }
    }

    /// <summary>При отсутствии accessor Routes всё равно проверяет revision через shared tree cache.</summary>
    [Fact]
    public void RoutesWithoutAccessorDetectAvailabilityRevisionThroughTrees()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        var availability = new NetworkAvailability(graph.Nodes.Values);
        using var broker = new ProjectionBroker(() => graph, new(availability), "binding", new(PageRows: 1));
        broker.Submit(new(Request(1), ProjectionKind.Routes, Key("A")));
        broker.Advance(1);
        availability.SetBackbone(false);
        Assert.Equal("projection_changed", Assert.Single(Drain(broker)).Code);
    }

    /// <summary>Null graph паркует и начало, и handoff успешных страниц; отмена остаётся доступной без графа.</summary>
    [Fact]
    public void PendingTopologyParksWorkAndPagesButNotCancellation()
    {
        GraphSnapshot? graph = null;
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(PageRows: 1));
        var request = new ProjectionRequest(Request(1), ProjectionKind.Graph);
        Assert.Equal(AdmissionStatus.Accepted, broker.Submit(request).Status);
        Assert.Equal(0, broker.Advance(10));
        Assert.False(broker.TryDequeueEvent(out _));
        graph = Graph([Node("A"), Node("B")]);
        Assert.Equal(1, broker.Advance(1));
        graph = null;
        Assert.False(broker.TryDequeueEvent(out _));
        Assert.Equal(1, broker.EventCount);
        Assert.True(broker.Cancel(request.Key).Accepted);
        var cancelled = Assert.Single(Drain(broker));
        Assert.Equal("cancelled", cancelled.Code);
        Assert.Empty(cancelled.Rows);
        Assert.Equal(0, broker.ActiveCount);
    }

    /// <summary>Отмена удаляет только unread страницы своего query; клиент получает один terminal после partial handoff.</summary>
    [Fact]
    public void PartialCancellationRetractsBufferedRowsAndPreservesDedup()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")]);
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(PageRows: 1));
        var request = new ProjectionRequest(Request(1), ProjectionKind.Graph);
        broker.Submit(request);
        broker.Advance(1);
        Assert.False(Assert.Single(Drain(broker)).Complete);
        broker.Advance(1);
        Assert.True(broker.Cancel(request.Key).Accepted);
        Assert.Equal(0, broker.RetainedNodeCount);
        var terminal = Assert.Single(Drain(broker));
        Assert.Equal("cancelled", terminal.Code);
        Assert.True(terminal.Complete);
        Assert.Empty(terminal.Rows);
        Assert.Equal(AdmissionStatus.Duplicate, broker.Submit(request).Status);
        Assert.Equal(RequestState.Cancelled, broker.Submit(request).State);
        Assert.False(broker.Cancel(request.Key).Accepted);
    }

    /// <summary>Output pressure не расходует новые slices, а cancel без собственного queued slot остаётся атомарным would_block.</summary>
    [Fact]
    public void OutputPressureBoundsWorkAndDoesNotLoseCancellation()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C")]);
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(MaxQueries: 2, PageRows: 1, MaxEvents: 1));
        broker.Submit(new(Request(1), ProjectionKind.Graph));
        broker.Submit(new(Request(2), ProjectionKind.Graph));
        Assert.Equal(1, broker.Advance(100));
        Assert.Equal(0, broker.Advance(100));
        Assert.True(broker.Cancel(Request(2)).WouldBlock);
        Assert.Equal(2, broker.ActiveCount);
        Assert.Equal(AdmissionStatus.WouldBlock, broker.Submit(new(Request(3), ProjectionKind.Graph)).Status);
        Assert.Equal(Request(1), Assert.Single(Drain(broker)).Key);
        Assert.True(broker.Cancel(Request(2)).Accepted);
        Assert.Equal("cancelled", Assert.Single(Drain(broker)).Code);
        Assert.Equal(AdmissionStatus.Accepted, broker.Submit(new(Request(3), ProjectionKind.Graph)).Status);
    }

    /// <summary>Даже готовая terminal страница удерживает query slot до handoff; непринятый serial можно повторить после освобождения.</summary>
    [Fact]
    public void QuerySlotRemainsReservedUntilTerminalHandoff()
    {
        var graph = Graph([Node("A")]);
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(MaxQueries: 1));
        broker.Submit(new(Request(1), ProjectionKind.Graph));
        broker.Advance(1);
        Assert.Equal(1, broker.ActiveCount);
        Assert.Equal(AdmissionStatus.WouldBlock, broker.Submit(new(Request(2), ProjectionKind.Graph)).Status);
        Assert.True(Assert.Single(Drain(broker)).Complete);
        Assert.Equal(AdmissionStatus.Accepted, broker.Submit(new(Request(2), ProjectionKind.Graph)).Status);
    }

    /// <summary>История вытесняется, но producer watermark запрещает повтор забытого ключа и changed body известного ключа.</summary>
    [Fact]
    public void HistoryWatermarksAndProducerCapacityPreventReexecution()
    {
        var graph = Graph([Node("A")]);
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(HistoryCapacity: 1, MaxProducers: 1));
        var first = new ProjectionRequest(Request(1), ProjectionKind.Graph);
        var second = new ProjectionRequest(Request(2), ProjectionKind.Graph);
        foreach (var request in new[] { first, second })
        {
            Assert.Equal(AdmissionStatus.Accepted, broker.Submit(request).Status);
            broker.Advance(1);
            Assert.Single(Drain(broker));
        }
        Assert.Equal(1, broker.HistoryCount);
        Assert.Equal(1, broker.ProducerCount);
        Assert.Equal("request_expired", broker.Submit(first).Code);
        Assert.Equal(AdmissionStatus.Duplicate, broker.Submit(second).Status);
        Assert.Equal("request_conflict", broker.Submit(second with { Kind = ProjectionKind.Routes, Source = Key("A") }).Code);
        Assert.Equal("producer_capacity", broker.Submit(new(Request(1, "other"), ProjectionKind.Graph)).Code);
        Assert.Equal(0, broker.Advance(10));
    }

    /// <summary>Cursor keys и route entries имеют общий явный bound, а завершение и Dispose освобождают workspace.</summary>
    [Fact]
    public void WorkspaceAndOutputStayBoundedAndDisposeReleasesThem()
    {
        var graph = Graph(Enumerable.Range(0, 100).Select(i => Node(i.ToString("D3"))).ToArray(),
            Enumerable.Range(0, 99).Select(i => (i.ToString("D3"), (i + 1).ToString("D3"), 1)).ToArray());
        var broker = new ProjectionBroker(() => graph, new(), "binding", new(MaxQueries: 2, PageRows: 1, MaxEvents: 2), () => 0);
        broker.Submit(new(Request(1), ProjectionKind.Graph));
        broker.Submit(new(Request(2), ProjectionKind.Routes, Key("000")));
        Assert.Equal(2, broker.Advance(100));
        Assert.Equal(300, broker.RetainedNodeCount);
        Assert.Equal(2, broker.EventCount);
        Assert.Equal(2, broker.ActiveCount);
        broker.Dispose();
        broker.Dispose();
        Assert.Equal(0, broker.RetainedNodeCount);
        Assert.Equal(0, broker.EventCount);
        Assert.Equal(0, broker.ActiveCount);
        Assert.Equal(0, broker.ProducerCount);
        Assert.Throws<ObjectDisposedException>(() => broker.Advance(1));
    }

    /// <summary>Даже активный cursor и unread page не удерживают структурный snapshot после отзыва владельцем.</summary>
    [Fact]
    public void ActiveCursorDoesNotRetainGraphSnapshot()
    {
        var holder = new GraphHolder();
        using var broker = new ProjectionBroker(() => holder.Graph, new(), "binding", new(PageRows: 1));
        var weak = StartUnretainedGraph(broker, holder);
        for (var attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
        Assert.Equal(3, broker.RetainedNodeCount);
        Assert.Equal(1, broker.EventCount);
        Assert.False(broker.TryDequeueEvent(out _));
        GC.KeepAlive(broker);
    }

    /// <summary>Ограниченный callback holder не содержит graph после завершения helper stack frame.</summary>
    private sealed class GraphHolder
    {
        /// <summary>Единственная сильная ссылка владельца на публикацию; null моделирует dirty topology.</summary>
        internal GraphSnapshot? Graph;
    }

    /// <summary>Отдельный stack frame исключает удержание локального graph JIT-компилятором во время GC проверки.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartUnretainedGraph(ProjectionBroker broker, GraphHolder holder)
    {
        holder.Graph = Graph([Node("A"), Node("B"), Node("C")]);
        var weak = new WeakReference(holder.Graph);
        broker.Submit(new(Request(1), ProjectionKind.Graph));
        broker.Advance(1);
        holder.Graph = null;
        return weak;
    }

    /// <summary>Страница копирует входной список и прекращает перечисление сразу после предельной строки.</summary>
    [Fact]
    public void PageRowsAreDefensiveBoundedImmutableValues()
    {
        var rows = new List<ProjectionRow> { new GraphNodeRow(Key("A"), "A", null, null, false) };
        var page = new ProjectionPage("binding", Request(1), ProjectionKind.Graph, 1, 7, 0, null, 1, rows, true, "completed");
        rows.Clear();
        Assert.Single(page.Rows);
        Assert.False((object)page.Rows is IList<ProjectionRow>);
        Assert.Throws<ArgumentException>(() => new ProjectionPage("binding", Request(2), ProjectionKind.Graph, 1, 7, 0,
            null, 1, Enumerable.Repeat<ProjectionRow>(new GraphNodeRow(Key("A"), "A", null, null, false), 65), true, "completed"));
    }

    /// <summary>Неверные semantic тела не потребляют serial, а memory caps дают пустой terminal без частичной таблицы.</summary>
    [Fact]
    public void RequestValidationAndGraphCapacityFailExplicitly()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        using var broker = new ProjectionBroker(() => graph, new(), "binding", new(MaxNodes: 1));
        foreach (var request in new[] { new ProjectionRequest(Request(1), ProjectionKind.Routes),
                     new ProjectionRequest(Request(1), ProjectionKind.Graph, Key("A")),
                     new ProjectionRequest(Request(1), (ProjectionKind)100),
                     new ProjectionRequest(Request(1), ProjectionKind.Graph, Mode: (RouteKind)100),
                     new ProjectionRequest(Request(1), ProjectionKind.Routes, new NodeKey("A", 0)) })
            Assert.Equal("invalid_request", broker.Submit(request).Code);
        Assert.Equal(0, broker.ProducerCount);
        Assert.Equal(AdmissionStatus.Accepted, broker.Submit(new(Request(1), ProjectionKind.Graph)).Status);
        broker.Advance(1);
        var failed = Assert.Single(Drain(broker));
        Assert.Equal("projection_capacity", failed.Code);
        Assert.Empty(failed.Rows);
        Assert.Equal(0, broker.RetainedNodeCount);
        using var edgeLimited = new ProjectionBroker(() => graph, new(), "binding", new(MaxEdges: 0));
        edgeLimited.Submit(new(Request(1), ProjectionKind.Graph));
        edgeLimited.Advance(1);
        Assert.Equal("projection_capacity", Assert.Single(Drain(edgeLimited)).Code);
    }

    /// <summary>Неправильно связанный accessor и provider завершаются безопасно и не делают memory counter отрицательным.</summary>
    [Fact]
    public void RevisionMismatchDuringCaptureDoesNotCorruptMemoryAccounting()
    {
        var graph = Graph([Node("A"), Node("B")], ("A", "B", 1));
        using var broker = new ProjectionBroker(() => graph, new(), "binding", availabilityRevision: () => 1);
        broker.Submit(new(Request(1), ProjectionKind.Routes, Key("A")));
        broker.Advance(1);
        Assert.Equal("projection_changed", Assert.Single(Drain(broker)).Code);
        Assert.Equal(0, broker.RetainedNodeCount);
    }

    /// <summary>Проверяет границы конфигурации до любых allocations мирового масштаба.</summary>
    [Fact]
    public void InvalidOptionsAndNegativeBudgetAreRejected()
    {
        var graph = Graph([Node("A")]);
        foreach (var options in new[] { new ProjectionOptions(MaxQueries: 0), new ProjectionOptions(MaxQueries: 17),
                     new ProjectionOptions(PageRows: 65), new ProjectionOptions(MaxEvents: 0), new ProjectionOptions(HistoryCapacity: 0),
                     new ProjectionOptions(MaxProducers: 129), new ProjectionOptions(MaxNodes: 10_001), new ProjectionOptions(MaxEdges: -1) })
            Assert.Throws<ArgumentException>(() => new ProjectionBroker(() => graph, new(), "binding", options));
        using var broker = new ProjectionBroker(() => graph, new(), "binding");
        Assert.Throws<ArgumentOutOfRangeException>(() => broker.Advance(-1));
        Assert.Equal(0, broker.Advance(0));
    }
}
