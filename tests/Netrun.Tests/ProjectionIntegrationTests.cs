using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Discovery;
using Netrun.Core.Projection;
using Netrun.Core.Routing;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Полный projection pipeline: raw snapshot, authoritative revisions, real route tree и framed Host output.</summary>
public sealed class ProjectionIntegrationTests
{
    /// <summary>Точный synthetic binding каждого независимого fixture.</summary>
    private static readonly Epochs Epochs = new("projection-world", "projection-engine", "projection-binding");

    /// <summary>Три реальные physical вершины создают две endpoint линии и один relay member без virtual clique rows.</summary>
    private static WorldChange[] Facts() =>
    [
        new SectorUpsert(new("sector", 1, 1, new([1]))),
        new NodeUpsert(new("apc-node", 1, 1, new("A", "apc", new(1, 1, 1), "sector", true, new("home", "home")))),
        new NodeUpsert(new("source-node", 1, 1, new("B", "server", new(2, 1, 1), "sector", true, new("home", "home")))),
        new NodeUpsert(new("target-node", 1, 1, new("C", "server", new(3, 1, 1), "sector", true, new("home", "home", IsNtnetRelay: true)))),
        new ServiceUpsert(new("source", 1, 1, new("source-node", 1), "origin", "source-tag", true, new([new("read", OperationSafety.ReadOnly)], 64))),
        new ServiceUpsert(new("target", 1, 1, new("target-node", 1), "research.authority", "target-tag", true, new([new("read", OperationSafety.ReadOnly)], 64)))
    ];

    /// <summary>Получает raw digest именно фактического JSON changes и создаёт четыре причинных snapshot frames.</summary>
    private static RuntimeInput[] Snapshot()
    {
        var chunkInput = new RuntimeInput(Epochs, 2, 0, new ApplyWorld([new SnapshotChunk("snapshot", 1, Facts())]));
        var chunkPacket = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(chunkInput)));
        var chunk = Assert.IsType<SnapshotChunk>(Assert.Single(Assert.IsType<ApplyWorld>(chunkPacket.Input.Command).Entries));
        return
        [
            new(Epochs, 1, 0, new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)])),
            chunkPacket.Input,
            new(Epochs, 3, 0, new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)Facts().Length, SnapshotChecksum.Compute([chunk], 3))])),
            new(Epochs, 4, 0, new ApplyWorld([new CatchUpComplete("snapshot", 0)]))
        ];
    }

    /// <summary>Публикует real mirror и derived topology без mock маршрутизации.</summary>
    private static void Publish(NetworkRuntime runtime)
    {
        foreach (var input in Snapshot()) Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
    }

    /// <summary>Новая команда проходит тот же strict wire codec, который использует TCP Host.</summary>
    private static RuntimeResult Apply(NetworkRuntime runtime, RuntimeCommand command, int time = 0) => runtime.Apply(
        Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(new(Epochs, runtime.AppliedSequence + 1, time, command)))).Input);

    /// <summary>Граф не требует source; Routes всегда адресует точное physical поколение.</summary>
    private static ProjectionRequest Request(ulong sequence = 1, ProjectionKind kind = ProjectionKind.Graph) =>
        new(new("ui", sequence), kind, kind == ProjectionKind.Routes ? new("source-node", 1) : null);

    /// <summary>Передаёт один ready event без выполнения скрытого Pump.</summary>
    private static T Take<T>(NetworkRuntime runtime) where T : TransportEvent
    {
        Assert.True(runtime.TryDequeueEvent(out var value));
        return Assert.IsAssignableFrom<T>(value);
    }

    /// <summary>Читает canonical ulong независимо от JSON numeric precision.</summary>
    private static ulong Counter(JsonElement value, string field) => ulong.Parse(value.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);

    /// <summary>Один serialized peer поддерживает causal ACK и exact input sequence.</summary>
    private sealed class Peer(IHostSession session)
    {
        /// <summary>Последний прочитанный output sequence.</summary>
        internal ulong Ack;
        /// <summary>Последний отправленный logical sequence.</summary>
        internal ulong Sequence;
        /// <summary>Проходит production session и обновляет только обработанный peer ACK.</summary>
        internal async Task<byte[]> Send(RuntimeInput input)
        {
            var output = await session.ProcessAsync(RuntimeJson.Encode(input, Ack), CancellationToken.None);
            using var parsed = JsonDocument.Parse(output);
            Ack = Counter(parsed.RootElement, "output_sequence");
            Sequence = input.Sequence;
            return output;
        }
        /// <summary>Кодирует следующую logical projection или control команду.</summary>
        internal Task<byte[]> Command(RuntimeCommand command) => Send(new(Epochs, Sequence + 1, 0, command));
    }

    /// <summary>Graph/Routes/mode/nullable source и cancel имеют полноценный canonical wire round-trip.</summary>
    [Fact]
    public void ProjectionCommandsRoundTripWithExactSourceAndCanonicalCounters()
    {
        RuntimeCommand[] commands =
        [
            new QueryProjection(Request()),
            new QueryProjection(Request() with { Mode = RouteKind.Machine }),
            new QueryProjection(new(new("ui", ulong.MaxValue), ProjectionKind.Routes, new("source", ulong.MaxValue), RouteKind.Machine)),
            new QueryProjection(Request(kind: ProjectionKind.Routes)),
            new CancelProjection(new("ui", ulong.MaxValue))
        ];
        foreach (var command in commands)
        {
            var input = new RuntimeInput(Epochs, ulong.MaxValue, 7, command);
            var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(input, ulong.MaxValue)));
            Assert.Equal(input, packet.Input);
            Assert.Equal(ulong.MaxValue, packet.AckOutputSequence);
        }
    }

    /// <summary>Projection shape, source requirement, enum allowlists, duplicates и unknowns проверяются до admission.</summary>
    [Fact]
    public void ProjectionWireRejectsInvalidFieldsAndMixedCommands()
    {
        var valid = Encoding.UTF8.GetString(RuntimeJson.Encode(new(Epochs, 1, 0, new QueryProjection(Request()))));
        foreach (var invalid in new[]
        {
            valid.Replace("\"projection_kind\":\"graph\"", "\"projection_kind\":\"unknown\"", StringComparison.Ordinal),
            valid.Replace("\"projection_kind\":\"graph\"", "\"projection_kind\":\"routes\"", StringComparison.Ordinal),
            valid.Replace("\"projection_kind\":\"graph\"", "\"projection_kind\":\"graph\",\"projection_kind\":\"graph\"", StringComparison.Ordinal),
            valid.Replace("\"source\":null", "\"source\":{\"node_id\":\"source-node\",\"generation\":\"1\"}", StringComparison.Ordinal),
            valid.Replace(",\"source\":null", "", StringComparison.Ordinal),
            valid.Replace("\"mode\":\"discovery\"", "\"mode\":\"other\"", StringComparison.Ordinal),
            valid.Replace("\"mode\":\"discovery\"", "\"mode\":\"discovery\",\"frequency\":null", StringComparison.Ordinal),
            valid.Replace("\"request_id\":\"ui:1\"", "\"request_id\":\"ui:01\"", StringComparison.Ordinal),
            valid[..^2] + ",{\"kind\":\"backbone_configure\",\"enabled\":true}]}"
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(new(Epochs, 1, 0,
            new QueryProjection(Request() with { Source = new("source-node", 1) }))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkRuntime(Epochs, new(Projection: new(PageRows: 33))));
    }

    /// <summary>Один work unit формирует одну bounded страницу, а все rows содержат real anchor/LAN/edge identities.</summary>
    [Fact]
    public void RuntimeGraphProjectionContainsTypedRowsAndAuthoritativeRevisionHints()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Projection: new(PageRows: 2)));
        Publish(runtime);
        var admission = Apply(runtime, new QueryProjection(Request()));
        Assert.Equal(AdmissionStatus.Accepted, admission.Admission!.Status);
        Assert.Equal(runtime.Graph!.InputSequence, admission.GraphInputSequence);
        Assert.True(admission.AvailabilityVersion > 0);
        var pages = new List<ProjectionPage>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, runtime.Pump(1));
            pages.Add(Take<ProjectionPage>(runtime));
        }
        Assert.Equal(new ulong[] { 1, 2, 3 }, pages.Select(page => page.PageIndex));
        Assert.Equal(new[] { 2, 2, 1 }, pages.Select(page => page.Rows.Count));
        Assert.All(pages, page =>
        {
            Assert.Equal(runtime.Graph.Version, page.GraphVersion);
            Assert.Equal(runtime.Graph.InputSequence, page.GraphInputSequence);
            Assert.Equal(admission.AvailabilityVersion, page.AvailabilityVersion);
            Assert.Null(page.Source);
        });
        var rows = pages.SelectMany(page => page.Rows).ToArray();
        var nodes = rows.Take(3).Cast<GraphNodeRow>().ToDictionary(row => row.Key.NodeId);
        Assert.Equal(new NodeKey("apc-node", 1), nodes["source-node"].Anchor);
        Assert.NotNull(nodes["source-node"].LanId);
        Assert.True(nodes["target-node"].IsRelay);
        Assert.Equal(2, rows.Skip(3).OfType<GraphEdgeRow>().Count());
        Assert.True(pages[^1].Complete);
        Assert.Equal("completed", pages[^1].Code);
        Assert.Equal(0, runtime.ActiveProjectionCount);
    }

    /// <summary>Routes использует реальные source-tree predecessors и совпадает с независимым pair route API.</summary>
    [Fact]
    public void RuntimeRouteProjectionHasZeroCostSourceAndCorrectPredecessors()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Projection: new(PageRows: 2)));
        Publish(runtime);
        Apply(runtime, new QueryProjection(Request(kind: ProjectionKind.Routes)));
        Assert.Equal(2, runtime.Pump(10));
        var pages = new[] { Take<ProjectionPage>(runtime), Take<ProjectionPage>(runtime) };
        var rows = pages.SelectMany(page => page.Rows).Cast<RouteRow>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(pages, page => Assert.Equal(new NodeKey("source-node", 1), page.Source));
        foreach (var row in rows)
        {
            var route = Apply(runtime, new QueryRoute(new("source-node", 1), row.Key, RouteKind.Discovery)).Route!;
            Assert.Equal(route.LatencyDs, row.LatencyDs);
            Assert.Equal(route.Path.Count == 1 ? null : route.Path[^2], row.Previous);
            Assert.False(row.Quantum);
        }
        Assert.True(pages[^1].Complete);
        Assert.Equal(AdmissionStatus.Duplicate, Apply(runtime, new QueryProjection(Request(kind: ProjectionKind.Routes))).Admission!.Status);
        Assert.False(runtime.TryDequeueEvent(out _));
    }

    /// <summary>Availability-only world delta не меняет GraphSnapshot, но отзывает unread route rows по общему overlay revision.</summary>
    [Fact]
    public void AvailabilityChangeInvalidatesUnreadRoutesWithoutTopologyRebuild()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Projection: new(PageRows: 1)));
        Publish(runtime);
        var graph = runtime.Graph;
        var admitted = Apply(runtime, new QueryProjection(Request(kind: ProjectionKind.Routes)));
        runtime.Pump(1);
        var node = runtime.Nodes["target-node"];
        var changed = Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = 2, State = node.State with { Online = false } })]));
        Assert.Same(graph, runtime.Graph);
        Assert.True(changed.AvailabilityVersion > admitted.AvailabilityVersion);
        var terminal = Take<ProjectionPage>(runtime);
        Assert.True(terminal.Complete);
        Assert.Equal("projection_changed", terminal.Code);
        Assert.Empty(terminal.Rows);
        Assert.Equal(0, runtime.ActiveProjectionCount);
    }

    /// <summary>Graph содержит structural rows, поэтому availability-only изменение не отзывает пригодную неизменившуюся таблицу.</summary>
    [Fact]
    public void AvailabilityChangeKeepsStructuralGraphProjectionReadable()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Projection: new(PageRows: 1)));
        Publish(runtime);
        var graph = runtime.Graph;
        var admitted = Apply(runtime, new QueryProjection(Request()));
        runtime.Pump(1);
        var node = runtime.Nodes["target-node"];
        var changed = Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = 2, State = node.State with { Online = false } })]));
        Assert.Same(graph, runtime.Graph);
        Assert.True(changed.AvailabilityVersion > admitted.AvailabilityVersion);
        var first = Take<ProjectionPage>(runtime);
        Assert.Equal("page", first.Code);
        Assert.Single(first.Rows);
        Assert.Equal(admitted.AvailabilityVersion, first.AvailabilityVersion);
        Assert.Equal(4, runtime.Pump(10));
        var pages = Enumerable.Range(0, 4).Select(_ => Take<ProjectionPage>(runtime)).ToArray();
        Assert.True(pages[^1].Complete);
        Assert.Equal("completed", pages[^1].Code);
    }

    /// <summary>Отзыв partial table после нового structural graph никогда не выдаётся как successful mixed-version completion.</summary>
    [Fact]
    public void StructuralReplacementRevokesPartiallyReadProjection()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Projection: new(PageRows: 1)));
        Publish(runtime);
        Apply(runtime, new QueryProjection(Request()));
        runtime.Pump(1);
        var first = Take<ProjectionPage>(runtime);
        Assert.False(first.Complete);
        var node = runtime.Nodes["target-node"];
        Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = 2, State = node.State with { Position = new(20, 1, 1) } })]));
        Assert.True(runtime.Graph!.Version > first.GraphVersion);
        runtime.Pump(1);
        var terminal = Take<ProjectionPage>(runtime);
        Assert.Equal("projection_changed", terminal.Code);
        Assert.True(terminal.PageIndex > first.PageIndex);
        Assert.True(terminal.Complete);
        Assert.Empty(terminal.Rows);
    }

    /// <summary>Dirty graph удерживает даже уже complete unread projection; новый graph возвращает explicit invalidation.</summary>
    [Fact]
    public void PendingGraphParksProjectionUntilFreshPublication()
    {
        using var runtime = new NetworkRuntime(Epochs, new(DeferTopologyBuild: true));
        Publish(runtime);
        var admitted = Apply(runtime, new QueryProjection(Request()));
        Assert.Equal(0UL, admitted.GraphVersion);
        Assert.Equal(0UL, admitted.GraphInputSequence);
        Assert.False(runtime.TryDequeueEvent(out _));
        Assert.Equal(0, runtime.Pump(0));
        Assert.Equal(1, runtime.Pump(1));
        var node = runtime.Nodes["target-node"];
        Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = 2, State = node.State with { Position = new(20, 1, 1) } })]));
        Assert.Null(runtime.Graph);
        Assert.False(runtime.TryDequeueEvent(out _));
        runtime.Pump(0);
        Assert.Equal("projection_changed", Take<ProjectionPage>(runtime).Code);
    }

    /// <summary>Три независимые очереди получают по одному work unit и event slot даже при маленьком общем бюджете.</summary>
    [Fact]
    public void ThreeProducersShareWorkBudgetAndOutputWithoutStarvation()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Transport: new(MaxEvents: 1),
            Discovery: new(MaxEvents: 1), Projection: new(PageRows: 1, MaxEvents: 1)));
        Publish(runtime);
        Apply(runtime, new SubmitRequest(new(new("machine", 1), new("source", 1), new("target", 1), "read", NetworkValue.Null)));
        Apply(runtime, new DiscoverServices(new(new("lookup", 1), new("source", 1), "research.authority")));
        Apply(runtime, new QueryProjection(Request()));
        for (var i = 0; i < 3; i++) Assert.Equal(1, runtime.Pump(1));
        Assert.Equal(3, runtime.EventCount);
        Assert.Equal(0, runtime.Pump(1));
        Assert.IsType<ServiceInvocation>(Take<TransportEvent>(runtime));
        Assert.IsType<DiscoveryPage>(Take<TransportEvent>(runtime));
        Assert.IsType<ProjectionPage>(Take<TransportEvent>(runtime));
        Assert.Equal(0, runtime.SimulationTimeDs);
    }

    /// <summary>Generated terminal удерживает query slot до handoff; would_block не расходует domain producer serial.</summary>
    [Fact]
    public void ProjectionPressureAndCancellationPreserveRetryableIdentity()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Projection: new(MaxQueries: 1, MaxEvents: 1)));
        Publish(runtime);
        Apply(runtime, new QueryProjection(Request()));
        runtime.Pump(1);
        var blocked = Apply(runtime, new QueryProjection(Request(2)));
        Assert.Equal(ApplyStatus.Applied, blocked.Apply.Status);
        Assert.Equal(AdmissionStatus.WouldBlock, blocked.Admission!.Status);
        Assert.True(Apply(runtime, new CancelProjection(Request().Key)).Action!.Accepted);
        var cancelled = Take<ProjectionPage>(runtime);
        Assert.Equal("cancelled", cancelled.Code);
        Assert.Empty(cancelled.Rows);
        Assert.Equal(AdmissionStatus.Accepted, Apply(runtime, new QueryProjection(Request(2))).Admission!.Status);
    }

    /// <summary>Production Host сериализует только typed bounded rows и authoritative versions; output journal повторяет exact projection bytes.</summary>
    [Fact]
    public async Task HostOutputsGraphAndRoutePagesWithStableReplayBytes()
    {
        using var session = new RuntimeHostSessionFactory(new(Projection: new(PageRows: 2))).Create(Epochs);
        var peer = new Peer(session);
        foreach (var input in Snapshot()) await peer.Send(input);
        var priorAck = peer.Ack;
        var graphBytes = await peer.Command(new QueryProjection(Request()));
        using var graph = JsonDocument.Parse(graphBytes);
        var entries = graph.RootElement.GetProperty("results").EnumerateArray().ToArray();
        var result = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "runtime_result");
        Assert.True(Counter(result, "graph_input_sequence") > 0);
        Assert.True(Counter(result, "availability_version") > 0);
        var pages = entries.Where(entry => entry.GetProperty("kind").GetString() == "projection_page").ToArray();
        Assert.Equal(3, pages.Length);
        Assert.All(pages, page =>
        {
            Assert.Equal("graph", page.GetProperty("projection_kind").GetString());
            Assert.Equal(JsonValueKind.Null, page.GetProperty("source").ValueKind);
            Assert.InRange(page.GetProperty("rows").GetArrayLength(), 1, 32);
            Assert.Equal(Counter(result, "graph_version"), Counter(page, "graph_version"));
            Assert.Equal(Counter(result, "availability_version"), Counter(page, "availability_version"));
        });
        var rows = pages.SelectMany(page => page.GetProperty("rows").EnumerateArray()).ToArray();
        Assert.Equal(new[] { "node", "node", "node", "edge", "edge" }, rows.Select(row => row.GetProperty("kind").GetString()));
        Assert.Equal("apc-node", rows[1].GetProperty("anchor").GetProperty("node_id").GetString());
        Assert.True(rows[2].GetProperty("is_relay").GetBoolean());
        Assert.Equal("endpoint", rows[3].GetProperty("link_kind").GetString());
        Assert.Equal("completed", pages[^1].GetProperty("code").GetString());
        Assert.Equal(graphBytes, await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, priorAck), CancellationToken.None));
        using var routes = JsonDocument.Parse(await peer.Command(new QueryProjection(Request(2, ProjectionKind.Routes))));
        var routeRows = routes.RootElement.GetProperty("results").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "projection_page")
            .SelectMany(page => page.GetProperty("rows").EnumerateArray()).ToArray();
        Assert.Equal(3, routeRows.Length);
        var source = Assert.Single(routeRows, row => row.GetProperty("node").GetProperty("node_id").GetString() == "source-node");
        Assert.Equal(JsonValueKind.Null, source.GetProperty("previous").ValueKind);
        Assert.Equal(0, source.GetProperty("latency_ds").GetDouble());
        Assert.False(source.GetProperty("quantum").GetBoolean());
    }

    /// <summary>Все semantic projection fields, включая kind/source/mode/cancel, входят в общий causal fingerprint.</summary>
    [Theory]
    [InlineData("key")]
    [InlineData("kind")]
    [InlineData("source")]
    [InlineData("generation")]
    [InlineData("mode")]
    [InlineData("cancel")]
    public void ProjectionFingerprintIncludesCompleteTypedCommand(string changed)
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime);
        var request = Request(kind: ProjectionKind.Routes);
        var input = new RuntimeInput(Epochs, runtime.AppliedSequence + 1, 0, new QueryProjection(request));
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
        var different = changed switch
        {
            "key" => request with { Key = new("ui", 2) },
            "kind" => request with { Kind = ProjectionKind.Graph, Source = null },
            "source" => request with { Source = new("other", 1) },
            "generation" => request with { Source = new("source-node", 2) },
            "mode" => request with { Mode = RouteKind.Machine },
            _ => request
        };
        RuntimeCommand command = changed == "cancel" ? new CancelProjection(request.Key) : new QueryProjection(different);
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(input with { Command = command }).Apply.Status);
        Assert.Equal(0, runtime.ActiveProjectionCount);
        Assert.False(runtime.TryDequeueEvent(out _));
    }
}
