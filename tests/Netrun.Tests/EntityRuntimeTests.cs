using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Runtime;
using Netrun.Core.Scheduling;
using Netrun.Core.Transport;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет actor FIFO через production runtime/protocol, без DM-состояния или тестового решателя.</summary>
public sealed class EntityRuntimeTests
{
    private static readonly Epochs Epochs = new("world", "engine", "binding");
    private static readonly ActorKey Actor = new("actor", 1);
    private static readonly NodeKey Node = new("node", 1);
    private static EntityDefinition Definition() => new(Actor, EntityKind.Resident, Node, 100, new("Runner"), "owner.1");
    private static RuntimeInput Input(ulong sequence, RuntimeCommand command) => new(Epochs, sequence, 0, command);
    private static ulong Publish(NetworkRuntime runtime)
    {
        WorldChange[] facts = [new SectorUpsert(new("s", 1, 1, new([1]))),
            new NodeUpsert(new("node", 1, 1, new("node", "apc", new(1, 1, 1), "s", true, new("home", "home"))))];
        var chunk = new SnapshotChunk("s", 1, facts);
        runtime.Apply(Input(1, new ApplyWorld([new SnapshotBegin("s", 0, 2)])));
        runtime.Apply(Input(2, new ApplyWorld([chunk])));
        runtime.Apply(Input(3, new ApplyWorld([new SnapshotCommit("s", 1, (ulong)facts.Length, SnapshotChecksum.Compute([chunk], 2))])));
        runtime.Apply(Input(4, new ApplyWorld([new CatchUpComplete("s", 0)])));
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        return 5;
    }

    [Fact]
    public void ActorCommandsOnlyEnqueueAndExecuteInOneFifoPrefix()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var create = new SubmitEntityOperation(new(new("actor.tasks", 1), Actor, null, new CreateEntity(Definition())));
        var damage = new SubmitEntityOperation(new(new("actor.tasks", 2), Actor, null, new DamageEntity(20)));
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, create)).Admission!.Status);
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, damage)).Admission!.Status);
        Assert.Null(runtime.ReadEntity(Actor));
        Assert.Equal(2, runtime.PendingTaskCount);
        runtime.Pump(100);
        Assert.Equal(80, runtime.ReadEntity(Actor)!.Integrity);
        var outcomes = new List<CoreTaskCompleted>();
        while (runtime.TryDequeueEvent(out var output)) if (output is CoreTaskCompleted task) outcomes.Add(task);
        Assert.Equal(new ulong[] { 1, 2 }, outcomes.Select(item => item.TaskSequence));
        Assert.All(outcomes, item => Assert.True(item.Success));
        Assert.Equal(AdmissionStatus.Duplicate, runtime.Apply(Input(sequence, damage)).Admission!.Status);
        runtime.Pump(100);
        Assert.Equal(80, runtime.ReadEntity(Actor)!.Integrity);
    }

    [Fact]
    public void WireRoundTripHasSingleActorIdentityAndCanonicalCounters()
    {
        var input = Input(1, new SubmitEntityOperation(new(new("actor.tasks", 1), Actor, null, new CreateEntity(Definition()))));
        var encoded = RuntimeJson.Encode(input);
        var json = Encoding.UTF8.GetString(encoded);
        Assert.Contains("\"actor_id\":\"actor\"", json);
        var decoded = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(encoded));
        var operation = Assert.IsType<SubmitEntityOperation>(decoded.Input.Command);
        Assert.Equal(input.Command, operation);
        var invalid = json.Replace("\"generation\":\"1\"", "\"generation\":1", StringComparison.Ordinal);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    public void UnknownDuplicateAndUnsafeActionDataAreRejected()
    {
        var encoded = RuntimeJson.Encode(Input(1, new SubmitEntityOperation(new(new("actor.tasks", 1), Actor, null, new DamageEntity(20)))));
        var json = Encoding.UTF8.GetString(encoded);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace("\"amount\":20", "\"amount\":20,\"amount\":30", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace("\"amount\":20", "\"amount\":20,\"proc\":\"execute\"", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace("\"amount\":20", "\"amount\":-20", StringComparison.Ordinal))));
    }

    [Fact]
    public void RawAccessCodecPreservesEmptyAndNullRequirementsAndEveryScopeCounter()
    {
        var network = new NetworkKey("net", 18446744073709551615UL);
        var access = new EntityAccessSnapshot(new("session", "principal", new([20, 67])),
            new([new NodeAccessPolicy(Node, network, 3, new([]))]),
            new([new NetworkAccessPolicy(network, "epoch", new([]))]),
            new([new NodeAccessGrant(Node, AccessLevel.User, network, "epoch", 3)]), new([]), new([]));
        var input = Input(1, new QueryEntityNavigation(new(new("nav", 1), Actor, 1, Node, Node, access)));
        var encoded = RuntimeJson.Encode(input);
        var decoded = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(encoded));
        var result = Assert.IsType<QueryEntityNavigation>(decoded.Input.Command);
        Assert.Equal(access, result.Request.Access);
        Assert.NotNull(result.Request.Access.Nodes[0].RequiredAccess);
        using var duplicate = JsonDocument.Parse("{\"actor_id\":\"a\",\"generation\":\"1\",\"generation\":\"2\"}");
        Assert.Throws<ProtocolException>(() => EntityDataJson.Read<ActorKey>(duplicate.RootElement));
    }

    [Fact]
    public void EntityCapacityRejectsOneTaskWithoutDeadlockingAQueuedDelete()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Entities: new(MaxEntities: 1)));
        var sequence = Publish(runtime);
        var other = new ActorKey("other", 1);
        runtime.Apply(Input(sequence++, new SubmitEntityOperation(new(new("tasks", 1), Actor, null, new CreateEntity(Definition())))));
        runtime.Apply(Input(sequence++, new SubmitEntityOperation(new(new("tasks", 2), other, null,
            new CreateEntity(Definition() with { Actor = other })))));
        runtime.Apply(Input(sequence, new SubmitEntityOperation(new(new("tasks", 3), Actor, null, new DeleteEntity()))));
        runtime.Pump(100);
        Assert.Equal(0, runtime.PendingTaskCount);
        Assert.Null(runtime.ReadEntity(Actor));
        Assert.Null(runtime.ReadEntity(other));
        var completed = new List<CoreTaskCompleted>();
        while (runtime.TryDequeueEvent(out var output)) if (output is CoreTaskCompleted task) completed.Add(task);
        Assert.Equal(new[] { true, false, true }, completed.Select(item => item.Success));
        Assert.Equal("entity_capacity", completed[1].Code);
    }
}
