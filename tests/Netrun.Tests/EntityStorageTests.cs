using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Storage;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Паритет файловой ёмкости, ownership/capabilities и атомарных переносов с file_store/storage/bots.dm.</summary>
public sealed class EntityStorageTests
{
    private static readonly ActorKey Owner = new("owner", 1), Recipient = new("recipient", 1), Stranger = new("stranger", 1);
    private static readonly StoreKey Local = new("local", 1), Shard = new("shard", 1), Memory = new("memory", 1);

    private sealed class Scene
    {
        internal readonly StorageWorld Core;
        internal ulong Sequence;
        internal long Now;
        internal Scene(int local = 64, int shard = 64, int memory = 64, StorageWorldOptions? options = null)
        {
            Core = new("storage-test", options);
            Assert.True(Core.RegisterStore(new(Local, local, StorageMediumKind.Local, Owner)));
            Assert.True(Core.RegisterStore(new(Shard, shard, StorageMediumKind.Shard, Recipient)));
            Drain();
            Assert.True(Core.RegisterStore(new(Memory, memory, StorageMediumKind.Local, Owner)));
            Core.Grant(Shard, Owner, StorageRights.All);
            Drain();
        }
        internal StorageResult Apply(StorageAction action, ActorKey? actor = null) => Core.Apply(new(new("storage", ++Sequence), actor ?? Owner, action), Now);
        internal FileKey Seed(StoreKey store, FileDefinition definition)
        {
            var result = Core.SeedFile(store, definition);
            Assert.True(result.Accepted, result.Code);
            return result.File!;
        }
        internal StorageChanged[] Drain()
        {
            var changes = new List<StorageChanged>();
            while (Core.TryDequeueEvent(out var change)) changes.Add(change!);
            return changes.ToArray();
        }
        internal void Capacity(StoreKey store, int capacity)
        {
            var previous = Core.ReadStore(store)!;
            Assert.True(Core.RegisterStore(new(store, capacity, previous.Kind, previous.Owner)));
        }
    }

    [Fact]
    public void TextCapacityUsesUnicodeCharactersAndExactDmScaling()
    {
        Assert.Equal(1, StorageWorld.TextSize(""));
        Assert.Equal(1, StorageWorld.TextSize(new string('x', 500)));
        Assert.Equal(2, StorageWorld.TextSize(new string('x', 501)));
        Assert.Equal(1, StorageWorld.TextSize(string.Concat(Enumerable.Repeat("😀", 500))));
        Assert.Equal(48 * 8, StorageWorld.DefaultNodeCapacity("ai"));
        Assert.Equal(32 * 8, StorageWorld.DefaultNodeCapacity("server"));
        Assert.Equal(24 * 8, StorageWorld.DefaultNodeCapacity("terminal"));
        Assert.Equal(16 * 8, StorageWorld.DefaultNodeCapacity("gateway"));
        Assert.Equal(12 * 8, StorageWorld.DefaultNodeCapacity("apc"));
        Assert.Equal(8 * 8, StorageWorld.DefaultNodeCapacity("unknown"));
        Assert.Equal("имя файла.txt", StorageWorld.NormalizeFileName("<b>имя</b>\tфайла?:.txt"));
        Assert.Equal("document.txt", StorageWorld.NormalizeFileName("?%*/\\"));
        Assert.Equal(64, StorageWorld.NormalizeFileName(new string('a', 80)).Length);
    }

    [Fact]
    public void CreateEditRenameAndOverflowPreserveActualDiskAccounting()
    {
        var scene = new Scene(local: 2);
        var created = scene.Apply(new CreateStorageFile(Local, new("<b>note</b>?.txt", Content: new string('x', 500))));
        Assert.True(created.Accepted);
        Assert.Equal("note.txt", scene.Core.ReadFile(created.File!)!.Name);
        Assert.Equal(1, scene.Core.ReadStore(Local)!.Used);
        Assert.True(scene.Apply(new EditStorageText(Local, created.File!, new string('y', 501))).Accepted);
        Assert.Equal(2, scene.Core.ReadStore(Local)!.Used);
        var before = scene.Core.ReadStore(Local)!;
        Assert.False(scene.Apply(new EditStorageText(Local, created.File!, new string('z', 1001))).Accepted);
        Assert.Equal(before, scene.Core.ReadStore(Local));
        Assert.True(scene.Apply(new RenameStorageFile(Local, created.File!, "<i>new</i>:name.txt")).Accepted);
        Assert.Equal("newname.txt", scene.Core.ReadFile(created.File!)!.Name);
        Assert.False(scene.Apply(new CreateStorageFile(Local, new("overflow.txt"))).Accepted);
        Assert.Equal(2, scene.Core.ReadStore(Local)!.Used);
    }

    [Fact]
    public void CopyAllocatesNewUidAndMoveRetainsOriginalUidAtomically()
    {
        var scene = new Scene();
        var original = scene.Seed(Local, new("tool.exe", "program", "strike", 2));
        var copy = scene.Apply(new TransferStorageFile(Local, Shard, original));
        Assert.True(copy.Accepted);
        Assert.NotEqual(original, copy.File);
        Assert.Equal(original, Assert.Single(scene.Core.ReadStore(Local)!.Files).File);
        Assert.Equal(copy.File, Assert.Single(scene.Core.ReadStore(Shard)!.Files).File);
        var moved = scene.Apply(new TransferStorageFile(Local, Memory, original, Move: true));
        Assert.True(moved.Accepted);
        Assert.Equal(original, moved.File);
        Assert.Empty(scene.Core.ReadStore(Local)!.Files);
        Assert.Equal(original, Assert.Single(scene.Core.ReadStore(Memory)!.Files).File);
        var source = scene.Core.ReadStore(Memory)!;
        var destination = scene.Core.ReadStore(Shard)!;
        scene.Capacity(Shard, 2);
        destination = scene.Core.ReadStore(Shard)!;
        Assert.False(scene.Apply(new TransferStorageFile(Memory, Shard, original, Move: true)).Accepted);
        Assert.Equal(source, scene.Core.ReadStore(Memory));
        Assert.Equal(destination, scene.Core.ReadStore(Shard));
    }

    [Fact]
    public void SameDiskCopyWorksButMoveDoesNotChangeEitherRevision()
    {
        var scene = new Scene(local: 4);
        var original = scene.Seed(Local, new("agent.bot", "bot", "scout", 2));
        var copied = scene.Apply(new TransferStorageFile(Local, Local, original));
        Assert.True(copied.Accepted);
        Assert.NotEqual(original, copied.File);
        Assert.Equal(4, scene.Core.ReadStore(Local)!.Used);
        var before = scene.Core.ReadStore(Local);
        Assert.Equal("same_storage", scene.Apply(new TransferStorageFile(Local, Local, original, Move: true)).Code);
        Assert.Equal(before, scene.Core.ReadStore(Local));
    }

    [Fact]
    public void FailedCapacityMoveDoesNotRemoveOrEvictOriginalFiles()
    {
        var scene = new Scene(shard: 1);
        var original = scene.Seed(Local, new("cargo.txt", Content: "cargo", Size: 2));
        var resident = scene.Seed(Shard, new("keep.txt"));
        var source = scene.Core.ReadStore(Local)!;
        var target = scene.Core.ReadStore(Shard)!;
        Assert.False(scene.Apply(new TransferStorageFile(Local, Shard, original, Move: true)).Accepted);
        Assert.Equal(source, scene.Core.ReadStore(Local));
        Assert.Equal(target, scene.Core.ReadStore(Shard));
        Assert.NotNull(scene.Core.ReadFile(resident));
        Assert.NotNull(scene.Core.ReadFile(original));
    }

    [Fact]
    public void ReadOnlyMediaAllowCopiesAndFirmwareCopiesBecomeChargedText()
    {
        var scene = new Scene(local: 0, shard: 1);
        var manual = scene.Seed(Local, new("readme.txt", Content: new string('m', 501), Size: 0, FirmwareManual: true));
        Assert.Equal(0, scene.Core.ReadStore(Local)!.Used);
        Assert.Equal(2, scene.Core.ReadFile(manual)!.CopySize);
        var before = scene.Core.ReadStore(Local);
        Assert.False(scene.Apply(new TransferStorageFile(Local, Shard, manual)).Accepted);
        Assert.Equal(before, scene.Core.ReadStore(Local));
        Assert.False(scene.Apply(new TransferStorageFile(Local, Memory, manual, Move: true)).Accepted);
        Assert.False(scene.Apply(new DeleteStorageFile(Local, manual)).Accepted);
        scene.Capacity(Shard, 2);
        var copied = scene.Apply(new TransferStorageFile(Local, Shard, manual));
        Assert.True(copied.Accepted);
        var file = scene.Core.ReadFile(copied.File!)!;
        Assert.False(file.FirmwareManual);
        Assert.Equal(2, file.Size);
        Assert.Equal(2, scene.Core.ReadStore(Shard)!.Used);
        Assert.True(scene.Apply(new EditStorageText(Shard, file.File, "edited")).Accepted);
        Assert.Equal(new string('m', 501), scene.Core.ReadFile(manual)!.Content);
    }

    [Fact]
    public void MapPreloadMayOverfillButOrdinaryWritesNeverEvict()
    {
        var scene = new Scene(local: 1);
        var preload = scene.Seed(Local, new("mapped.txt", Size: 3));
        Assert.Equal(3, scene.Core.ReadStore(Local)!.Used);
        Assert.False(scene.Apply(new CreateStorageFile(Local, new("new.txt"))).Accepted);
        Assert.Equal(preload, Assert.Single(scene.Core.ReadStore(Local)!.Files).File);
        Assert.Equal(3, scene.Core.ReadStore(Local)!.Used);
    }

    [Fact]
    public void StoreAclAndFileCapabilitiesAreIndependentAndCannotBeForgedByUid()
    {
        var scene = new Scene();
        var file = scene.Seed(Local, new("restricted.access", "access", Capabilities: FileCapabilities.Copy | FileCapabilities.Store | FileCapabilities.Delete));
        Assert.False(scene.Apply(new ReadStorage(Local), Stranger).Accepted);
        Assert.False(scene.Apply(new DeleteStorageFile(Local, file), Stranger).Accepted);
        Assert.False(scene.Apply(new TransferStorageFile(Local, Shard, file)).Accepted);
        Assert.NotNull(scene.Core.ReadFile(file));
        Assert.False(scene.Apply(new CreateStorageFile(Local, new("forged.access", "access"))).Accepted);
        Assert.False(scene.Apply(new CreateStorageFile(Local, new("forged.exe", "program", "assault"))).Accepted);
        scene.Core.Grant(Local, Owner, StorageRights.CreateSpecial);
        Assert.True(scene.Apply(new CreateStorageFile(Local, new("trusted.exe", "program", "strike", 2))).Accepted);
        scene.Core.Grant(Local, Stranger, StorageRights.Read);
        Assert.True(scene.Apply(new ReadStorage(Local), Stranger).Accepted);
        Assert.False(scene.Apply(new DeleteStorageFile(Local, file), Stranger).Accepted);
    }

    [Fact]
    public void PrivateSessionSnapshotNeverCopiesOrTransfersAndDeletionRevokesIt()
    {
        var scene = new Scene();
        var sessionStore = new StoreKey("session", 1);
        Assert.True(scene.Core.RegisterStore(new(sessionStore, 0, StorageMediumKind.Session, Owner)));
        scene.Core.SetSessionLive("dive", true);
        var privateFile = scene.Seed(sessionStore, new("identity.access", "access", Capabilities: FileCapabilities.Store,
            BoundStore: sessionStore, SessionId: "dive"));
        Assert.True(scene.Core.CanUseFile(Owner, sessionStore, privateFile));
        Assert.False(scene.Apply(new TransferStorageFile(sessionStore, Local, privateFile)).Accepted);
        Assert.False(scene.Apply(new TransferStorageFile(sessionStore, Local, privateFile, Move: true)).Accepted);
        Assert.True(scene.Apply(new DeleteStorageFile(sessionStore, privateFile)).Accepted);
        Assert.Null(scene.Core.ReadFile(privateFile));
        privateFile = scene.Seed(sessionStore, new("identity.access", "access", Capabilities: FileCapabilities.Store,
            BoundStore: sessionStore, SessionId: "dive"));
        scene.Core.SetSessionLive("dive", false);
        Assert.False(scene.Core.CanUseFile(Owner, sessionStore, privateFile));
        Assert.False(scene.Core.SeedFile(Local, new("forged.access", "access", BoundStore: sessionStore, SessionId: "dive")).Accepted);
    }

    [Fact]
    public void AgentPackagesChargeNestedMemoryOnceAndCopiesOwnIndependentUidTrees()
    {
        var scene = new Scene();
        var metadata = NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["alias"] = NetworkValue.FromText("Package Agent") });
        var package = scene.Seed(Local, new("agent.bot", "bot", "scout", 4, Metadata: metadata, Children: new([
            new("strike.exe", "program", "strike", 2), new("cargo.txt", Content: "cargo", Size: 3)
        ])));
        var original = scene.Core.ReadFile(package)!;
        Assert.Equal(9, original.Size);
        Assert.Equal(9, scene.Core.ReadStore(Local)!.Used);
        Assert.Equal(3, scene.Core.FileCount);
        var copy = scene.Apply(new TransferStorageFile(Local, Shard, package));
        Assert.True(copy.Accepted);
        var cloned = scene.Core.ReadFile(copy.File!)!;
        Assert.Equal(9, cloned.Size);
        Assert.Equal(6, scene.Core.FileCount);
        Assert.True(cloned.Metadata.DeepEquals(original.Metadata));
        Assert.All(cloned.Children!, child => Assert.DoesNotContain(original.Children!, old => old.File == child.File));
        Assert.False(scene.Apply(new DeleteStorageFile(Local, original.Children![0].File)).Accepted);
        Assert.True(scene.Apply(new RenameStorageFile(Shard, copy.File!, "copy.bot")).Accepted);
        Assert.Equal("agent.bot", scene.Core.ReadFile(package)!.Name);
    }

    [Fact]
    public void NestedFirmwareIsChargedBeforeDeepCopyMutatesAnyStore()
    {
        var scene = new Scene(shard: 3);
        var package = scene.Seed(Local, new("agent.bot", "bot", "scout", 2, Children: new([
            new("manual.txt", Content: new string('x', 501), Size: 0, FirmwareManual: true)
        ])));
        Assert.Equal(2, scene.Core.ReadFile(package)!.Size);
        Assert.Equal(4, scene.Core.ReadFile(package)!.CopySize);
        var before = scene.Core.FileCount;
        Assert.False(scene.Apply(new TransferStorageFile(Local, Shard, package)).Accepted);
        Assert.Equal(before, scene.Core.FileCount);
        Assert.Empty(scene.Core.ReadStore(Shard)!.Files);
        scene.Capacity(Shard, 4);
        var copied = scene.Apply(new TransferStorageFile(Local, Shard, package));
        Assert.True(copied.Accepted);
        Assert.Equal(4, scene.Core.ReadFile(copied.File!)!.Size);
        Assert.Equal(2, Assert.Single(scene.Core.ReadFile(copied.File!)!.Children!).Size);
    }

    [Fact]
    public void RestoreValidatesWholeLoadoutBeforeEvictionAndPreservesNestedUids()
    {
        var scene = new Scene(memory: 3);
        var loadout = scene.Seed(Memory, new("keep.exe", "program", "patch", 2));
        var package = scene.Seed(Local, new("agent.bot", "bot", "scout", 4, Children: new([
            new("strike.exe", "program", "strike", 2), new("cargo.txt", Content: "cargo", Size: 3)
        ])));
        var before = scene.Core.ReadFile(package)!;
        var memory = scene.Core.ReadStore(Memory)!;
        Assert.False(scene.Apply(new RestoreAgentMemory(Local, package, Memory)).Accepted);
        Assert.Equal(memory, scene.Core.ReadStore(Memory));
        Assert.NotNull(scene.Core.ReadFile(loadout));
        Assert.Equal(before, scene.Core.ReadFile(package));
        scene.Capacity(Memory, 5);
        Assert.True(scene.Apply(new RestoreAgentMemory(Local, package, Memory)).Accepted);
        Assert.Null(scene.Core.ReadFile(loadout));
        Assert.Equal(before.Children!.Select(child => child.File), scene.Core.ReadStore(Memory)!.Files.Select(file => file.File));
        Assert.Equal(5, scene.Core.ReadStore(Memory)!.Used);
        Assert.Equal(4, scene.Core.ReadFile(package)!.Size);
        Assert.Null(scene.Core.ReadFile(package)!.Children);
    }

    [Fact]
    public void ExplicitEmptyAgentMemoryReplacesLoadoutButNullPreservesDefaults()
    {
        var scene = new Scene();
        var loadout = scene.Seed(Memory, new("keep.exe", "program", "patch", 2));
        var ordinary = scene.Seed(Local, new("default.bot", "bot", "scout", 2));
        Assert.True(scene.Apply(new RestoreAgentMemory(Local, ordinary, Memory)).Accepted);
        Assert.NotNull(scene.Core.ReadFile(loadout));
        var empty = scene.Seed(Local, new("empty.bot", "bot", "scout", 2, Children: new([])));
        Assert.True(scene.Apply(new RestoreAgentMemory(Local, empty, Memory)).Accepted);
        Assert.Empty(scene.Core.ReadStore(Memory)!.Files);
        Assert.Null(scene.Core.ReadFile(loadout));
    }

    [Fact]
    public void PackingAndRecallingMemoryAreAtomicAndKeepOriginalCargoUids()
    {
        var scene = new Scene(local: 5);
        var package = scene.Seed(Local, new("agent.bot", "bot", "scout", 4));
        var cargo = scene.Seed(Memory, new("cargo.txt", Content: "cargo", Size: 2));
        var metadata = NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["name"] = NetworkValue.FromText("Saved Agent") });
        var before = scene.Core.ReadStore(Memory)!;
        Assert.False(scene.Apply(new PackAgentMemory(Memory, Local, package, metadata)).Accepted);
        Assert.Equal(before, scene.Core.ReadStore(Memory));
        Assert.Equal(4, scene.Core.ReadFile(package)!.Size);
        scene.Capacity(Local, 6);
        Assert.True(scene.Apply(new PackAgentMemory(Memory, Local, package, metadata)).Accepted);
        Assert.Empty(scene.Core.ReadStore(Memory)!.Files);
        Assert.Equal(cargo, Assert.Single(scene.Core.ReadFile(package)!.Children!).File);
        Assert.Equal(6, scene.Core.ReadStore(Local)!.Used);
        Assert.True(scene.Core.ReadFile(package)!.Metadata.DeepEquals(metadata));
        Assert.True(scene.Apply(new RestoreAgentMemory(Local, package, Memory)).Accepted);
        Assert.Equal(cargo, Assert.Single(scene.Core.ReadStore(Memory)!.Files).File);
        Assert.Equal(4, scene.Core.ReadStore(Local)!.Used);
    }

    [Fact]
    public void AcquiredGrantsMergePerMediumWithoutChangingCanonicalUidOrCurrentStamps()
    {
        var scene = new Scene(shard: 1);
        scene.Core.UpdateScope(new("node:n", 1, "home", 1));
        var incoming = scene.Seed(Local, new("strong.grants", "grants", Grants: new([new("node:n", 1, "home", 1, 3)])));
        var survivor = scene.Seed(Shard, new("weak.grants", "grants", Grants: new([new("node:n", 1, "home", 1, 1)])));
        scene.Drain();
        var moved = scene.Apply(new TransferStorageFile(Local, Shard, incoming, Move: true));
        Assert.True(moved.Accepted);
        Assert.Equal(survivor, moved.File);
        Assert.Equal(1, scene.Core.ReadStore(Shard)!.Used);
        Assert.Null(scene.Core.ReadFile(incoming));
        Assert.Equal(3, Assert.Single(scene.Core.ReadCurrentGrants(Recipient, Shard, survivor)).Level);
        Assert.Contains(scene.Drain(), change => change.Replacements.Any(replacement => replacement.Previous == incoming && replacement.Current == survivor));
        scene.Core.UpdateScope(new("node:n", 1, "home", 2));
        Assert.Empty(scene.Core.ReadCurrentGrants(Recipient, Shard, survivor));
        Assert.Equal(1UL, Assert.Single(scene.Core.ReadFile(survivor)!.Grants).Epoch);
    }

    [Fact]
    public void StaleGrantCopyCannotRebindOldAuthorityToCurrentNodeOrNetwork()
    {
        var scene = new Scene(shard: 1);
        scene.Core.UpdateScope(new("node:n", 2, "new-domain", 5));
        var old = scene.Seed(Local, new("old.grants", "grants", Grants: new([new("node:n", 1, "old-domain", 1, 3)])));
        var fresh = scene.Seed(Shard, new("fresh.grants", "grants", Grants: new([new("node:n", 2, "new-domain", 5, 1)])));
        Assert.True(scene.Apply(new TransferStorageFile(Local, Shard, old)).Accepted);
        var valid = Assert.Single(scene.Core.ReadCurrentGrants(Recipient, Shard, fresh));
        Assert.Equal(1, valid.Level);
        Assert.Equal(2UL, valid.Generation);
        Assert.Equal(5UL, valid.Epoch);
        Assert.Equal("new-domain", valid.Domain);
    }

    [Fact]
    public void OfferIsInertSnapshotAndFullMemoryAcceptanceDoesNotLoseIt()
    {
        var scene = new Scene(shard: 1);
        var original = scene.Seed(Local, new("offer.txt", Content: "original"));
        var resident = scene.Seed(Shard, new("keep.txt"));
        var offered = scene.Apply(new OfferStorageFile(Local, Shard, original, "Sender"));
        Assert.True(offered.Accepted);
        Assert.NotEqual(original, offered.File);
        Assert.Single(scene.Core.ReadStore(Shard)!.Files);
        Assert.False(Assert.Single(scene.Core.ReadOffers(Shard)).CanAccept);
        Assert.False(scene.Apply(new RespondStorageOffer(Shard, offered.OfferId!, true), Recipient).Accepted);
        Assert.Single(scene.Core.ReadOffers(Shard));
        Assert.True(scene.Apply(new EditStorageText(Local, original, "changed")).Accepted);
        Assert.True(scene.Apply(new DeleteStorageFile(Shard, resident), Recipient).Accepted);
        Assert.True(scene.Apply(new RespondStorageOffer(Shard, offered.OfferId!, true), Recipient).Accepted);
        Assert.Empty(scene.Core.ReadOffers(Shard));
        Assert.Equal("original", scene.Core.ReadFile(offered.File!)!.Content);
        Assert.Equal("changed", scene.Core.ReadFile(original)!.Content);
    }

    [Fact]
    public void MailboxLimitExpiryAndReplacementDoNotLeakOwnedSnapshots()
    {
        var scene = new Scene();
        var original = scene.Seed(Local, new("offer.txt"));
        for (var index = 0; index < 8; index++)
        {
            scene.Now = index * 20;
            Assert.True(scene.Apply(new OfferStorageFile(Local, Shard, original, "Sender")).Accepted);
        }
        scene.Now = 160;
        Assert.False(scene.Apply(new OfferStorageFile(Local, Shard, original, "Sender")).Accepted);
        Assert.Equal(9, scene.Core.FileCount);
        Assert.Equal(8, scene.Core.ReadOffers(Shard).Count);
        scene.Now = 2000;
        Assert.True(scene.Apply(new OfferStorageFile(Local, Shard, original, "Sender")).Accepted);
        Assert.Equal(2, scene.Core.FileCount);
        Assert.Single(scene.Core.ReadOffers(Shard));
        Assert.Equal(3200, scene.Core.NextExpiryDs);
        scene.Core.ExpireOffers(3200, 1);
        Assert.Equal(1, scene.Core.FileCount);
        Assert.Empty(scene.Core.ReadOffers(Shard));
        Assert.Null(scene.Core.NextExpiryDs);
    }

    [Fact]
    public void RequestReplayAndHistoryWatermarkNeverDuplicateFileMutation()
    {
        var scene = new Scene(options: new(HistoryCapacity: 1));
        var operation = new StorageOperation(new("replay", 1), Owner, new CreateStorageFile(Local, new("note.txt", Content: "payload")));
        var first = scene.Core.Apply(operation, 0);
        var before = scene.Core.ReadStore(Local)!;
        var duplicate = scene.Core.Apply(operation, 1);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.File, duplicate.File);
        Assert.Equal(before, scene.Core.ReadStore(Local));
        Assert.Equal("request_conflict", scene.Core.Apply(operation with { Action = new CreateStorageFile(Local, new("other.txt")) }, 1).Code);
        Assert.True(scene.Core.Apply(new(new("replay", 2), Owner, new ReadStorage(Local)), 1).Accepted);
        Assert.Equal("request_expired", scene.Core.Apply(operation, 1).Code);
        Assert.Equal(1, scene.Core.FileCount);
    }

    [Fact]
    public void OutputPressureDoesNotConsumeHeadAndPipelineCannotOvertakeIt()
    {
        var scene = new Scene(options: new(MaxEvents: 2));
        var original = scene.Seed(Local, new("cargo.txt"));
        var head = new StorageOperation(new("ordered", 1), Owner, new TransferStorageFile(Local, Shard, original, Move: true));
        var tail = new StorageOperation(new("ordered", 2), Owner, new ReadStorage(Local));
        var blocked = scene.Core.Apply(head, 0);
        Assert.True(blocked.WouldBlock);
        Assert.Equal("storage_output_pressure", blocked.Code);
        Assert.Equal("producer_backpressure", scene.Core.Apply(tail, 0).Code);
        scene.Drain();
        var retried = scene.Core.Apply(head, 0);
        Assert.True(retried.Accepted);
        Assert.False(retried.Duplicate);
        Assert.Equal(original, retried.File);
        Assert.True(scene.Core.Apply(tail, 0).Accepted);
        Assert.Empty(scene.Core.ReadStore(Local)!.Files);
    }

    [Fact]
    public void RecycledStoreIdentityDoesNotGrantAccessToNewDiskGeneration()
    {
        var scene = new Scene();
        var file = scene.Seed(Local, new("old.txt"));
        scene.Core.Grant(Local, Stranger, StorageRights.All);
        Assert.True(scene.Core.RemoveStore(Local));
        Assert.Null(scene.Core.ReadFile(file));
        Assert.False(scene.Core.RegisterStore(new(Local, 64, Owner: Owner)));
        var replacement = Local with { Generation = 2 };
        Assert.True(scene.Core.RegisterStore(new(replacement, 64, Owner: Owner)));
        Assert.False(scene.Apply(new ReadStorage(replacement), Stranger).Accepted);
        Assert.Contains(scene.Drain(), change => change.Store == Local && change.Removed);
    }

    [Fact]
    public void NestedPrivateCapabilitiesCannotEscapeInsideAnAgentContainer()
    {
        var scene = new Scene();
        scene.Core.SetSessionLive("dive", true);
        var package = scene.Seed(Local, new("agent.bot", "bot", "scout", 2, Children: new([
            new("identity.access", "access", Capabilities: FileCapabilities.Store, BoundStore: Local, SessionId: "dive")
        ])));
        var loadout = scene.Seed(Memory, new("keep.exe", "program", "patch", 2));
        var before = scene.Core.FileCount;
        Assert.False(scene.Apply(new TransferStorageFile(Local, Shard, package)).Accepted);
        Assert.False(scene.Apply(new RestoreAgentMemory(Local, package, Memory)).Accepted);
        Assert.Equal(before, scene.Core.FileCount);
        Assert.NotNull(scene.Core.ReadFile(loadout));
        Assert.NotNull(scene.Core.ReadFile(package)!.Children);
        Assert.Empty(scene.Core.ReadStore(Shard)!.Files);
    }

    [Fact]
    public void PackingRefusesToOverwriteExistingOwnedMemory()
    {
        var scene = new Scene();
        var package = scene.Seed(Local, new("agent.bot", "bot", "scout", 2, Children: new([
            new("keep.txt", Content: "previous")
        ])));
        var cargo = scene.Seed(Memory, new("cargo.txt"));
        var before = scene.Core.ReadFile(package)!;
        Assert.Equal("agent_package_not_empty", scene.Apply(new PackAgentMemory(Memory, Local, package, NetworkValue.Null)).Code);
        Assert.Equal(before, scene.Core.ReadFile(package));
        Assert.Equal(cargo, Assert.Single(scene.Core.ReadStore(Memory)!.Files).File);
    }

    [Fact]
    public void StaticRulesAndFingerprintNeedNoMutableStoreOrTemporaryRuntime()
    {
        Assert.True(StorageWorld.IsValidStoreDefinition(new(Local, 0)));
        Assert.False(StorageWorld.IsValidStoreDefinition(new(Local with { Generation = 0 }, 1)));
        var file = new FileDefinition("agent.bot", "bot", "scout", 2, Children: new([new("nested.txt")]));
        var operation = new StorageOperation(new("static", 1), Owner, new CreateStorageFile(Local, file));
        Assert.True(StorageWorld.IsValidFileDefinition(file));
        Assert.NotNull(StorageWorld.FingerprintOperation(operation));
        Assert.False(StorageWorld.IsValidFileDefinition(file, new(MaxTreeDepth: 1)));
        Assert.Null(StorageWorld.FingerprintOperation(operation, new(MaxTreeDepth: 1)));
        var core = new StorageWorld("static", new(MaxTreeDepth: 1));
        Assert.Null(core.Fingerprint(operation));
        Assert.Equal(0, core.StoreCount);
        Assert.Equal(0, core.FileCount);
        Assert.Equal(0, core.EventCount);
    }

    [Fact]
    public void CanonicalGrantMergeDoesNotIntroduceAnImplicit4096ScopeCap()
    {
        var scene = new Scene(shard: 1);
        var grants = Enumerable.Range(0, 5000).Select(index => new FileGrant("scope-" + index, 1, "home", 1, 2)).ToArray();
        foreach (var grant in grants) scene.Core.UpdateScope(new(grant.ScopeId, grant.Generation, grant.Domain, grant.Epoch));
        var source = scene.Seed(Local, new("many.grants", "grants", Grants: new(grants, 6000)));
        var canonical = scene.Seed(Shard, new("canonical.grants", "grants"));
        var copy = scene.Apply(new TransferStorageFile(Local, Shard, source));
        Assert.True(copy.Accepted);
        Assert.Equal(canonical, copy.File);
        Assert.Equal(5000, scene.Core.ReadCurrentGrants(Recipient, Shard, canonical).Count);
        Assert.Equal(1, scene.Core.ReadStore(Shard)!.Used);
    }
}
