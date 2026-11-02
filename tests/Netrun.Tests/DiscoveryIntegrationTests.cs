using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Discovery;
using Netrun.Core.Communication;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет discovery через реальные codec, snapshot, runtime, router и Host output journal.</summary>
public sealed class DiscoveryIntegrationTests
{
    /// <summary>Один synthetic binding для полностью изолированных runtime экземпляров.</summary>
    private static readonly Epochs Epochs = new("discovery-world", "discovery-engine", "discovery-binding");

    /// <summary>Wire peer подтверждает только уже прочитанные outputs и не назначает время poll.</summary>
    private sealed class Peer(IHostSession session)
    {
        /// <summary>Последний прочитанный output sequence.</summary>
        internal ulong Ack;
        /// <summary>Последний отправленный logical input sequence.</summary>
        internal ulong Sequence;

        /// <summary>Использует настоящий strict serializer и production session.</summary>
        internal async Task<byte[]> Send(RuntimeInput input)
        {
            var body = await session.ProcessAsync(RuntimeJson.Encode(input, Ack), CancellationToken.None);
            using var output = JsonDocument.Parse(body);
            Ack = Counter(output.RootElement, "output_sequence");
            Sequence = input.Sequence;
            return body;
        }

        /// <summary>Назначает только causal input sequence; wall clock в протокол не попадает.</summary>
        internal Task<byte[]> Command(RuntimeCommand command, int time = 0) => Send(new(Epochs, Sequence + 1, time, command));

        /// <summary>Poll изменяет output ACK, сохраняя logical sequence и simulation time.</summary>
        internal async Task<byte[]> Poll(int maximumEvents = 64)
        {
            var body = await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, Ack, maximumEvents), CancellationToken.None);
            using var output = JsonDocument.Parse(body);
            Ack = Counter(output.RootElement, "output_sequence");
            return body;
        }
    }

    /// <summary>Полный declared service с allowlisted read operation; native acceptance не сериализуется.</summary>
    private static ServiceFact Service(string id, string owner, string family, string? tag = "tag", ulong generation = 1, ulong revision = 1) =>
        new(id, generation, revision, new(owner, 1), family, tag, true, new([new("read", OperationSafety.ReadOnly)], 64));

    /// <summary>Стабильные адреса определяют сравнимый порядок candidate pages.</summary>
    private static string Target(int index) => "target-" + index.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>Действительная topology: один APC anchor и физически достижимые server endpoints.</summary>
    private static WorldChange[] Facts(int targets)
    {
        var facts = new List<WorldChange>
        {
            new SectorUpsert(new("sector", 1, 1, new([1]))),
            new NodeUpsert(new("source-node", 1, 1, new("source-address", "apc", new(1, 1, 1), "sector", true, new("home", "home")))),
            new ServiceUpsert(Service("source", "source-node", "origin"))
        };
        for (var i = 0; i < targets; i++)
        {
            var id = Target(i);
            facts.Add(new NodeUpsert(new(id + "-node", 1, 1, new(id, "server", new(i + 2, 1, 1), "sector", true, new("home", "home")))));
            facts.Add(new ServiceUpsert(Service(id, id + "-node", "research.authority")));
        }
        return facts.ToArray();
    }

    /// <summary>Получает decoder-derived raw digest каждого фактического chunk и строит полный snapshot v3.</summary>
    private static IEnumerable<RuntimeInput> Snapshot(int targets)
    {
        ulong sequence = 1;
        yield return new(Epochs, sequence++, 0, new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)]));
        var chunks = new List<SnapshotChunk>();
        ulong index = 0;
        foreach (var changes in Facts(targets).Chunk(128))
        {
            var input = new RuntimeInput(Epochs, sequence++, 0, new ApplyWorld([new SnapshotChunk("snapshot", ++index, changes)]));
            var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(input)));
            chunks.Add(Assert.IsType<SnapshotChunk>(Assert.Single(Assert.IsType<ApplyWorld>(packet.Input.Command).Entries)));
            yield return packet.Input;
        }
        yield return new(Epochs, sequence++, 0, new ApplyWorld([new SnapshotCommit("snapshot", index,
            (ulong)Facts(targets).Length, SnapshotChecksum.Compute(chunks, 3))]));
        yield return new(Epochs, sequence, 0, new ApplyWorld([new CatchUpComplete("snapshot", 0)]));
    }

    /// <summary>Применяет real wire models к runtime; next input всегда использует подтверждённую sequence границу.</summary>
    private static RuntimeResult Apply(NetworkRuntime runtime, RuntimeCommand command, int time = 0)
    {
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(new(Epochs, runtime.AppliedSequence + 1, time, command))));
        return runtime.Apply(packet.Input);
    }

    /// <summary>Initial publication заполняет directory один раз из согласованного mirror.</summary>
    private static void Publish(NetworkRuntime runtime, int targets = 1)
    {
        foreach (var input in Snapshot(targets)) Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
    }

    /// <summary>Стандартный bounded query не ограничивает authorities до native selects/accepts.</summary>
    private static DiscoveryRequest Request(ulong sequence = 1) => new(new("lookup", sequence), new("source", 1), "research.authority");

    /// <summary>Снимает один общий event, оставляя type assertion вызывающему тесту.</summary>
    private static TransportEvent Take(NetworkRuntime runtime)
    {
        Assert.True(runtime.TryDequeueEvent(out var value));
        return Assert.IsAssignableFrom<TransportEvent>(value);
    }

    /// <summary>String counters не проходят через округляющее JSON number.</summary>
    private static ulong Counter(JsonElement element, string property) => ulong.Parse(element.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);
    /// <summary>Читает bounded output entries нужного allowlisted типа.</summary>
    private static IEnumerable<JsonElement> Entries(JsonDocument output, string kind) => output.RootElement.GetProperty("results").EnumerateArray()
        .Where(entry => entry.GetProperty("kind").GetString() == kind);

    /// <summary>Новые logical commands сохраняют typed metadata и canonical UInt64 counters.</summary>
    [Fact]
    public void DiscoveryAndCancellationRoundTripThroughStrictRuntimeCodec()
    {
        RuntimeCommand[] commands =
        [
            new DiscoverServices(Request()),
            new DiscoverServices(new(new("producer", ulong.MaxValue), new("source", ulong.MaxValue), "pda.messenger", new string('t', 256), true, 1459, 1)),
            new DiscoverServices(Request() with { Communications = true }),
            new CancelDiscovery(new("producer", ulong.MaxValue))
        ];
        foreach (var command in commands)
        {
            var input = new RuntimeInput(Epochs, 42, 7, command);
            var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(input, ulong.MaxValue)));
            Assert.Equal(input, packet.Input);
            Assert.Equal(ulong.MaxValue, packet.AckOutputSequence);
        }
    }

    /// <summary>Discovery schema обязательна целиком: duplicates/unknown fields, mixed frames и некорректные counters отвергаются.</summary>
    [Fact]
    public void DiscoveryWireRejectsMalformedMetadataAndMixedWorldFrames()
    {
        var valid = Encoding.UTF8.GetString(RuntimeJson.Encode(new(Epochs, 1, 0, new DiscoverServices(Request()))));
        foreach (var invalid in new[]
        {
            valid.Replace("\"max_results\":4096", "\"max_results\":0", StringComparison.Ordinal),
            valid.Replace("\"max_results\":4096", "\"max_results\":4097", StringComparison.Ordinal),
            valid.Replace("\"max_results\":4096", "\"max_results\":\"4096\"", StringComparison.Ordinal),
            valid.Replace("\"max_results\":4096", "\"max_results\":4096,\"max_results\":4096", StringComparison.Ordinal),
            valid.Replace("\"max_results\":4096", "\"max_results\":4096,\"unknown\":true", StringComparison.Ordinal),
            valid.Replace(",\"recipient_tag\":null", "", StringComparison.Ordinal),
            valid.Replace("\"recipient_tag\":null", "\"recipient_tag\":\"\\uD800\"", StringComparison.Ordinal),
            valid.Replace("\"communications\":false", "\"communications\":0", StringComparison.Ordinal),
            valid.Replace("\"frequency\":null", "\"frequency\":0", StringComparison.Ordinal),
            valid.Replace("\"frequency\":null", "\"frequency\":1459", StringComparison.Ordinal),
            valid.Replace("\"request_id\":\"lookup:1\"", "\"request_id\":\"lookup:01\"", StringComparison.Ordinal),
            valid.Replace("\"generation\":\"1\"", "\"generation\":1", StringComparison.Ordinal),
            valid[..^2] + ", {\"kind\":\"node_remove\",\"node_id\":\"n\",\"generation\":\"1\",\"node_revision\":\"1\"}]}"
        })
            Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        var cancel = Encoding.UTF8.GetString(RuntimeJson.Encode(new(Epochs, 1, 0, new CancelDiscovery(new("lookup", 1)))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(cancel.Replace("\"request_id\":\"lookup:1\"",
            "\"request_id\":\"lookup:1\",\"source\":null", StringComparison.Ordinal))));
    }

    /// <summary>Initial snapshot создаёт реальные маршруты, duplicate frame не начинает второй query, а все страницы укладываются в budget.</summary>
    [Fact]
    public void RealSnapshotDiscoveryAndGlobalReplayShareOneCausalRuntime()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Discovery: new(PageSize: 1)));
        Publish(runtime, 3);
        Assert.Equal(4, runtime.DiscoveryServiceCount);
        var input = new RuntimeInput(Epochs, runtime.AppliedSequence + 1, 0, new DiscoverServices(Request()));
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(input).Admission!.Status);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(input).Apply.Status);
        Assert.Equal(1, runtime.ActiveDiscoveryCount);
        var keys = new List<ServiceKey>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, runtime.Pump(1));
            keys.AddRange(Assert.IsType<DiscoveryPage>(Take(runtime)).Candidates);
        }
        Assert.Equal(Enumerable.Range(0, 3).Select(Target), keys.Select(key => key.ServiceId));
        Assert.Equal(0, runtime.ActiveDiscoveryCount);
        Assert.Equal(0, runtime.SimulationTimeDs);
        Assert.Equal(AdmissionStatus.Duplicate, Apply(runtime, new DiscoverServices(Request())).Admission!.Status);
        Assert.False(runtime.TryDequeueEvent(out _));
    }

    /// <summary>Service-only generation/tag changes обновляют индекс O(touched), а node-only и stale service input не меняют directory revision.</summary>
    [Fact]
    public void DirectoryTracksOnlyAcceptedChangedServiceIdsAndNeverOldGenerations()
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime);
        var graph = runtime.Graph;
        var revision = runtime.DiscoveryDirectoryRevision;
        var node = runtime.Nodes[Target(0) + "-node"];
        Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = 2, State = node.State with { Online = false } })]));
        Assert.Equal(revision, runtime.DiscoveryDirectoryRevision);
        Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = 3 })]));
        var replacement = Service(Target(0), Target(0) + "-node", "research.authority", "new-tag", generation: 2);
        Apply(runtime, new ApplyWorld([new ServiceUpsert(replacement)]));
        Assert.Equal(revision + 1, runtime.DiscoveryDirectoryRevision);
        Assert.Same(graph, runtime.Graph);
        Apply(runtime, new ApplyWorld([new ServiceUpsert(Service(Target(0), Target(0) + "-node", "research.authority", revision: 99))]));
        Assert.Equal(revision + 1, runtime.DiscoveryDirectoryRevision);
        Apply(runtime, new DiscoverServices(Request() with { RecipientTag = "new-tag" }));
        runtime.Pump(10);
        Assert.Equal(new ServiceKey(Target(0), 2), Assert.Single(Assert.IsType<DiscoveryPage>(Take(runtime)).Candidates));
        Apply(runtime, new ApplyWorld([new ServiceRemove(Target(0), 2, 2)]));
        Assert.Equal(1, runtime.DiscoveryServiceCount);
        Apply(runtime, new DiscoverServices(Request(2)));
        runtime.Pump(10);
        Assert.Empty(Assert.IsType<DiscoveryPage>(Take(runtime)).Candidates);
    }

    /// <summary>Pending graph принимает bounded query, но не выдаёт старые routes; Pump(0) публикует graph без lookup work.</summary>
    [Fact]
    public void DeferredTopologyKeepsDiscoveryParkedUntilFreshPublication()
    {
        using var runtime = new NetworkRuntime(Epochs, new(DeferTopologyBuild: true));
        Publish(runtime);
        Assert.Null(runtime.Graph);
        Assert.Equal(AdmissionStatus.Accepted, Apply(runtime, new DiscoverServices(Request())).Admission!.Status);
        Assert.False(runtime.TryDequeueEvent(out _));
        Assert.Equal(0, runtime.Pump(0));
        Assert.NotNull(runtime.Graph);
        Assert.False(runtime.TryDequeueEvent(out _));
        Assert.Equal(1, runtime.Pump(1));
        Assert.Single(Assert.IsType<DiscoveryPage>(Take(runtime)).Candidates);
        var node = runtime.Nodes[Target(0) + "-node"];
        Apply(runtime, new ApplyWorld([new NodeUpsert(node with { Revision = 2, State = node.State with { Position = new(20, 1, 1) } })]));
        Assert.Null(runtime.Graph);
        Apply(runtime, new DiscoverServices(Request(2)));
        Assert.False(runtime.TryDequeueEvent(out _));
        runtime.Pump(0);
        runtime.Pump(1);
        Assert.Single(Assert.IsType<DiscoveryPage>(Take(runtime)).Candidates);
    }

    /// <summary>Общий бюджет одного work unit и разные full event queues не позволяют одной lane вытеснить другую.</summary>
    [Fact]
    public void TransportAndDiscoveryShareBudgetAndOutputFairlyUnderPressure()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Transport: new(MaxEvents: 1), Discovery: new(MaxEvents: 1, PageSize: 1)));
        Publish(runtime, 3);
        Apply(runtime, new DiscoverServices(Request()));
        Apply(runtime, new SubmitRequest(new(new("machine", 1), new("source", 1), new(Target(0), 1), "read", NetworkValue.Null)));
        Assert.Equal(1, runtime.Pump(1));
        Assert.Equal(1, runtime.Pump(1));
        Assert.Equal(2, runtime.EventCount);
        Assert.Equal(0, runtime.Pump(1));
        var first = Take(runtime);
        var second = Take(runtime);
        var invocation = Assert.Single(new[] { first, second }.OfType<ServiceInvocation>());
        Assert.Single(new[] { first, second }.OfType<DiscoveryPage>());
        Apply(runtime, new FinishInvocation(new(Epochs.Binding, invocation.InvocationId, true)));
        Assert.Equal(1, runtime.Pump(1));
        var next = new[] { Take(runtime), Take(runtime) };
        Assert.Single(next.OfType<RequestCompleted>());
        Assert.Single(next.OfType<DiscoveryPage>());
        Assert.Equal(0, runtime.SimulationTimeDs);
    }

    /// <summary>Host journal переносит только bounded discovery_page identities; ACK-only polls сохраняют input clock/sequence и output pressure.</summary>
    [Fact]
    public async Task WireSnapshotQueryPollAndCancellationUseProductionHostSession()
    {
        using var session = new RuntimeHostSessionFactory(new(PumpWorkBudget: 1, Discovery: new(MaxEvents: 1, PageSize: 1))).Create(Epochs);
        var peer = new Peer(session);
        foreach (var input in Snapshot(3)) await peer.Send(input);
        using var submitted = JsonDocument.Parse(await peer.Command(new DiscoverServices(Request()), 7));
        var admission = Assert.Single(Entries(submitted, "runtime_result")).GetProperty("admission");
        Assert.Equal("accepted", admission.GetProperty("status").GetString());
        var first = Assert.Single(Entries(submitted, "discovery_page"));
        Assert.Equal(new[] { "binding_epoch", "request_id", "kind", "candidates", "complete", "code" }.Order(),
            first.EnumerateObject().Select(property => property.Name).Order());
        Assert.False(first.GetProperty("complete").GetBoolean());
        var applied = Counter(submitted.RootElement, "applied_sequence");
        using var held = JsonDocument.Parse(await peer.Poll(0));
        using var pressure = JsonDocument.Parse(await peer.Poll(0));
        Assert.Empty(held.RootElement.GetProperty("results").EnumerateArray());
        Assert.Empty(pressure.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(applied, Counter(pressure.RootElement, "applied_sequence"));
        using var second = JsonDocument.Parse(await peer.Poll(1));
        Assert.Equal(Target(1), Assert.Single(Assert.Single(Entries(second, "discovery_page")).GetProperty("candidates").EnumerateArray())
            .GetProperty("service_id").GetString());
        using var completed = JsonDocument.Parse(await peer.Poll(1));
        var last = Assert.Single(Entries(completed, "discovery_page"));
        Assert.True(last.GetProperty("complete").GetBoolean());
        Assert.Equal("completed", last.GetProperty("code").GetString());
        Assert.Equal(Target(2), Assert.Single(last.GetProperty("candidates").EnumerateArray()).GetProperty("service_id").GetString());
        Assert.Equal(applied, Counter(completed.RootElement, "applied_sequence"));
        using var nextQuery = JsonDocument.Parse(await peer.Command(new DiscoverServices(Request(2)), 7));
        Assert.False(Assert.Single(Entries(nextQuery, "discovery_page")).GetProperty("complete").GetBoolean());
        using var cancelled = JsonDocument.Parse(await peer.Command(new CancelDiscovery(Request(2).Key), 7));
        Assert.True(Assert.Single(Entries(cancelled, "runtime_result")).GetProperty("action").GetProperty("accepted").GetBoolean());
        var terminal = Assert.Single(Entries(cancelled, "discovery_page"));
        Assert.True(terminal.GetProperty("complete").GetBoolean());
        Assert.Equal("cancelled", terminal.GetProperty("code").GetString());
        Assert.Empty(terminal.GetProperty("candidates").EnumerateArray());
        using var sameTime = JsonDocument.Parse(await peer.Command(new ConfigureBackbone(true), 7));
        var status = Assert.Single(Entries(sameTime, "runtime_result"));
        Assert.Equal("applied", status.GetProperty("status").GetString());
        Assert.Equal(7, status.GetProperty("simulation_time_ds").GetInt32());
    }

    /// <summary>Semantic would_block подтверждает outer sequence, но не расходует domain serial и не выполняется снова при replay того же frame.</summary>
    [Fact]
    public void DiscoveryPressureConsumesLogicalFrameButKeepsDomainRequestRetryable()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Discovery: new(MaxQueries: 1)));
        Publish(runtime, 3);
        Apply(runtime, new DiscoverServices(Request()));
        var input = new RuntimeInput(Epochs, runtime.AppliedSequence + 1, 0, new DiscoverServices(Request(2)));
        var blocked = runtime.Apply(input);
        Assert.Equal(ApplyStatus.Applied, blocked.Apply.Status);
        Assert.Equal(AdmissionStatus.WouldBlock, blocked.Admission!.Status);
        Assert.Equal(input.Sequence, runtime.AppliedSequence);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(input).Apply.Status);
        Assert.Equal(1, runtime.ActiveDiscoveryCount);
        runtime.Pump(10);
        Assert.True(Assert.IsType<DiscoveryPage>(Take(runtime)).Complete);
        Assert.Equal(AdmissionStatus.Accepted, Apply(runtime, new DiscoverServices(Request(2))).Admission!.Status);
    }

    /// <summary>Host передаёт native advertisement conflict отдельным diagnostic полем, сохраняя пригодную ordinary сеть.</summary>
    [Fact]
    public async Task CommunicationDiagnosticDoesNotBecomeFatalRuntimeError()
    {
        using var session = new RuntimeHostSessionFactory().Create(Epochs);
        var peer = new Peer(session);
        foreach (var input in Snapshot(1)) await peer.Send(input);
        var port = new CommunicationPortFact(new("source-node", 1), CommunicationPortKind.Hub, true, true, true,
            1, false, true, true, new([]), new([]));
        var first = Service("port-a", "source-node", "telecomms.receiver") with { Communication = port };
        var second = Service("port-b", "source-node", "telecomms.receiver") with { Communication = port with { NativeZ = 2 } };
        using var changed = JsonDocument.Parse(await peer.Command(new ApplyWorld([new ServiceUpsert(first), new ServiceUpsert(second)])));
        var result = Assert.Single(Entries(changed, "runtime_result"));
        Assert.Equal("communication_port_conflict", result.GetProperty("communication_error").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error_code").ValueKind);
        Assert.False(result.GetProperty("topology_pending").GetBoolean());
        using var found = JsonDocument.Parse(await peer.Command(new DiscoverServices(Request())));
        Assert.Single(Assert.Single(Entries(found, "discovery_page")).GetProperty("candidates").EnumerateArray());
    }

    /// <summary>Каждое semantic поле нового query входит в global frame fingerprint, включая nullable frequency и result cap.</summary>
    [Theory]
    [InlineData("key")]
    [InlineData("source")]
    [InlineData("generation")]
    [InlineData("family")]
    [InlineData("tag")]
    [InlineData("communications")]
    [InlineData("frequency")]
    [InlineData("limit")]
    [InlineData("cancel")]
    public void DiscoveryFrameFingerprintIncludesAllTypedMetadata(string changed)
    {
        using var runtime = new NetworkRuntime(Epochs);
        Publish(runtime);
        var request = Request() with { Communications = true };
        var input = new RuntimeInput(Epochs, runtime.AppliedSequence + 1, 0, new DiscoverServices(request));
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
        var different = changed switch
        {
            "key" => request with { Key = new("lookup", 2) },
            "source" => request with { Source = new("other", 1) },
            "generation" => request with { Source = new("source", 2) },
            "family" => request with { DestinationFamily = "other" },
            "tag" => request with { RecipientTag = "other" },
            "communications" => request with { Communications = false },
            "frequency" => request with { Frequency = 1459 },
            "limit" => request with { MaxResults = 1 },
            _ => request
        };
        RuntimeCommand command = changed == "cancel" ? new CancelDiscovery(request.Key) : new DiscoverServices(different);
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(input with { Command = command }).Apply.Status);
        Assert.Equal(0, runtime.ActiveDiscoveryCount);
        Assert.False(runtime.TryDequeueEvent(out _));
    }
}
