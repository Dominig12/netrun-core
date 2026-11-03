using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Runtime;
using Netrun.Core.Scheduling;
using Netrun.Core.Storage;
using Netrun.Core.Transport;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Production Runtime/Protocol FIFO и bounded scalar outputs файлов без второго DM storage owner.</summary>
public sealed class StorageRuntimeTests
{
    private static readonly Epochs Epochs = new("storage-world", "storage-engine", "storage-binding");
    private static readonly ActorKey Actor = new("runner", 1);
    private static readonly NodeKey Node = new("node", 1);
    private static readonly StoreKey Local = new("local", 1), Shard = new("shard", 1);
    private static RuntimeInput Input(ulong sequence, RuntimeCommand command, int time = 0) => new(Epochs, sequence, time, command);
    private static WorldChange[] Facts() => [new SectorUpsert(new("s", 1, 1, new([1]))),
        new NodeUpsert(new("node", 1, 1, new("node", "apc", new(1, 1, 1), "s", true, new("home", "home"))))];
    private static ulong Publish(NetworkRuntime runtime)
    {
        var facts = Facts();
        var chunk = new SnapshotChunk("snapshot", 1, facts);
        runtime.Apply(Input(1, new ApplyWorld([new SnapshotBegin("snapshot", 0, 2)])));
        runtime.Apply(Input(2, new ApplyWorld([chunk])));
        runtime.Apply(Input(3, new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)facts.Length, SnapshotChecksum.Compute([chunk], 2))])));
        runtime.Apply(Input(4, new ApplyWorld([new CatchUpComplete("snapshot", 0)])));
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        return 5;
    }
    private static TransportEvent[] Drain(NetworkRuntime runtime)
    {
        var events = new List<TransportEvent>();
        while (runtime.TryDequeueEvent(out var output)) events.Add(output!);
        return events.ToArray();
    }
    private static SubmitStorageOperation Operation(ulong serial, StorageAction action) => new(new(new("files", serial), Actor, action));

    [Fact]
    public void StorageAndActorIntentsShareTheSameFifoAndOnlyMutateAfterPump()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        RuntimeCommand[] commands = [
            new SubmitEntityOperation(new(new("actors", 1), Actor, null,
                new CreateEntity(new(Actor, EntityKind.Resident, Node, 100, new("Runner"), "owner")))),
            new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor)),
            new RegisterStorage(new("bootstrap", 2), new(Shard, 10, StorageMediumKind.Shard, Actor)),
            new SeedStorageFile(new("bootstrap", 3), Local, new("seed.txt", Content: "seed", Size: 2)),
            Operation(1, new CreateStorageFile(Local, new("created.txt", Content: "created"))),
            new SubmitEntityOperation(new(new("actors", 2), Actor, null, new DamageEntity(20)))
        ];
        foreach (var command in commands)
            Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, command)).Admission!.Status);
        Assert.Null(runtime.ReadStorage(Local));
        Assert.Null(runtime.ReadEntity(Actor));
        Assert.Equal(6, runtime.PendingTaskCount);
        runtime.Pump(1000);
        Assert.Equal(80, runtime.ReadEntity(Actor)!.Integrity);
        Assert.Equal(3, runtime.ReadStorage(Local)!.Used);
        var outcomes = Drain(runtime).OfType<CoreTaskCompleted>().ToArray();
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5, 6 }, outcomes.Select(outcome => outcome.TaskSequence));
        Assert.All(outcomes, outcome => Assert.True(outcome.Success));
        var original = runtime.ReadStorage(Local)!.Files.Single(file => file.Name == "seed.txt").File;
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, Operation(2,
            new TransferStorageFile(Local, Shard, original, Move: true)))).Admission!.Status);
        Assert.Equal(2, runtime.ReadStorage(Local)!.Files.Count);
        runtime.Pump(1000);
        Assert.Single(runtime.ReadStorage(Local)!.Files);
        Assert.Equal(original, Assert.Single(runtime.ReadStorage(Shard)!.Files).File);
    }

    [Fact]
    public void OutputPressurePausesContinuationWithoutRepeatingStorageMutation()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Tasks: new(MaximumEvents: 1), Storage: new(MaxEvents: 2)));
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 2), Local, new("one.txt"))));
        var events = new List<TransportEvent>();
        for (var attempt = 0; attempt < 1000 && (runtime.PendingTaskCount > 0 || runtime.EventCount > 0); attempt++)
        {
            runtime.Pump(1);
            events.AddRange(Drain(runtime));
        }
        Assert.Equal(0, runtime.PendingTaskCount);
        Assert.Equal(1, runtime.StorageFileCount);
        Assert.Single(runtime.ReadStorage(Local)!.Files);
        Assert.Equal(2UL, runtime.ReadStorage(Local)!.Revision);
        Assert.Equal(2, events.OfType<StorageOperationCompleted>().Count());
        Assert.Equal(2, events.OfType<StorageChangedProjection>().Count());
        Assert.Equal(new ulong[] { 1, 2 }, events.OfType<CoreTaskCompleted>().Select(outcome => outcome.TaskSequence));
    }

    [Fact]
    public void ReplayedLogicalRequestDoesNotCreateAnotherFile()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor))));
        var create = Operation(1, new CreateStorageFile(Local, new("one.txt")));
        runtime.Apply(Input(sequence++, create));
        runtime.Pump(1000); Drain(runtime);
        Assert.Equal(AdmissionStatus.Duplicate, runtime.Apply(Input(sequence++, create)).Admission!.Status);
        runtime.Pump(1000);
        Assert.Single(runtime.ReadStorage(Local)!.Files);
        Assert.DoesNotContain(Drain(runtime), output => output is StorageOperationCompleted);
        var conflict = Operation(1, new CreateStorageFile(Local, new("different.txt")));
        Assert.Equal(AdmissionStatus.Rejected, runtime.Apply(Input(sequence, conflict)).Admission!.Status);
        Assert.Single(runtime.ReadStorage(Local)!.Files);
    }

    [Fact]
    public void TrustedRemovalRevokesExactStoreGenerationAndFileOwnership()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 2), Local, new("owned.txt"))));
        runtime.Pump(1000); Drain(runtime);
        runtime.Apply(Input(sequence++, new RemoveStorage(new("bootstrap", 3), Local)));
        Assert.NotNull(runtime.ReadStorage(Local));
        runtime.Pump(1000);
        Assert.Null(runtime.ReadStorage(Local));
        Assert.Equal(0, runtime.StorageFileCount);
        Assert.Contains(Drain(runtime), output => output is StorageChangedProjection { Change.Removed: true });
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 4), new(Local, 10, Owner: Actor))));
        runtime.Pump(1000);
        Assert.Null(runtime.ReadStorage(Local));
        var replacement = Local with { Generation = 2 };
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 5), new(replacement, 10, Owner: Actor))));
        runtime.Pump(1000);
        Assert.NotNull(runtime.ReadStorage(replacement));
        Assert.Empty(runtime.ReadStorage(replacement)!.Files);
    }

    [Fact]
    public void WireRoundTripsAllClosedActionsAndMetadataWithCanonicalGenerations()
    {
        var metadata = NetworkValue.FromObject(new Dictionary<string, NetworkValue>
        {
            ["persona"] = NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["alias"] = NetworkValue.FromText("Агент") }),
            ["empty"] = NetworkValue.FromArray([]),
            ["none"] = NetworkValue.Null
        });
        var file = new FileKey("file", ulong.MaxValue);
        RuntimeCommand[] commands = [
            new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor)),
            new RemoveStorage(new("bootstrap", 2), Local),
            new SeedStorageFile(new("bootstrap", 3), Local, new("agent.bot", "bot", "scout", 2, Metadata: metadata,
                Children: new([new("patch.exe", "program", "patch", 2)]))),
            Operation(1, new CreateStorageFile(Local, new("text.txt", Content: "текст"))),
            Operation(2, new TransferStorageFile(Local, Shard, file, true)),
            Operation(3, new DeleteStorageFile(Local, file)),
            Operation(4, new EditStorageText(Local, file, "edited")),
            Operation(5, new RenameStorageFile(Local, file, "new.txt")),
            Operation(6, new PackAgentMemory(Local, Shard, file, metadata)),
            Operation(7, new RestoreAgentMemory(Local, file, Shard)),
            Operation(8, new OfferStorageFile(Local, Shard, file, "Sender")),
            Operation(9, new RespondStorageOffer(Shard, "offer-1", true)),
            Operation(10, new ReadStorage(Local))
        ];
        foreach (var command in commands)
        {
            var encoded = RuntimeJson.Encode(Input(1, command));
            var decoded = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(encoded));
            // Nullable metadata и явный NetworkValue.Null имеют одну canonical wire-семантику.
            Assert.Equal(encoded, RuntimeJson.Encode(decoded.Input));
            if (command is SubmitStorageOperation before && decoded.Input.Command is SubmitStorageOperation after)
                Assert.Equal(StorageWorld.FingerprintOperation(before.Operation), StorageWorld.FingerprintOperation(after.Operation));
        }
        var text = Encoding.UTF8.GetString(RuntimeJson.Encode(Input(1, Operation(1, new DeleteStorageFile(Local, file)))));
        Assert.Contains("\"generation\":\"18446744073709551615\"", text);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(text.Replace(
            "\"generation\":\"18446744073709551615\"", "\"generation\":18446744073709551615", StringComparison.Ordinal))));
    }

    [Fact]
    public void StorageCodecRejectsMissingDuplicateUnknownAndGrantInjectionFields()
    {
        var text = Encoding.UTF8.GetString(RuntimeJson.Encode(Input(1, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor)))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(text.Replace("\"capacity\":10,", "", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(text.Replace("\"capacity\":10", "\"capacity\":10,\"capacity\":20", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(text.Replace("\"capacity\":10", "\"capacity\":10,\"proc\":\"execute\"", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(text.Replace("storage_register", "storage_grant", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(Input(1, new RegisterStorage(new("bootstrap", 1), new(Local, -1)))));
    }

    [Fact]
    public async Task LargeSeedPayloadProducesOnlyScalarHostReplies()
    {
        using var session = new RuntimeHostSession(Epochs);
        ulong sequence = 0, ack = 0;
        async Task<byte[]> Send(RuntimeCommand command)
        {
            var bytes = await session.ProcessAsync(RuntimeJson.Encode(Input(++sequence, command), ack), CancellationToken.None);
            using var output = JsonDocument.Parse(bytes);
            ack = ulong.Parse(output.RootElement.GetProperty("output_sequence").GetString()!, CultureInfo.InvariantCulture);
            return bytes;
        }
        var facts = Facts(); var chunk = new SnapshotChunk("snapshot", 1, facts);
        await Send(new ApplyWorld([new SnapshotBegin("snapshot", 0, 2)]));
        await Send(new ApplyWorld([chunk]));
        await Send(new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)facts.Length, SnapshotChecksum.Compute([chunk], 2))]));
        await Send(new ApplyWorld([new CatchUpComplete("snapshot", 0)]));
        await Send(new RegisterStorage(new("bootstrap", 1), new(Local, 100, Owner: Actor)));
        var payload = "sensitive-file-payload-marker" + new string('x', 60000);
        var children = new ValueList<FileDefinition>(Enumerable.Range(0, 8).Select(index => new FileDefinition("part-" + index + ".txt", Content: payload)));
        var reply = await Send(new SeedStorageFile(new("bootstrap", 2), Local, new("agent.bot", "bot", "scout", 2, Children: children)));
        var text = Encoding.UTF8.GetString(reply);
        Assert.True(reply.Length < 8192);
        Assert.DoesNotContain("sensitive-file-payload-marker", text);
        Assert.DoesNotContain("children", text);
        Assert.Contains("storage_completed", text);
        Assert.Contains("storage_changed", text);
    }

    [Fact]
    public void OfferExpiryBecomesAnotherTaskAtTheTailOfTheCommonFifo()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 2), new(Shard, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 3), Local, new("offer.txt"))));
        runtime.Pump(1000); Drain(runtime);
        var file = Assert.Single(runtime.ReadStorage(Local)!.Files).File;
        runtime.Apply(Input(sequence++, Operation(1, new OfferStorageFile(Local, Shard, file, "Sender"))));
        runtime.Pump(1000); Drain(runtime);
        Assert.Equal(2, runtime.StorageFileCount);
        runtime.Apply(Input(sequence++, Operation(2, new ReadStorage(Local)), time: 1200));
        runtime.Pump(1000);
        var tasks = Drain(runtime).OfType<CoreTaskCompleted>().ToArray();
        Assert.Equal("files", tasks[0].Key.ProducerId);
        Assert.Equal("storage.expiry", tasks[1].Key.ProducerId);
        Assert.True(tasks[0].TaskSequence < tasks[1].TaskSequence);
        Assert.Equal(1, runtime.StorageFileCount);
    }

    [Fact]
    public void HardFileCapacityRejectsHeadSoFollowingDeleteCanFreeSpace()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Storage: new(MaxFiles: 1)));
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 2), Local, new("old.txt"))));
        runtime.Pump(1000); Drain(runtime);
        var old = Assert.Single(runtime.ReadStorage(Local)!.Files).File;
        runtime.Apply(Input(sequence++, Operation(1, new CreateStorageFile(Local, new("too-early.txt")))));
        runtime.Apply(Input(sequence++, Operation(2, new DeleteStorageFile(Local, old))));
        runtime.Apply(Input(sequence++, Operation(3, new CreateStorageFile(Local, new("after-delete.txt")))));
        runtime.Pump(1000);
        Assert.Equal(0, runtime.PendingTaskCount);
        Assert.Equal("after-delete.txt", Assert.Single(runtime.ReadStorage(Local)!.Files).Name);
        var outcomes = Drain(runtime).OfType<CoreTaskCompleted>().ToArray();
        Assert.False(outcomes[0].Success);
        Assert.Equal("storage_capacity", outcomes[0].Code);
        Assert.True(outcomes[1].Success);
        Assert.True(outcomes[2].Success);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("pack")]
    [InlineData("restore")]
    public void ImpossibleAtomicEventReservationRejectsHeadAndLetsFollowingMutationsRun(string action)
    {
        using var runtime = new NetworkRuntime(Epochs, new(Storage: new(MaxEvents: 1)));
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 2), new(Shard, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 3), Local,
            new("agent.bot", "bot", "scout", 2, Children: action == "restore" ? new([new("nested.txt")]) : null))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 4), Shard, new("old.txt"))));
        runtime.Pump(1000); Drain(runtime);
        var source = runtime.ReadStorage(Local)!;
        var package = Assert.Single(source.Files).File;
        var old = Assert.Single(runtime.ReadStorage(Shard)!.Files).File;
        StorageAction blocked = action switch
        {
            "move" => new TransferStorageFile(Local, Shard, package, Move: true),
            "pack" => new PackAgentMemory(Shard, Local, package, NetworkValue.Null),
            "restore" => new RestoreAgentMemory(Local, package, Shard),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        runtime.Apply(Input(sequence++, Operation(1, blocked)));
        runtime.Apply(Input(sequence++, Operation(2, new DeleteStorageFile(Shard, old))));
        runtime.Apply(Input(sequence++, Operation(3, new CreateStorageFile(Shard, new("after-delete.txt")))));
        runtime.Pump(1000);
        Assert.Equal(0, runtime.PendingTaskCount);
        Assert.Equal(source, runtime.ReadStorage(Local));
        Assert.Equal("after-delete.txt", Assert.Single(runtime.ReadStorage(Shard)!.Files).Name);
        var outcomes = Drain(runtime).OfType<CoreTaskCompleted>().ToArray();
        Assert.Equal(3, outcomes.Length);
        Assert.False(outcomes[0].Success);
        Assert.Equal("storage_event_capacity", outcomes[0].Code);
        Assert.True(outcomes[1].Success);
        Assert.True(outcomes[2].Success);
    }

    [Fact]
    public void TrustedReadOnlyStoreRemovalReleasesOffersAndPublishesOneTombstoneWithinOneSlot()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Storage: new(MaxEvents: 1)));
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 1), new(Local, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 2), new(Shard, 10, Owner: Actor))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 3), Local, new("sender.txt"))));
        runtime.Apply(Input(sequence++, new SeedStorageFile(new("bootstrap", 4), Shard, new("owned.txt"))));
        runtime.Pump(1000); Drain(runtime);
        var source = Assert.Single(runtime.ReadStorage(Local)!.Files).File;
        runtime.Apply(Input(sequence++, Operation(1, new OfferStorageFile(Local, Shard, source, "Sender"))));
        runtime.Pump(1000); Drain(runtime);
        Assert.Equal(3, runtime.StorageFileCount);
        runtime.Apply(Input(sequence++, new RegisterStorage(new("bootstrap", 5), new(Shard, 0, Owner: Actor))));
        runtime.Pump(1000); Drain(runtime);
        runtime.Apply(Input(sequence++, new RemoveStorage(new("bootstrap", 6), Shard)));
        runtime.Apply(Input(sequence++, Operation(2, new ReadStorage(Local))));
        runtime.Pump(1000);
        Assert.Equal(0, runtime.PendingTaskCount);
        Assert.Null(runtime.ReadStorage(Shard));
        Assert.Equal(1, runtime.StorageFileCount);
        var events = Drain(runtime);
        var change = Assert.Single(events.OfType<StorageChangedProjection>());
        Assert.True(change.Change.Removed);
        Assert.Equal(Shard, change.Change.Store);
        Assert.All(events.OfType<CoreTaskCompleted>(), result => Assert.True(result.Success));
    }
}
