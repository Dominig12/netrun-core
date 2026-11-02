using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет indexed point-to-APC выбор и его немедленный causal wire reply без собственного console node.</summary>
public sealed class AnchorQueryTests
{
    /// <summary>Каждый fixture имеет независимый synthetic binding.</summary>
    private static readonly Epochs Epochs = new("anchor-world", "anchor-engine", "anchor-binding");

    /// <summary>APC facts отделяют native supplied area от physical placement и online overlay.</summary>
    private static NodeFact Apc(string id, string address, int x, int y, int z = 1, string sector = "sector",
        string? area = null, string? supplied = null, bool online = true) => new(id, 1, 1,
        new(address, "apc", new(x, y, z), sector, online, new(area, supplied)));

    /// <summary>Builder receives actual complete facts; query не должен заимствовать этот mutable словарь.</summary>
    private static Dictionary<string, NodeFact> World(params NodeFact[] nodes) => nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);

    /// <summary>Native ordered floor stack передаётся без числовой пересортировки.</summary>
    private static Dictionary<string, SectorFact> Sectors(params int[] levels) => new(StringComparer.Ordinal)
    {
        ["sector"] = new("sector", 1, 1, new(levels.Length == 0 ? [1] : levels))
    };

    /// <summary>Полная point command identity; область может быть явно неизвестна.</summary>
    private static AnchorRequest Request(ulong sequence = 1, Position? position = null, string sector = "sector", string? area = null) =>
        new(new("point", sequence), position ?? new(1, 1, 1), sector, area);

    /// <summary>Snapshot v3 использует decoder-derived hash фактически закодированного changes.</summary>
    private static RuntimeInput[] Snapshot(params NodeFact[] nodes)
    {
        WorldChange[] facts = [new SectorUpsert(Sectors()["sector"]), .. nodes.Select(node => new NodeUpsert(node))];
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

    /// <summary>Initial mirror publication и topology build выполняются production coordinator.</summary>
    private static void Publish(NetworkRuntime runtime, params NodeFact[] nodes)
    {
        foreach (var input in Snapshot(nodes)) Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
    }

    /// <summary>Logical input всегда проходит строгий codec; semantic reply может быть отрицательным без fatal frame error.</summary>
    private static RuntimeResult Apply(NetworkRuntime runtime, RuntimeCommand command) => runtime.Apply(
        Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(new(Epochs, runtime.AppliedSequence + 1, 0, command)))).Input);

    /// <summary>Chebyshev X/Y distance отличается от Manhattan, а native floor index — от числовой разницы Z.</summary>
    [Fact]
    public void IndexedPointUsesChebyshevDistanceAndNativeOrderedFloors()
    {
        var builder = new NetworkTopology();
        var graph = builder.Build(World(Apc("diagonal", "Z", 12, 12), Apc("axis", "A", 13, 10)), Sectors(), 1);
        Assert.Equal(new NodeKey("diagonal", 1), builder.ResolveAnchor(graph, new(10, 10, 1), "sector"));
        graph = builder.Build(World(Apc("numeric-far", "A", 10, 10, 100), Apc("numeric-near", "B", 13, 10, 50)), Sectors(100, 50, 1), 2);
        Assert.Equal(new NodeKey("numeric-far", 1), builder.ResolveAnchor(graph, new(10, 10, 1), "sector"));
    }

    /// <summary>Native supplied-area bucket побеждает distance и offline status, но неизвестная area возвращается к общему nearest.</summary>
    [Fact]
    public void NativeAreaPreferenceIncludesOfflineApcsAndFallsBackWhenAbsent()
    {
        var builder = new NetworkTopology();
        var graph = builder.Build(World(
            Apc("native", "B", 100, 100, area: "mounted", supplied: "home", online: false),
            Apc("near", "A", 2, 1, area: "home", supplied: "other")), Sectors(), 1);
        Assert.Equal(new NodeKey("native", 1), builder.ResolveAnchor(graph, new(1, 1, 1), "sector", "home"));
        Assert.Equal(new NodeKey("near", 1), builder.ResolveAnchor(graph, new(1, 1, 1), "sector", "missing"));
        Assert.Equal(new NodeKey("near", 1), builder.ResolveAnchor(graph, new(1, 1, 1), "sector"));
    }

    /// <summary>Точное равенство distance разрешается ordinal address, без выбора по NodeId или insertion order.</summary>
    [Fact]
    public void AnchorTieUsesAddressAndQueriesDoNotBorrowInputRegistry()
    {
        var nodes = World(Apc("low-id", "Z", 10, 10), Apc("high-id", "A", 12, 10));
        var sectors = Sectors();
        var builder = new NetworkTopology();
        var graph = builder.Build(nodes, sectors, 17);
        nodes.Clear();
        sectors.Clear();
        for (var i = 0; i < 100; i++)
            Assert.Equal(new NodeKey("high-id", 1), builder.ResolveAnchor(graph, new(11, 10, 1), "sector"));
        Assert.Same(graph, builder.Current);
        Assert.Equal(17UL, graph.InputSequence);
    }

    /// <summary>Spatial lookup никогда не пересекает независимый sector; known empty sector отличается от unknown/Z mismatch.</summary>
    [Fact]
    public void SectorBoundaryAndNoApcHaveDistinctSafeResults()
    {
        var sectors = Sectors();
        sectors["other"] = new("other", 1, 1, new([2]));
        var builder = new NetworkTopology();
        var graph = builder.Build(World(Apc("far", "A", 100, 100), Apc("other", "B", 1, 1, 2, "other")), sectors, 1);
        Assert.Equal(new NodeKey("far", 1), builder.ResolveAnchor(graph, new(1, 1, 1), "sector"));
        Assert.Equal("sector_unknown", Assert.Throws<TopologyException>(() => builder.ResolveAnchor(graph, new(1, 1, 1), "unknown")).Code);
        Assert.Equal("sector_position", Assert.Throws<TopologyException>(() => builder.ResolveAnchor(graph, new(1, 1, 2), "sector")).Code);
        graph = builder.Build(World(), Sectors(), 2);
        Assert.Null(builder.ResolveAnchor(graph, new(1, 1, 1), "sector"));
    }

    /// <summary>Новый successful Build заменяет indices атомарно; failed Build не публикует частичный APC набор.</summary>
    [Fact]
    public void OnlySuccessfulPublicationReplacesRetainedSpatialIndex()
    {
        var builder = new NetworkTopology(new(MaxPhysicalEdges: 1));
        var first = Apc("first", "A", 1, 1);
        var second = Apc("second", "B", 100, 1);
        var original = builder.Build(World(first, second), Sectors(), 1);
        Assert.Throws<TopologyException>(() => builder.Build(World(first, second, Apc("unpublished", "C", 50, 1)), Sectors(), 2));
        Assert.Same(original, builder.Current);
        Assert.Equal(new NodeKey("first", 1), builder.ResolveAnchor(original, new(50, 1, 1), "sector"));
        var replacement = builder.Build(World(first with { Generation = 2 }), Sectors(), 3);
        Assert.Equal(new NodeKey("first", 2), builder.ResolveAnchor(replacement, new(50, 1, 1), "sector"));
        Assert.Equal("anchor_graph_stale", Assert.Throws<TopologyException>(() => builder.ResolveAnchor(original, new(50, 1, 1), "sector")).Code);
    }

    /// <summary>Strict wire bounds сохраняют nullable area/canonical key, отвергая unknown/duplicate coordinate fields и invalid Unicode.</summary>
    [Fact]
    public void AnchorCodecRoundTripsAndRejectsMalformedPointMetadata()
    {
        var input = new RuntimeInput(Epochs, ulong.MaxValue, 7,
            new QueryAnchor(Request(ulong.MaxValue, new(32767, 32767, 4096), area: new string('t', 128))));
        Assert.Equal(input, Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(input, ulong.MaxValue))).Input);
        var valid = Encoding.UTF8.GetString(RuntimeJson.Encode(new(Epochs, 1, 0, new QueryAnchor(Request()))));
        foreach (var invalid in new[]
        {
            valid.Replace("\"x\":1", "\"x\":0", StringComparison.Ordinal),
            valid.Replace("\"x\":1", "\"x\":32768", StringComparison.Ordinal),
            valid.Replace("\"y\":1", "\"y\":\"1\"", StringComparison.Ordinal),
            valid.Replace("\"z\":1", "\"z\":4097", StringComparison.Ordinal),
            valid.Replace("\"x\":1", "\"x\":1,\"x\":1", StringComparison.Ordinal),
            valid.Replace("\"x\":1", "\"x\":1,\"unknown\":1", StringComparison.Ordinal),
            valid.Replace("\"base_area_id\":null", "\"base_area_id\":\"\\uD800\"", StringComparison.Ordinal),
            valid.Replace(",\"base_area_id\":null", "", StringComparison.Ordinal),
            valid.Replace("\"request_id\":\"point:1\"", "\"request_id\":\"point:01\"", StringComparison.Ordinal),
            valid.Replace("\"sector_id\":\"sector\"", "\"sector_id\":null", StringComparison.Ordinal)
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(new(Epochs, 1, 0, new QueryAnchor(Request() with { Position = new(0, 1, 1) }))));
    }

    /// <summary>Immediate runtime reply не создаёт event/query slots и сохраняет точные APC поколения после replacement.</summary>
    [Fact]
    public void RuntimePointReplyIsImmediateAndGenerationAware()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var first = Apc("apc", "A", 1, 1);
        Publish(runtime, first);
        var graph = runtime.Graph;
        var result = Apply(runtime, new QueryAnchor(Request()));
        Assert.Equal(new NodeKey("apc", 1), result.Anchor!.Node);
        Assert.Equal("found", result.Anchor.Code);
        Assert.Equal(graph!.Version, result.Anchor.GraphVersion);
        Assert.Equal(graph.InputSequence, result.Anchor.GraphInputSequence);
        Assert.Equal(0, runtime.EventCount);
        Assert.Equal(0, runtime.ActiveProjectionCount);
        Assert.Equal(0, runtime.ActiveDiscoveryCount);
        Apply(runtime, new ApplyWorld([new NodeUpsert(first with { Generation = 2 })]));
        Assert.Equal(new NodeKey("apc", 2), Apply(runtime, new QueryAnchor(Request(2))).Anchor!.Node);
        Assert.Equal(0, runtime.SimulationTimeDs);
    }

    /// <summary>Dirty/failed Build не отдаёт прежний anchor даже при сохранённом diagnostic Current внутри topology owner.</summary>
    [Fact]
    public void PendingAndFailedTopologyNeverReturnStaleAnchor()
    {
        using var runtime = new NetworkRuntime(Epochs, new(DeferTopologyBuild: true));
        var apc = Apc("apc", "A", 1, 1);
        Publish(runtime, apc);
        var pending = Apply(runtime, new QueryAnchor(Request())).Anchor!;
        Assert.Equal("topology_pending", pending.Code);
        Assert.Null(pending.Node);
        Assert.Equal(0UL, pending.GraphVersion);
        runtime.Pump(0);
        Assert.Equal("found", Apply(runtime, new QueryAnchor(Request(2))).Anchor!.Code);
        Apply(runtime, new ApplyWorld([new SectorRemove("sector", 1, 2)]));
        runtime.Pump(0);
        Assert.Null(runtime.Graph);
        Assert.Equal("topology_missing_sector", runtime.TopologyError);
        var failed = Apply(runtime, new QueryAnchor(Request(3)));
        Assert.Equal("topology_pending", failed.Anchor!.Code);
        Assert.Null(failed.Anchor.Node);
        Assert.Null(failed.ErrorCode);
        Assert.Equal(0UL, failed.Anchor.GraphInputSequence);
        Apply(runtime, new ApplyWorld([new SectorUpsert(new("sector", 2, 1, new([1])))]));
        runtime.Pump(0);
        Assert.Equal(new NodeKey("apc", 1), Apply(runtime, new QueryAnchor(Request(4))).Anchor!.Node);
    }

    /// <summary>Negative point result остаётся semantic reply принятого frame и не закрывает usable graph.</summary>
    [Fact]
    public void NoApcUnknownSectorAndWrongFloorAreNonFatalTypedReplies()
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime);
        Assert.Equal("not_found", Apply(runtime, new QueryAnchor(Request())).Anchor!.Code);
        Assert.Equal("sector_unknown", Apply(runtime, new QueryAnchor(Request(2, sector: "unknown"))).Anchor!.Code);
        var floor = Apply(runtime, new QueryAnchor(Request(3, new(1, 1, 2))));
        Assert.Equal(ApplyStatus.Applied, floor.Apply.Status);
        Assert.Equal("sector_position", floor.Anchor!.Code);
        Assert.Null(floor.Anchor.Node);
        Assert.Null(floor.ErrorCode);
        Assert.NotNull(runtime.Graph);
    }

    /// <summary>Повтор старого frame возвращает прежнее наблюдение с прежней graph boundary, не выполняя spatial query на новой publication.</summary>
    [Fact]
    public void LogicalReplayPreservesOriginalAnchorResultAfterGraphChanges()
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime, Apc("far", "A", 100, 1));
        var input = new RuntimeInput(Epochs, 5, 0, new QueryAnchor(Request()));
        var original = runtime.Apply(input).Anchor!;
        Apply(runtime, new ApplyWorld([new NodeUpsert(Apc("near", "B", 1, 1))]));
        var current = Apply(runtime, new QueryAnchor(Request(2))).Anchor!;
        Assert.Equal(new NodeKey("near", 1), current.Node);
        var replay = runtime.Apply(input);
        Assert.Equal(ApplyStatus.Duplicate, replay.Apply.Status);
        Assert.Equal(original, replay.Anchor);
        Assert.True(replay.GraphVersion > original.GraphVersion);
        Assert.Equal(new NodeKey("far", 1), replay.Anchor!.Node);
    }

    /// <summary>Native Host получает exact nested anchor_result и canonical counters, без synthetic node registration.</summary>
    [Fact]
    public async Task HostSerializesImmediateAnchorResultAndSemanticFailures()
    {
        using var session = new RuntimeHostSessionFactory().Create(Epochs);
        ulong ack = 0;
        foreach (var input in Snapshot(Apc("apc", "A", 1, 1)))
        {
            using var output = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.Encode(input, ack), CancellationToken.None));
            ack = ulong.Parse(output.RootElement.GetProperty("output_sequence").GetString()!, CultureInfo.InvariantCulture);
        }
        using var response = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.Encode(new(Epochs, 5, 0, new QueryAnchor(Request())), ack), CancellationToken.None));
        var result = Assert.Single(response.RootElement.GetProperty("results").EnumerateArray());
        var anchor = result.GetProperty("anchor_result");
        Assert.Equal("point:1", anchor.GetProperty("request_id").GetString());
        Assert.Equal("apc", anchor.GetProperty("node").GetProperty("node_id").GetString());
        Assert.Equal("1", anchor.GetProperty("node").GetProperty("generation").GetString());
        Assert.Equal(result.GetProperty("graph_version").GetString(), anchor.GetProperty("graph_version").GetString());
        Assert.Equal(result.GetProperty("graph_input_sequence").GetString(), anchor.GetProperty("graph_input_sequence").GetString());
        Assert.Equal("found", anchor.GetProperty("code").GetString());
        ack = ulong.Parse(response.RootElement.GetProperty("output_sequence").GetString()!, CultureInfo.InvariantCulture);
        using var unknown = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.Encode(new(Epochs, 6, 0,
            new QueryAnchor(Request(2, sector: "unknown"))), ack), CancellationToken.None));
        var negative = Assert.Single(unknown.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("applied", negative.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, negative.GetProperty("error_code").ValueKind);
        Assert.Equal(JsonValueKind.Null, negative.GetProperty("anchor_result").GetProperty("node").ValueKind);
        Assert.Equal("sector_unknown", negative.GetProperty("anchor_result").GetProperty("code").GetString());
    }

    /// <summary>Полный key/position/sector/area body включён в global replay identity.</summary>
    [Theory]
    [InlineData("key")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("z")]
    [InlineData("sector")]
    [InlineData("area")]
    [InlineData("kind")]
    public void AnchorFingerprintCoversEverySemanticField(string field)
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime, Apc("apc", "A", 1, 1));
        var request = Request();
        var input = new RuntimeInput(Epochs, 5, 0, new QueryAnchor(request));
        runtime.Apply(input);
        var different = field switch
        {
            "key" => request with { Key = new("point", 2) },
            "x" => request with { Position = new(2, 1, 1) },
            "y" => request with { Position = new(1, 2, 1) },
            "z" => request with { Position = new(1, 1, 2) },
            "sector" => request with { SectorId = "other" },
            "area" => request with { BaseAreaId = "home" },
            _ => request
        };
        RuntimeCommand command = field == "kind" ? new ConfigureBackbone(true) : new QueryAnchor(different);
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(input with { Command = command }).Apply.Status);
    }
}
