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

/// <summary>Проверяет graph-sync через строгий codec, accepted world hooks, четыре runtime lanes и production Host session.</summary>
public sealed class GraphSynchronizationIntegrationTests
{
    /// <summary>Один изолированный synthetic binding каждого теста.</summary>
    private static readonly Epochs Epochs = new("sync-world", "sync-engine", "sync-binding");
    /// <summary>Persistent stream имеет один canonical domain ID до explicit cancel.</summary>
    private static readonly RequestKey Stream = new("graph", 1);

    /// <summary>Ordinal endpoint ID без связи с runtime addresses.</summary>
    private static string Leaf(int index) => "leaf-" + index.ToString("D3", CultureInfo.InvariantCulture);
    /// <summary>Обычный endpoint всегда имеет один APC cable и не становится internal forwarder.</summary>
    private static NodeFact Endpoint(int index) => new(Leaf(index), 1, 1,
        new("B-" + index.ToString("D3", CultureInfo.InvariantCulture), "server", new(index + 2, 1, 1), "s", true, new("home", "home")));

    /// <summary>Actual spatial builder получает один APC и отдельно объявленные source/target service contracts.</summary>
    private static WorldChange[] Facts(int leaves = 3)
    {
        var facts = new List<WorldChange>
        {
            new SectorUpsert(new("s", 1, 1, new([1]))),
            new NodeUpsert(new("apc", 1, 1, new("A", "apc", new(1, 1, 1), "s", true, new("home", "home"))))
        };
        facts.AddRange(Enumerable.Range(0, leaves).Select(index => new NodeUpsert(Endpoint(index))));
        facts.Add(new ServiceUpsert(new("source", 1, 1, new(Leaf(0), 1), "origin", "source-tag", true, new([new("read", OperationSafety.ReadOnly)], 64))));
        facts.Add(new ServiceUpsert(new("target", 1, 1, new(Leaf(1), 1), "research.authority", "target-tag", true, new([new("read", OperationSafety.ReadOnly)], 64))));
        return facts.ToArray();
    }

    /// <summary>Checksum v3 построен из decoder-derived raw changes digest реального wire chunk.</summary>
    private static RuntimeInput[] Snapshot(int leaves = 3)
    {
        var facts = Facts(leaves);
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(new(Epochs, 2, 0,
            new ApplyWorld([new SnapshotChunk("snapshot", 1, facts)])))));
        var chunk = Assert.IsType<SnapshotChunk>(Assert.Single(Assert.IsType<ApplyWorld>(packet.Input.Command).Entries));
        return
        [
            new(Epochs, 1, 0, new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)])), packet.Input,
            new(Epochs, 3, 0, new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)facts.Length, SnapshotChecksum.Compute([chunk], 3))])),
            new(Epochs, 4, 0, new ApplyWorld([new CatchUpComplete("snapshot", 0)]))
        ];
    }

    /// <summary>Открывает real runtime readiness и выполняет initial authority reset ровно после публикации mirror.</summary>
    private static void Publish(NetworkRuntime runtime, int leaves = 3)
    {
        foreach (var input in Snapshot(leaves)) Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
    }

    /// <summary>Ввод typed команды обязательно проходит production JSON parser.</summary>
    private static RuntimeResult Apply(NetworkRuntime runtime, RuntimeCommand command) => runtime.Apply(
        Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(new(Epochs, runtime.AppliedSequence + 1, 0, command)))).Input);

    /// <summary>Один ready event без implicit Pump или application ACK.</summary>
    private static T Take<T>(NetworkRuntime runtime) where T : TransportEvent
    {
        Assert.True(runtime.TryDequeueEvent(out var value));
        return Assert.IsAssignableFrom<T>(value);
    }

    /// <summary>String counters читаются без потери UInt64 precision.</summary>
    private static ulong Counter(JsonElement value, string name) => ulong.Parse(value.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    /// <summary>Выбор по kind не зависит от соседних runtime diagnostics.</summary>
    private static JsonElement Entry(JsonDocument document, string kind) => Assert.Single(document.RootElement.GetProperty("results").EnumerateArray(),
        value => value.GetProperty("kind").GetString() == kind);

    /// <summary>Session peer отличает output ACK от explicit graph application ACK.</summary>
    private sealed class Peer(IHostSession session)
    {
        /// <summary>Последняя logical sequence.</summary>
        internal ulong Sequence;
        /// <summary>Последний обработанный output frame; чтение page само по себе не вызывает Continue.</summary>
        internal ulong Ack;

        /// <summary>Отправляет snapshot/control и подтверждает только уже прочитанные output bytes.</summary>
        internal async Task<byte[]> Send(RuntimeInput input)
        {
            var bytes = await session.ProcessAsync(RuntimeJson.Encode(input, Ack), CancellationToken.None);
            using var document = JsonDocument.Parse(bytes);
            Ack = Counter(document.RootElement, "output_sequence");
            Sequence = input.Sequence;
            return bytes;
        }
        /// <summary>Выполняет одну команду в общей causal последовательности.</summary>
        internal Task<byte[]> Command(RuntimeCommand command) => Send(new(Epochs, Sequence + 1, 0, command));
    }

    /// <summary>Все три graph controls имеют exact typed round-trip и positive string page counters.</summary>
    [Fact]
    public void ThreeGraphSynchronizationCommandsRoundTripWithStrictCanonicalSchema()
    {
        RuntimeCommand[] commands =
        [
            new StartGraphSynchronization(new("graph-producer", ulong.MaxValue)),
            new ContinueGraphSynchronization(new("graph-producer", ulong.MaxValue), ulong.MaxValue),
            new CancelGraphSynchronization(new("graph-producer", ulong.MaxValue))
        ];
        foreach (var command in commands)
        {
            var input = new RuntimeInput(Epochs, 42, 7, command);
            var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(input, ulong.MaxValue)));
            Assert.Equal(input, packet.Input);
            Assert.Equal(ulong.MaxValue, packet.AckOutputSequence);
        }
        var valid = Encoding.UTF8.GetString(RuntimeJson.Encode(new(Epochs, 1, 0, new ContinueGraphSynchronization(Stream, 1))));
        foreach (var invalid in new[]
        {
            valid.Replace("\"page_index\":\"1\"", "\"page_index\":1", StringComparison.Ordinal),
            valid.Replace("\"page_index\":\"1\"", "\"page_index\":\"0\"", StringComparison.Ordinal),
            valid.Replace("\"page_index\":\"1\"", "\"page_index\":\"01\"", StringComparison.Ordinal),
            valid.Replace("\"page_index\":\"1\"", "\"page_index\":\"1\",\"page_index\":\"1\"", StringComparison.Ordinal),
            valid.Replace(",\"page_index\":\"1\"", "", StringComparison.Ordinal),
            valid.Replace("\"request_id\":\"graph:1\"", "\"request_id\":\"graph:01\"", StringComparison.Ordinal),
            valid.Replace("\"page_index\":\"1\"", "\"page_index\":\"1\",\"unknown\":true", StringComparison.Ordinal),
            valid[..^2] + ",{\"kind\":\"graph_sync_cancel\",\"request_id\":\"graph:1\"}]}"
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        foreach (var command in new RuntimeCommand[] { new StartGraphSynchronization(Stream), new CancelGraphSynchronization(Stream) })
        {
            var json = Encoding.UTF8.GetString(RuntimeJson.Encode(new(Epochs, 1, 0, command)));
            Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace("\"request_id\":\"graph:1\"",
                "\"request_id\":\"graph:1\",\"page_index\":\"1\"", StringComparison.Ordinal))));
        }
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(new(Epochs, 1, 0, new ContinueGraphSynchronization(Stream, 0))));
    }

    /// <summary>Runtime observe hooks не путают обычные endpoint changes, service changes и реальные forwarder/backbone updates.</summary>
    [Fact]
    public void RuntimeAuthorityChangesOnlyForTouchedRelevantFacts()
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime);
        var initial = Apply(runtime, new ConfigureBackbone(true));
        Assert.True(initial.InfrastructureStamp > 0);
        Assert.True(initial.AvailabilityInfrastructureStamp > 0);
        var leaf = runtime.Nodes[Leaf(2)];
        var moved = Apply(runtime, new ApplyWorld([new NodeUpsert(leaf with { Revision = 2, State = leaf.State with { Position = new(100, 1, 1) } })]));
        Assert.Equal(initial.InfrastructureStamp, moved.InfrastructureStamp);
        Assert.Equal(initial.AvailabilityInfrastructureStamp, moved.AvailabilityInfrastructureStamp);
        var graph = runtime.Graph;
        var current = runtime.Nodes[Leaf(2)];
        var offline = Apply(runtime, new ApplyWorld([new NodeUpsert(current with { Revision = 3, State = current.State with { Online = false } })]));
        Assert.Same(graph, runtime.Graph);
        Assert.Equal(initial.InfrastructureStamp, offline.InfrastructureStamp);
        Assert.Equal(initial.AvailabilityInfrastructureStamp, offline.AvailabilityInfrastructureStamp);
        var service = runtime.Services["target"];
        var serviceOnly = Apply(runtime, new ApplyWorld([new ServiceUpsert(service with { Revision = 2, Tag = "other" })]));
        Assert.Equal(initial.InfrastructureStamp, serviceOnly.InfrastructureStamp);
        Assert.Equal(initial.AvailabilityInfrastructureStamp, serviceOnly.AvailabilityInfrastructureStamp);
        var stale = Apply(runtime, new ApplyWorld([new NodeUpsert(leaf)]));
        Assert.Equal(serviceOnly.InfrastructureStamp, stale.InfrastructureStamp);
        var apc = runtime.Nodes["apc"];
        var transit = Apply(runtime, new ApplyWorld([new NodeUpsert(apc with { Revision = 2, State = apc.State with { Online = false } })]));
        Assert.True(transit.AvailabilityInfrastructureStamp > initial.AvailabilityInfrastructureStamp);
        Assert.Equal(initial.InfrastructureStamp, transit.InfrastructureStamp);
        var backbone = Apply(runtime, new ConfigureBackbone(false));
        Assert.True(backbone.AvailabilityInfrastructureStamp > transit.AvailabilityInfrastructureStamp);
        var sector = runtime.Sectors["s"];
        var geometry = Apply(runtime, new ApplyWorld([new SectorUpsert(sector with { Revision = 2, Levels = new([1, 2]) })]));
        Assert.True(geometry.InfrastructureStamp > initial.InfrastructureStamp);
    }

    /// <summary>Общий budget=1 и заполненные event slots дают по одному продвижению каждой из четырёх независимых lanes.</summary>
    [Fact]
    public void FourRuntimeProducersShareBudgetAndHandoffFairly()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Transport: new(MaxEvents: 1), Discovery: new(MaxEvents: 1),
            Projection: new(PageRows: 1, MaxEvents: 1), GraphSynchronization: new(PageOperations: 1)));
        Publish(runtime);
        Apply(runtime, new SubmitRequest(new(new("machine", 1), new("source", 1), new("target", 1), "read", NetworkValue.Null)));
        Apply(runtime, new DiscoverServices(new(new("lookup", 1), new("source", 1), "research.authority")));
        Apply(runtime, new QueryProjection(new(new("routes", 1), ProjectionKind.Routes, new(Leaf(0), 1))));
        Apply(runtime, new StartGraphSynchronization(Stream));
        for (var i = 0; i < 4; i++) Assert.Equal(1, runtime.Pump(1));
        Assert.Equal(4, runtime.EventCount);
        Assert.Equal(0, runtime.Pump(1));
        var native = Take<ServiceInvocation>(runtime);
        Assert.True(native.SourceNodeRevision > 0);
        Assert.True(native.DestinationNodeRevision > 0);
        Assert.True(native.InfrastructureStamp > 0);
        Take<DiscoveryPage>(runtime);
        Take<ProjectionPage>(runtime);
        var graph = Take<GraphSynchronizationPage>(runtime);
        Assert.True(Apply(runtime, new ContinueGraphSynchronization(Stream, graph.PageIndex)).Action!.Accepted);
        Assert.InRange(runtime.Pump(1), 0, 1);
        Assert.InRange(runtime.Pump(1), 0, 1);
        Assert.Equal(0, runtime.SimulationTimeDs);
    }

    /// <summary>Production route pages сохраняют captured proof для движущегося target, не срывая unaffected source tree.</summary>
    [Fact]
    public void RuntimeStampedSourceTreeFinishesThroughUnrelatedLeafChurn()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Projection: new(PageRows: 1, MaxEvents: 1)));
        Publish(runtime);
        var admitted = Apply(runtime, new QueryProjection(new(new("routes", 1), ProjectionKind.Routes, new(Leaf(0), 1))));
        var capturedVersion = admitted.GraphVersion;
        var pages = new List<ProjectionPage>();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1, runtime.Pump(1));
            var node = runtime.Nodes[Leaf(2)];
            var moved = Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = node.Revision + 1,
                State = node.State with { Position = new(100 + i, 1, 1) } })]));
            Assert.Equal(admitted.InfrastructureStamp, moved.InfrastructureStamp);
            pages.Add(Take<ProjectionPage>(runtime));
        }
        Assert.All(pages, page =>
        {
            Assert.Equal(capturedVersion, page.GraphVersion);
            Assert.Equal(admitted.InfrastructureStamp, page.InfrastructureStamp);
            Assert.Equal(admitted.AvailabilityInfrastructureStamp, page.AvailabilityInfrastructureStamp);
            Assert.Equal(1UL, page.SourceNodeRevision);
        });
        Assert.True(pages[^1].Complete);
        Assert.Equal("completed", pages[^1].Code);
        var rows = pages.SelectMany(page => page.Rows).Cast<RouteRow>().ToDictionary(row => row.Key);
        Assert.Equal(runtime.Nodes[Leaf(1)].Revision, rows[new(Leaf(1), 1)].NodeRevision);
        Assert.True(runtime.Nodes[Leaf(2)].Revision > rows[new(Leaf(2), 1)].NodeRevision);
        Assert.Equal(0, runtime.ActiveProjectionCount);
    }

    /// <summary>Real Host различает frame ACK и page commit ACK, завершает snapshot при churn, затем передаёт small/online-only deltas.</summary>
    [Fact]
    public async Task HostGraphStreamUsesApplicationAcksAndNeverRestartsSnapshotForLeafMoves()
    {
        using var session = new RuntimeHostSessionFactory(new(PumpWorkBudget: 1, GraphSynchronization: new(PageOperations: 1))).Create(Epochs);
        var peer = new Peer(session);
        foreach (var input in Snapshot()) await peer.Send(input);
        using var started = JsonDocument.Parse(await peer.Command(new StartGraphSynchronization(Stream)));
        Assert.Equal("accepted", Entry(started, "runtime_result").GetProperty("admission").GetProperty("status").GetString());
        var page = Entry(started, "graph_sync_page").Clone();
        var initialVersion = Counter(page, "graph_version");
        var infrastructure = Counter(page, "infra_stamp");
        var operations = new List<JsonElement>();
        var moving = Endpoint(2);
        var count = 0;
        while (true)
        {
            count++;
            Assert.InRange(count, 1, 7);
            Assert.Equal("snapshot", page.GetProperty("phase").GetString());
            Assert.Equal(initialVersion, Counter(page, "graph_version"));
            Assert.Equal(infrastructure, Counter(page, "infra_stamp"));
            operations.Add(Assert.Single(page.GetProperty("operations").EnumerateArray()).Clone());
            if (page.GetProperty("batch_complete").GetBoolean()) break;
            moving = moving with { Revision = moving.Revision + 1, State = moving.State with { Position = new(100 + count, 1, 1) } };
            using var changed = JsonDocument.Parse(await peer.Command(new ApplyWorld([new NodeUpsert(moving)])));
            Assert.DoesNotContain(changed.RootElement.GetProperty("results").EnumerateArray(), value => value.GetProperty("kind").GetString() == "graph_sync_page");
            Assert.Equal(infrastructure, Counter(Entry(changed, "runtime_result"), "infra_stamp"));
            using var continued = JsonDocument.Parse(await peer.Command(new ContinueGraphSynchronization(Stream, Counter(page, "page_index"))));
            Assert.True(Entry(continued, "runtime_result").GetProperty("action").GetProperty("accepted").GetBoolean());
            page = Entry(continued, "graph_sync_page").Clone();
        }
        Assert.Equal(7, count);
        Assert.Equal(new[] { "node_upsert", "node_upsert", "node_upsert", "node_upsert", "edge_upsert", "edge_upsert", "edge_upsert" },
            operations.Select(operation => operation.GetProperty("kind").GetString()));
        var ordinary = Assert.Single(operations, operation => operation.GetProperty("kind").GetString() == "node_upsert" &&
            operation.GetProperty("node").GetProperty("node_id").GetString() == Leaf(2));
        Assert.True(ordinary.GetProperty("is_endpoint").GetBoolean());
        Assert.Equal("1", ordinary.GetProperty("node_revision").GetString());
        using var deltaOutput = JsonDocument.Parse(await peer.Command(new ContinueGraphSynchronization(Stream, Counter(page, "page_index"))));
        var delta = Entry(deltaOutput, "graph_sync_page");
        Assert.Equal("delta", delta.GetProperty("phase").GetString());
        Assert.True(Counter(delta, "graph_version") > initialVersion);
        Assert.Equal(initialVersion, Counter(delta, "from_graph_version"));
        var nodeDelta = Assert.Single(delta.GetProperty("operations").EnumerateArray());
        Assert.Equal("node_upsert", nodeDelta.GetProperty("kind").GetString());
        Assert.Equal(moving.Revision, Counter(nodeDelta, "node_revision"));
        using var edgeOutput = JsonDocument.Parse(await peer.Command(new ContinueGraphSynchronization(Stream, Counter(delta, "page_index"))));
        var edgePage = Entry(edgeOutput, "graph_sync_page");
        Assert.True(edgePage.GetProperty("batch_complete").GetBoolean());
        var edge = Assert.Single(edgePage.GetProperty("operations").EnumerateArray());
        Assert.Equal("edge_upsert", edge.GetProperty("kind").GetString());
        Assert.Matches("^[0-9a-f]{64}$", edge.GetProperty("edge_id").GetString());
        var currentVersion = Counter(edgePage, "graph_version");
        using var idle = JsonDocument.Parse(await peer.Command(new ContinueGraphSynchronization(Stream, Counter(edgePage, "page_index"))));
        Assert.Single(idle.RootElement.GetProperty("results").EnumerateArray());
        moving = moving with { Revision = moving.Revision + 1, State = moving.State with { Online = false } };
        using var online = JsonDocument.Parse(await peer.Command(new ApplyWorld([new NodeUpsert(moving)])));
        var onlinePage = Entry(online, "graph_sync_page");
        Assert.Equal(currentVersion, Counter(onlinePage, "graph_version"));
        Assert.Equal(currentVersion, Counter(onlinePage, "from_graph_version"));
        Assert.True(onlinePage.GetProperty("batch_complete").GetBoolean());
        Assert.Equal(moving.Revision, Counter(Assert.Single(onlinePage.GetProperty("operations").EnumerateArray()), "node_revision"));
        using var acknowledged = JsonDocument.Parse(await peer.Command(new ContinueGraphSynchronization(Stream, Counter(onlinePage, "page_index"))));
        using var cancelled = JsonDocument.Parse(await peer.Command(new CancelGraphSynchronization(Stream)));
        var cancellation = Entry(cancelled, "graph_sync_page");
        Assert.Equal("cancelled", cancellation.GetProperty("code").GetString());
        Assert.Empty(cancellation.GetProperty("operations").EnumerateArray());
        using var cancelAck = JsonDocument.Parse(await peer.Command(new ContinueGraphSynchronization(Stream, Counter(cancellation, "page_index"))));
        using var restarted = JsonDocument.Parse(await peer.Command(new StartGraphSynchronization(new("graph", 2))));
        Assert.Equal("accepted", Entry(restarted, "runtime_result").GetProperty("admission").GetProperty("status").GetString());
        Assert.Equal("snapshot", Entry(restarted, "graph_sync_page").GetProperty("phase").GetString());
    }

    /// <summary>Все graph control виды, key и exact ACK serial принадлежат общему causal fingerprint.</summary>
    [Theory]
    [InlineData("key")]
    [InlineData("continue")]
    [InlineData("cancel")]
    public void GraphControlsCannotChangeInsideAlreadyAcceptedLogicalFrame(string field)
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime);
        var input = new RuntimeInput(Epochs, 5, 0, new StartGraphSynchronization(Stream));
        runtime.Apply(input);
        RuntimeCommand different = field switch
        {
            "key" => new StartGraphSynchronization(new("graph", 2)),
            "continue" => new ContinueGraphSynchronization(Stream, 1),
            _ => new CancelGraphSynchronization(Stream)
        };
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(input with { Command = different }).Apply.Status);
        runtime.Pump(10);
        Assert.False(runtime.TryDequeueEvent(out _));
    }
}
