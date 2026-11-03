using Netrun.Core;
using Netrun.Core.Discovery;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Service watches report changes without polling, routing, or native invocation.</summary>
public sealed class ServiceWatchTests
{
    private sealed class World : INetworkWorldView
    {
        public Dictionary<string, NodeFact> NodeMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ServiceFact> ServiceMap { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, NodeFact> Nodes => NodeMap;
        public IReadOnlyDictionary<string, ServiceFact> Services => ServiceMap;
        public IReadOnlyDictionary<string, SectorFact> Sectors { get; } = new Dictionary<string, SectorFact>();
    }

    private static ServiceFact Service(string id, string owner, string family, string? tag, ulong revision = 1, ulong generation = 1) =>
        new(id, generation, revision, new(owner, 1), family, tag, true, new ValueList<OperationContract>([new("read", OperationSafety.ReadOnly)]));

    private static World Fixture()
    {
        var world = new World();
        world.NodeMap["source-node"] = new("source-node", 1, 1, new("SRC", "server", null, null, true));
        world.NodeMap["target-node"] = new("target-node", 1, 1, new("DST", "server", null, null, true));
        world.ServiceMap["source"] = Service("source", "source-node", "test.source", null);
        world.ServiceMap["target"] = Service("target", "target-node", "test.target", "alpha");
        return world;
    }

    [Fact]
    public void WatchReplaysExistingThenPublishesChangesAndTombstones()
    {
        var world = Fixture();
        var directory = new ServiceDirectory();
        foreach (var service in world.Services.Values)
            Assert.Equal(DirectoryUpdateResult.Applied, directory.Upsert(service));
        using var watch = new ServiceWatchBroker(world, directory, "binding");
        var request = new ServiceWatchRequest(new("watch", 1), new("source", 1), "test.target", "alpha");
        Assert.Equal(AdmissionStatus.Accepted, watch.Subscribe(request).Status);
        Assert.Equal(AdmissionStatus.Duplicate, watch.Subscribe(request).Status);
        Assert.Equal(1, watch.ActiveCount);
        Assert.True(watch.TryDequeueEvent(out var initial));
        Assert.Equal("added", initial!.Change);
        Assert.Equal("target", initial.Service!.ServiceId);
        Assert.True(watch.TryDequeueEvent(out var snapshotEnd));
        Assert.Equal("snapshot_end", snapshotEnd!.Change);

        var updated = world.ServiceMap["target"] with { Revision = 2 };
        world.ServiceMap["target"] = updated;
        directory.Upsert(updated);
        watch.Observe(Service("target", "target-node", "test.target", "alpha"), updated);
        Assert.True(watch.TryDequeueEvent(out var update));
        Assert.Equal("updated", update!.Change);

        var renamed = updated with { Revision = 3, Tag = "beta" };
        world.ServiceMap["target"] = renamed;
        directory.Upsert(renamed);
        watch.Observe(updated, renamed);
        Assert.True(watch.TryDequeueEvent(out var removed));
        Assert.Equal("removed", removed!.Change);
        Assert.Equal("alpha", removed.Tag);
        Assert.False(watch.TryDequeueEvent(out _));
        Assert.Equal("cancelled", watch.Unsubscribe(request.Key).Code);
        Assert.Equal("duplicate", watch.Unsubscribe(request.Key).Code);
        Assert.Equal(0, watch.ActiveCount);
    }

    [Fact]
    public void OverflowRequiresResubscriptionInsteadOfSilentlyDroppingUpdates()
    {
        var world = Fixture();
        var directory = new ServiceDirectory();
        foreach (var service in world.Services.Values) directory.Upsert(service);
        using var watch = new ServiceWatchBroker(world, directory, "binding",
            new(MaxSubscriptions: 2, MaxEvents: 2, MaxInitialCandidates: 2));
        var request = new ServiceWatchRequest(new("watch", 1), new("source", 1), "test.target");
        Assert.Equal(AdmissionStatus.Accepted, watch.Subscribe(request).Status);
        var before = world.ServiceMap["target"];
        var next = before with { Revision = 2 };
        world.ServiceMap["target"] = next;
        directory.Upsert(next);
        watch.Observe(before, next);
        Assert.True(watch.TryDequeueEvent(out var overflow));
        Assert.Equal("resync_required", overflow!.Change);
        Assert.False(watch.TryDequeueEvent(out _));
        Assert.Equal(0, watch.ActiveCount);
    }

    [Fact]
    public void SourceLossRevokesWatchBeforeDeliveringItsEvents()
    {
        var world = Fixture();
        var directory = new ServiceDirectory();
        foreach (var service in world.Services.Values) directory.Upsert(service);
        using var broker = new ServiceWatchBroker(world, directory, "binding");
        var request = new ServiceWatchRequest(new("watch", 1), new("source", 1), "test.target");
        Assert.Equal(AdmissionStatus.Accepted, broker.Subscribe(request).Status);
        world.ServiceMap.Remove("source");
        directory.Remove(new("source", 1));
        Assert.True(broker.TryDequeueEvent(out var notice));
        Assert.Equal("source_unavailable", notice!.Change);
        Assert.Equal(0, broker.ActiveCount);
    }

    [Fact]
    public void WatchCommandsRoundTripThroughStrictWireCodec()
    {
        var epoch = new Epochs("world", "engine", "binding");
        var watched = new RuntimeInput(epoch, 1, 0,
            new WatchServices(new(new("watch", 1), new("source", 1), "test.target", "alpha")));
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(watched, 0)));
        Assert.Equal(watched.Command, packet.Input.Command);
        var unwatch = new RuntimeInput(epoch, 2, 0, new UnwatchServices(new("watch", 1)));
        var cancel = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(unwatch, 0)));
        Assert.Equal(unwatch.Command, cancel.Input.Command);
    }
}
