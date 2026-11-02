using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netrun.Core;
using Netrun.Core.Communication;
using Netrun.Core.Routing;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Сквозные проверки metadata → schema/checksum → Runtime catalog → typed transport capability.</summary>
public sealed class CommunicationIntegrationTests
{
    /// <summary>Один независимый world/engine/binding для каждого test runtime.</summary>
    private static readonly Epochs Epochs = new("comm-world", "comm-engine", "comm-binding");

    /// <summary>Полный сырой physical fact; telecomms ports не являются NTnet quantum relays.</summary>
    private static NodeFact Node(string id, int x, int z, string sector, string area, string type = "server") =>
        new(id, 1, 1, new(id, type, new(x, 1, z), sector, true, new(area, area)));

    /// <summary>Native directed bridge через реальные object Z; пустые filters принимают любую положительную частоту.</summary>
    private static CommunicationPortFact Port(string id, CommunicationPortKind kind, string target, int nativeZ) =>
        new(new(id, 1), kind, true, true, true, nativeZ, true, true, true,
            new ValueList<int>([]), new ValueList<NodeKey>([new(target, 1)]));

    /// <summary>Каждый сервис содержит настоящую allowlisted операцию; native facts принадлежат тому же owner.</summary>
    private static ServiceFact Service(string id, string owner, string family, CommunicationPortFact? port = null) =>
        new(id, 1, 1, new(owner, 1), family, "tag", true,
            new ValueList<OperationContract>([new("read", OperationSafety.ReadOnly)]), port);

    /// <summary>Две исходные сети; connected=true даёт один linked stack для проверки обычного transport/coalescing.</summary>
    private static WorldChange[] Facts(bool connected = false, bool duplicatePort = false)
    {
        var left = connected ? "s" : "left";
        var right = connected ? "s" : "right";
        var facts = new List<WorldChange>
        {
            new SectorUpsert(new(left, 1, 1, new ValueList<int>(connected ? [1, 2] : [1])))
        };
        if (!connected)
            facts.Add(new SectorUpsert(new(right, 1, 1, new ValueList<int>([2]))));
        facts.AddRange([
            new NodeUpsert(Node("left-apc", 1, 1, left, "left-area", "apc")),
            new NodeUpsert(Node("a", 2, 1, left, "left-area")),
            new NodeUpsert(Node("r", 3, 1, left, "left-area")),
            new NodeUpsert(Node("right-apc", 1, 2, right, "right-area", "apc")),
            new NodeUpsert(Node("h", 2, 2, right, "right-area")),
            new NodeUpsert(Node("d", 3, 2, right, "right-area")),
            new ServiceUpsert(Service("source", "a", "telecomms.origin")),
            new ServiceUpsert(Service("destination", "d", "telecomms.receiver")),
            new ServiceUpsert(Service("relay-port", "r", "telecomms.relay", Port("r", CommunicationPortKind.Relay, "h", 1))),
            new ServiceUpsert(Service("hub-port", "h", "telecomms.relay", Port("h", CommunicationPortKind.Hub, "r", 2)))
        ]);
        if (duplicatePort)
            facts.Add(new ServiceUpsert(Service("duplicate-r", "r", "telecomms.origin", Port("r", CommunicationPortKind.Relay, "h", 1))));
        return facts.ToArray();
    }

    /// <summary>Последовательная runtime-команда без wall-clock продвижения.</summary>
    private static RuntimeInput Input(ulong sequence, RuntimeCommand command) => new(Epochs, sequence, 0, command);

    /// <summary>Проводит input через настоящую v1.3 schema; snapshot WireDigest создаётся именно из raw bytes changes.</summary>
    private static RuntimeResult ApplyWire(NetworkRuntime runtime, RuntimeInput input) =>
        runtime.Apply(Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(input))).Input);

    /// <summary>Готовит catalog-inclusive checksum v3 с digest, который реально увидит получатель IPC.</summary>
    private static ulong Publish(NetworkRuntime runtime, bool connected = false, bool duplicatePort = false)
    {
        Assert.Equal(ApplyStatus.Applied, ApplyWire(runtime, Input(1, new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)]))).Apply.Status);
        var chunkPacket = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(
            Input(2, new ApplyWorld([new SnapshotChunk("snapshot", 1, Facts(connected, duplicatePort))])))));
        var chunk = Assert.IsType<SnapshotChunk>(Assert.Single(Assert.IsType<ApplyWorld>(chunkPacket.Input.Command).Entries));
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(chunkPacket.Input).Apply.Status);
        var checksum = SnapshotChecksum.Compute([chunk], 3);
        Assert.Equal(ApplyStatus.Applied, ApplyWire(runtime, Input(3, new ApplyWorld([
            new SnapshotCommit("snapshot", 1, (ulong)chunk.Changes.Count, checksum)]))).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, ApplyWire(runtime, Input(4, new ApplyWorld([new CatchUpComplete("snapshot", 0)]))).Apply.Status);
        Assert.False(runtime.TopologyPending);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        return 5;
    }

    /// <summary>Обычный class остаётся default и не получает radio privilege из family или coalescing key.</summary>
    private static NetworkRequest Request(ulong id, TrafficClass traffic = TrafficClass.Ordinary, int? frequency = null,
        string? coalescing = null) => new(new("producer", id), new("source", 1), new("destination", 1), "read",
            NetworkValue.Null, CoalescingKey: coalescing, StateRevision: coalescing is null ? null : id,
            Traffic: traffic, Frequency: frequency);

    /// <summary>Только attested Communications и обе allowlisted families разрешают raw native fallback.</summary>
    [Fact]
    public void WorldMetadataEnablesOnlyExplicitCommunicationTraffic()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        Assert.Equal(2, runtime.CommunicationPortCount);
        var ordinary = ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(1))));
        Assert.Equal(AdmissionStatus.Rejected, ordinary.Admission!.Status);
        var communications = ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(2, TrafficClass.Communications, 1459))));
        Assert.Equal(AdmissionStatus.Accepted, communications.Admission!.Status);
        runtime.Pump();
        Assert.True(runtime.TryDequeueEvent(out var value));
        Assert.Equal(new RequestKey("producer", 2), Assert.IsType<ServiceInvocation>(value).Key);
        var source = runtime.Services["source"];
        ApplyWire(runtime, Input(sequence++, new ApplyWorld([new ServiceUpsert(source with { Revision = 2, Family = "research.client" })])));
        var forbidden = ApplyWire(runtime, Input(sequence, new SubmitRequest(Request(3, TrafficClass.Communications, 1459))));
        Assert.Equal(AdmissionStatus.Rejected, forbidden.Admission!.Status);
        Assert.Equal("communication_route_unavailable", forbidden.Admission.Code);
    }

    /// <summary>Уже handed-off radio invocation сохраняет captured port proof; ordinary authority остаётся независимой.</summary>
    [Fact]
    public void SerializedCommunicationProofDoesNotAcquireNewPortPolicyRevision()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime, connected: true);
        ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(1))));
        var before = ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(2, TrafficClass.Communications))));
        runtime.Pump();
        var invocations = new List<ServiceInvocation>();
        while (runtime.TryDequeueEvent(out var value))
            invocations.Add(Assert.IsType<ServiceInvocation>(value));
        var ordinary = Assert.Single(invocations, value => value.Traffic == TrafficClass.Ordinary);
        Assert.Null(ordinary.CommunicationRevision);
        // Ordered service lane допускает следующий request после известного результата первого.
        ApplyWire(runtime, Input(sequence++, new FinishInvocation(new(Epochs.Binding, ordinary.InvocationId, true))));
        runtime.Pump();
        while (runtime.TryDequeueEvent(out var value))
        {
            if (value is ServiceInvocation invocation) invocations.Add(invocation);
            else Assert.IsType<RequestCompleted>(value);
        }
        var radio = Assert.Single(invocations, value => value.Traffic == TrafficClass.Communications);
        Assert.Equal(before.CommunicationRevision, radio.CommunicationRevision);
        var service = runtime.Services["relay-port"];
        var changed = ApplyWire(runtime, Input(sequence, new ApplyWorld([new ServiceUpsert(service with
        {
            Revision = 2,
            Communication = service.Communication! with { Receiving = false }
        })])));
        Assert.True(changed.CommunicationRevision > radio.CommunicationRevision);
        Assert.Equal(before.InfrastructureStamp, changed.InfrastructureStamp);
        Assert.Equal(before.AvailabilityInfrastructureStamp, changed.AvailabilityInfrastructureStamp);
    }

    /// <summary>Native switches применяются до handoff уже подготовленного invocation; graph publication остаётся прежней.</summary>
    [Fact]
    public void ServicePortDeltaRevokesQueuedNativeInvocationWithoutGraphRebuild()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var graph = runtime.Graph;
        ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(1, TrafficClass.Communications))));
        runtime.Pump();
        var service = runtime.Services["relay-port"];
        var delta = new ServiceUpsert(service with { Revision = 2, Communication = service.Communication! with { Receiving = false } });
        var applied = ApplyWire(runtime, Input(sequence, new ApplyWorld([delta])));
        Assert.Same(graph, runtime.Graph);
        Assert.Null(applied.CommunicationError);
        Assert.True(runtime.TryDequeueEvent(out var value));
        Assert.Equal(RequestState.Failed, Assert.IsType<RequestCompleted>(value).State);
        Assert.False(runtime.TryDequeueEvent(out _));
    }

    /// <summary>Несколько эквивалентных owner advertisements схлопываются, конфликт отзывает только native port и исправляется live.</summary>
    [Fact]
    public void ConflictingOwnerMetadataDoesNotCloseOrdinaryNetworkOrBinding()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime, duplicatePort: true);
        Assert.Equal(2, runtime.CommunicationPortCount);
        var graph = runtime.Graph;
        var duplicate = runtime.Services["duplicate-r"];
        var result = ApplyWire(runtime, Input(sequence++, new ApplyWorld([new ServiceUpsert(duplicate with
        {
            Revision = 2, Communication = duplicate.Communication! with { Receiving = false }
        })])));
        Assert.Equal("communication_port_conflict", result.CommunicationError);
        Assert.Null(result.ErrorCode);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        Assert.Same(graph, runtime.Graph);
        Assert.Equal(1, runtime.CommunicationPortCount);
        var local = ApplyWire(runtime, Input(sequence++, new QueryRoute(new("a", 1), new("r", 1))));
        Assert.Equal(RouteStatus.Found, local.Route!.Status);
        Assert.Equal(AdmissionStatus.Rejected, ApplyWire(runtime, Input(sequence++,
            new SubmitRequest(Request(1, TrafficClass.Communications)))).Admission!.Status);
        result = ApplyWire(runtime, Input(sequence++, new ApplyWorld([new ServiceRemove("duplicate-r", 1, 3)])));
        Assert.Null(result.CommunicationError);
        Assert.Equal(2, runtime.CommunicationPortCount);
        Assert.Same(graph, runtime.Graph);
        Assert.Equal(AdmissionStatus.Accepted, ApplyWire(runtime, Input(sequence,
            new SubmitRequest(Request(2, TrafficClass.Communications)))).Admission!.Status);
    }

    /// <summary>Coalescing не смешивает разные packet classes или radio frequencies одного producer/service pair.</summary>
    [Fact]
    public void CoalescingContextIncludesTrafficClassAndFrequency()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime, connected: true);
        Assert.Equal(AdmissionStatus.Accepted, ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(1, coalescing: "state")))).Admission!.Status);
        Assert.Equal(AdmissionStatus.Accepted, ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(2, TrafficClass.Communications, coalescing: "state")))).Admission!.Status);
        Assert.Equal(AdmissionStatus.Accepted, ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(3, TrafficClass.Communications, 1459, "state")))).Admission!.Status);
        Assert.Equal(AdmissionStatus.Accepted, ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(4, TrafficClass.Communications, 1461, "state")))).Admission!.Status);
        var replacement = ApplyWire(runtime, Input(sequence, new SubmitRequest(Request(5, TrafficClass.Communications, 1459, "state"))));
        Assert.Equal(AdmissionStatus.Coalesced, replacement.Admission!.Status);
        Assert.Equal(new RequestKey("producer", 3), replacement.Admission.CoalescedInto);
        Assert.Equal(4, runtime.ActiveRequestCount);
    }

    /// <summary>Traffic/frequency входят также в полный runtime replay fingerprint, а не только в merge key.</summary>
    [Fact]
    public void ReplayedCommandCannotChangeItsFrequency()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var first = Input(sequence, new SubmitRequest(Request(1, TrafficClass.Communications, 1459)));
        Assert.Equal(ApplyStatus.Applied, ApplyWire(runtime, first).Apply.Status);
        Assert.Equal(ApplyStatus.Duplicate, ApplyWire(runtime, first).Apply.Status);
        var conflict = ApplyWire(runtime, Input(sequence, new SubmitRequest(Request(1, TrafficClass.Communications, 1461))));
        Assert.Equal(ApplyStatus.ResyncRequired, conflict.Apply.Status);
    }

    /// <summary>Native exception после возможного частичного эффекта означает OutcomeUnknown, даже если Success=false.</summary>
    [Fact]
    public void NativeOutcomeUnknownIsNeverReportedAsKnownFailureOrRetried()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime, connected: true);
        ApplyWire(runtime, Input(sequence++, new SubmitRequest(Request(1))));
        runtime.Pump();
        Assert.True(runtime.TryDequeueEvent(out var invocation));
        var id = Assert.IsType<ServiceInvocation>(invocation).InvocationId;
        var result = new InvocationResult(Epochs.Binding, id, false, Code: "outcome_unknown");
        Assert.True(ApplyWire(runtime, Input(sequence++, new FinishInvocation(result))).Action!.Accepted);
        Assert.True(runtime.TryDequeueEvent(out var value));
        Assert.Equal(RequestState.OutcomeUnknown, Assert.IsType<RequestCompleted>(value).State);
        Assert.True(ApplyWire(runtime, Input(sequence, new FinishInvocation(result))).Action!.Accepted);
        runtime.Pump();
        Assert.False(runtime.TryDequeueEvent(out _));
        Assert.Equal(0, runtime.ActiveRequestCount);
    }

    /// <summary>Service metadata требует minor/checksum 3 и точного совпадения наследуемого owner.</summary>
    [Fact]
    public void ServicePortSchemaAndChecksumAreVersionGated()
    {
        var service = Service("port", "r", "telecomms.relay", Port("r", CommunicationPortKind.Relay, "h", 1));
        var fact = new ServiceUpsert(service);
        var batch = new InputBatch(Epochs, 1, 0, [fact]);
        var encoded = BatchJson.Encode(batch);
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(3, json.RootElement.GetProperty("protocol_minor").GetInt32());
        Assert.Equal(service, Assert.IsType<ServiceUpsert>(Assert.Single(BatchJson.Decode(encoded).Entries)).Service);
        var old = Encoding.UTF8.GetString(encoded).Replace("\"protocol_minor\":3", "\"protocol_minor\":2", StringComparison.Ordinal);
        Assert.Throws<ProtocolException>(() => BatchJson.Decode(Encoding.UTF8.GetBytes(old)));
        Assert.Throws<ArgumentException>(() => SnapshotChecksum.Compute([new SnapshotChunk("s", 1, [fact])], 2));
        var mismatched = service with { Communication = service.Communication! with { Owner = new("other", 1) } };
        Assert.False(FactValidation.IsValid(new ServiceUpsert(mismatched)));
        Assert.Throws<ProtocolException>(() => BatchJson.Encode(batch with { Entries = [new ServiceUpsert(mismatched)] }));
    }

    /// <summary>Старый service exporter без communication продолжает работать; explicit null допустим только в minor 3.</summary>
    [Fact]
    public void OptionalPortMetadataPreservesOldServiceWireForm()
    {
        var service = new ServiceUpsert(Service("source", "a", "telecomms.origin"));
        var batch = new InputBatch(Epochs, 1, 0, [service]);
        var legacy = BatchJson.Encode(batch);
        using var legacyJson = JsonDocument.Parse(legacy);
        Assert.Equal(2, legacyJson.RootElement.GetProperty("protocol_minor").GetInt32());
        Assert.False(legacyJson.RootElement.GetProperty("entries")[0].TryGetProperty("communication", out _));
        var modern = JsonNode.Parse(BatchJson.Encode(batch, 3))!;
        modern["entries"]![0]!["communication"] = null;
        Assert.Null(Assert.IsType<ServiceUpsert>(Assert.Single(BatchJson.Decode(Encoding.UTF8.GetBytes(modern.ToJsonString())).Entries)).Service.Communication);
        modern["protocol_minor"] = 2;
        Assert.Throws<ProtocolException>(() => BatchJson.Decode(Encoding.UTF8.GetBytes(modern.ToJsonString())));
    }

    /// <summary>Новые optional request fields round-trip, а default ordinary shape остаётся прежним.</summary>
    [Fact]
    public void RequestTrafficAndFrequencyAreOptionalAndStrict()
    {
        var ordinary = RuntimeJson.Encode(Input(1, new SubmitRequest(Request(1))));
        using var ordinaryJson = JsonDocument.Parse(ordinary);
        Assert.False(ordinaryJson.RootElement.GetProperty("entries")[0].TryGetProperty("traffic_class", out _));
        var decoded = Assert.IsType<SubmitRequest>(Assert.IsType<RuntimePacket>(RuntimeJson.Decode(ordinary)).Input.Command).Request;
        Assert.Equal(TrafficClass.Ordinary, decoded.Traffic);
        Assert.Null(decoded.Frequency);
        var radio = RuntimeJson.Encode(Input(1, new SubmitRequest(Request(1, TrafficClass.Communications, 1459))));
        decoded = Assert.IsType<SubmitRequest>(Assert.IsType<RuntimePacket>(RuntimeJson.Decode(radio)).Input.Command).Request;
        Assert.Equal(TrafficClass.Communications, decoded.Traffic);
        Assert.Equal(1459, decoded.Frequency);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(Input(1, new SubmitRequest(Request(1, frequency: 1459)))));
        var malformed = JsonNode.Parse(radio)!;
        malformed["entries"]![0]!["frequency"] = 0;
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(malformed.ToJsonString())));
        var duplicate = Encoding.UTF8.GetString(radio).Replace("\"traffic_class\":\"communications\"",
            "\"traffic_class\":\"communications\",\"traffic_class\":\"ordinary\"", StringComparison.Ordinal);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(duplicate)));
        malformed = JsonNode.Parse(ordinary)!;
        malformed["entries"]![0]!["unexpected_optional"] = true;
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(malformed.ToJsonString())));
    }

    /// <summary>Raw-wire checksum v3 покрывает native switches; неизвестные native fields и дубли frequencies не принимаются.</summary>
    [Fact]
    public void NativeMetadataIsStrictAndIncludedInRawSnapshotDigest()
    {
        var service = Service("port", "r", "telecomms.relay", Port("r", CommunicationPortKind.Relay, "h", 1));
        var packet = BatchJson.Encode(new InputBatch(Epochs, 1, 0, [new SnapshotChunk("s", 1, [new ServiceUpsert(service)])]));
        var first = Assert.IsType<SnapshotChunk>(Assert.Single(BatchJson.Decode(packet).Entries));
        var modified = JsonNode.Parse(packet)!;
        modified["entries"]![0]!["changes"]![0]!["communication"]!["receiving"] = false;
        var second = Assert.IsType<SnapshotChunk>(Assert.Single(BatchJson.Decode(Encoding.UTF8.GetBytes(modified.ToJsonString())).Entries));
        Assert.NotEqual(first.WireDigest, second.WireDigest);
        Assert.NotEqual(SnapshotChecksum.Compute([first], 3), SnapshotChecksum.Compute([second], 3));
        modified["entries"]![0]!["changes"]![0]!["communication"]!["owner"] = "forged";
        Assert.Throws<ProtocolException>(() => BatchJson.Decode(Encoding.UTF8.GetBytes(modified.ToJsonString())));
        modified = JsonNode.Parse(packet)!;
        modified["entries"]![0]!["changes"]![0]!["communication"]!["listening_frequencies"] = new JsonArray(1459, 1459);
        Assert.Throws<ProtocolException>(() => BatchJson.Decode(Encoding.UTF8.GetBytes(modified.ToJsonString())));
    }
}
