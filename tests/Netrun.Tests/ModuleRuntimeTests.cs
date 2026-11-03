using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Plugins;
using Netrun.Core.Runtime;
using Netrun.Core.Scheduling;
using Netrun.Core.Transport;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет настоящий mod task в общей очереди, typed effects и session isolation.</summary>
public sealed class ModuleRuntimeTests
{
    private static readonly Epochs Epochs = new("world", "engine", "binding");
    private static readonly ActorKey Actor = new("actor", 1);
    private sealed class Plugin : INetrunPlugin
    {
        public PluginMetadata Metadata => new("test.mods", "Mods", "1");
        public void Configure(INetrunPluginRegistry registry) => registry.RegisterModule(new Factory());
    }
    private sealed class Factory : INetrunModuleFactory
    {
        public string Id => "test.counter";
        public IModuleSession Create(ModuleContext context) => new Session(context);
    }
    private sealed class Session(ModuleContext context) : IModuleSession
    {
        private int _counter;
        public IModuleTask Start(ModuleRequest request) => new Work(request, context, ++_counter);
        public void Dispose() { }
    }
    private sealed class Work(ModuleRequest request, ModuleContext context, int counter) : IModuleTask
    {
        private int _remaining = 3;
        public ModuleStep Advance(int workBudget)
        {
            var consumed = Math.Min(_remaining, workBudget);
            _remaining -= consumed;
            if (request.Operation == "bad") return new(workBudget + 1, true);
            if (_remaining > 0) return new(consumed, false);
            var effects = context.ReadEntity(Actor) is null ? null :
                new ValueList<EntityOperation>([new(new("test.counter.effects", (ulong)counter), Actor, null, new DamageEntity(5))]);
            return new(consumed, true, NetworkValue.FromNumber(counter), effects,
                new([new ModuleProjection("counter", NetworkValue.FromNumber(counter))]));
        }
        public void Dispose() { }
    }

    private static RuntimeInput Input(ulong sequence, RuntimeCommand command) => new(Epochs, sequence, 0, command);
    private static void Publish(NetworkRuntime runtime)
    {
        WorldChange[] facts = [new NodeUpsert(new("n", 1, 1, new("n", "server", new(1, 1, 1), "s", true)))];
        var chunk = new SnapshotChunk("s", 1, facts);
        runtime.Apply(Input(1, new ApplyWorld([new SnapshotBegin("s", 0, 2)])));
        runtime.Apply(Input(2, new ApplyWorld([chunk])));
        runtime.Apply(Input(3, new ApplyWorld([new SnapshotCommit("s", 1, 1, SnapshotChecksum.Compute([chunk], 2))])));
        runtime.Apply(Input(4, new ApplyWorld([new CatchUpComplete("s", 0)])));
    }

    [Fact]
    public void ModuleEffectsRunBeforeFollowingFifoTaskAndDomainReplayDoesNotRepeatThem()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Plugins: [new Plugin()]));
        Publish(runtime);
        runtime.Apply(Input(5, new SubmitEntityOperation(new(new("actors", 1), Actor, null,
            new CreateEntity(new(Actor, EntityKind.Resident, new("n", 1), 100, new("Runner"), "owner"))))));
        var module = new SubmitModuleRequest(new(new("mods", 1), "test.counter", "increment", NetworkValue.Null));
        runtime.Apply(Input(6, module));
        runtime.Apply(Input(7, new SubmitEntityOperation(new(new("actors", 2), Actor, null, new DamageEntity(10)))));
        Assert.Null(runtime.ReadEntity(Actor));
        runtime.Pump(100);
        Assert.Equal(85, runtime.ReadEntity(Actor)!.Integrity);
        var events = new List<TransportEvent>();
        while (runtime.TryDequeueEvent(out var item)) events.Add(item!);
        Assert.Equal(new ulong[] { 1, 2, 3 }, events.OfType<CoreTaskCompleted>().Select(item => item.TaskSequence));
        Assert.Equal(new double[] { 100, 95, 85 }, events.OfType<EntityChanged>().Select(item => item.View!.Integrity));
        Assert.Equal(NetworkValue.FromNumber(1), Assert.Single(events.OfType<ModuleCompleted>()).Result);
        Assert.Equal(AdmissionStatus.Duplicate, runtime.Apply(Input(8, module)).Admission!.Status);
        runtime.Pump(100);
        Assert.Equal(85, runtime.ReadEntity(Actor)!.Integrity);
    }

    [Fact]
    public void ModSessionStateIsIsolatedForEachRuntimeBinding()
    {
        var plugin = new Plugin();
        using var first = new NetworkRuntime(Epochs, new(Plugins: [plugin]));
        using var second = new NetworkRuntime(Epochs, new(Plugins: [plugin]));
        foreach (var runtime in new[] { first, second })
        {
            Publish(runtime);
            runtime.Apply(Input(5, new SubmitModuleRequest(new(new("mods", 1), "test.counter", "increment", NetworkValue.Null))));
            runtime.Pump(100);
            ModuleCompleted? result = null;
            while (runtime.TryDequeueEvent(out var output)) if (output is ModuleCompleted item) result = item;
            Assert.Equal(NetworkValue.FromNumber(1), result!.Result);
        }
    }

    [Fact]
    public void BrokenModProducesSafeFailureAndDoesNotStopFollowingTask()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Plugins: [new Plugin()]));
        Publish(runtime);
        runtime.Apply(Input(5, new SubmitModuleRequest(new(new("mods", 1), "test.counter", "bad", NetworkValue.Null))));
        runtime.Apply(Input(6, new SubmitModuleRequest(new(new("mods", 2), "test.counter", "increment", NetworkValue.Null))));
        runtime.Pump(100);
        var results = new List<ModuleCompleted>();
        while (runtime.TryDequeueEvent(out var output)) if (output is ModuleCompleted result) results.Add(result);
        Assert.False(results[0].Success);
        Assert.Equal("plugin_module_step_invalid", results[0].Code);
        Assert.True(results[1].Success);
        Assert.Equal(0, runtime.PendingTaskCount);
    }

    [Fact]
    public void ModuleWireRoundTripPreservesPayloadAndRejectsUnknownOperationEnvelopeFields()
    {
        var command = new SubmitModuleRequest(new(new("mods", 1), "test.counter", "increment", NetworkValue.FromText("state")));
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(Input(1, command))));
        Assert.Equal(command, packet.Input.Command);
    }
}
