using Netrun.Core;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет общую транзакцию фактов разных модулей и snapshot полного каталога.</summary>
public sealed class NetworkCatalogTests
{
    /// <summary>Независимая сессия тестового мира.</summary>
    private static readonly Epochs Epochs = new("world", "engine", "binding");
    /// <summary>Физический узел с native metadata области.</summary>
    private static NodeFact Node(ulong revision = 1) => new("node", 1, revision,
        new("APC-1", "apc", new(10, 20, 7), "sector", true, new("area", "area", "engineering")));
    /// <summary>Порядок этажей намеренно отличается от числовой сортировки.</summary>
    private static SectorFact Sector(params int[] levels) => new("sector", 1, 1, new(levels.Length == 0 ? [7, 2] : levels));
    /// <summary>Сервис указывает поколение owner и ограниченный набор операций.</summary>
    private static ServiceFact Service(ulong revision = 1) => new("service", 1, revision, new("node", 1),
        "atmos.controller", null, true, new([new OperationContract("status", OperationSafety.ReadOnly)]));
    /// <summary>Составляет пакет независимых модулей с сохранением порядка.</summary>
    private static InputBatch Batch(ulong sequence, params WorldChange[] changes) => new(Epochs, sequence, 0, changes);

    /// <summary>Конфликт сектора не публикует стоящее перед ним изменение узла или сервиса.</summary>
    [Fact]
    public void CrossModuleConflictLeavesWholeWorldUnchanged()
    {
        var mirror = new WorldMirror(Epochs);
        mirror.Apply(Batch(1, new NodeUpsert(Node()), new SectorUpsert(Sector()), new ServiceUpsert(Service())));
        var result = mirror.Apply(Batch(2, new NodeUpsert(Node(2)), new ServiceRemove("service", 1, 2),
            new SectorUpsert(Sector(2, 7))));
        Assert.Equal("revision_conflict", result.Code);
        Assert.Equal(1UL, mirror.AppliedSequence);
        Assert.Equal(1UL, mirror.Nodes["node"].Revision);
        Assert.Single(mirror.Services);
        Assert.Equal(new[] { 7, 2 }, mirror.Sectors["sector"].Levels);
    }

    /// <summary>Сравнение immutable коллекций выполняется по значениям, а не ссылкам.</summary>
    [Fact]
    public void EquivalentSectorAndServiceRevisionsDoNotConflict()
    {
        var mirror = new WorldMirror(Epochs);
        mirror.Apply(Batch(1, new SectorUpsert(Sector()), new ServiceUpsert(Service())));
        Assert.Equal(ApplyStatus.Applied,
            mirror.Apply(Batch(2, new SectorUpsert(Sector()), new ServiceUpsert(Service()))).Status);
    }

    /// <summary>Metadata и порядок этажей входят в fingerprint, поэтому другой replay требует resync.</summary>
    [Fact]
    public void CatalogAndNetworkMetadataBelongToReplayIdentity()
    {
        var mirror = new WorldMirror(Epochs);
        mirror.Apply(Batch(1, new NodeUpsert(Node()), new SectorUpsert(Sector())));
        var changed = Node() with { State = Node().State with { Network = new("area", "area", "science") } };
        Assert.Equal(ApplyStatus.ResyncRequired, mirror.Apply(Batch(1, new NodeUpsert(changed), new SectorUpsert(Sector()))).Status);
    }

    /// <summary>Tombstones каталогов ограничены памятью и не позволяют воскресить удалённое поколение.</summary>
    [Fact]
    public void CatalogTombstonesUseCapacityAndRetireGenerations()
    {
        var mirror = new WorldMirror(Epochs, new CoreLimits(MaxTrackedServices: 1));
        mirror.Apply(Batch(1, new ServiceRemove("service", 1, 2)));
        Assert.Equal("generation_retired", mirror.Apply(Batch(2, new ServiceUpsert(Service(3)))).Code);
        var other = Service() with { ServiceId = "other" };
        Assert.Equal("catalog_capacity", mirror.Apply(Batch(2, new ServiceUpsert(other))).Code);
        Assert.Empty(mirror.Services);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(2, new ServiceUpsert(Service() with { Generation = 2 }))).Status);
    }

    /// <summary>Nullspace узел допустим без выдуманных координат или сектора.</summary>
    [Fact]
    public void MissingPhysicalPositionIsRepresentedExplicitly()
    {
        var mirror = new WorldMirror(Epochs);
        var node = Node() with { State = new("unplaced", "server", null, null, false) };
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(1, new NodeUpsert(node))).Status);
        Assert.Null(mirror.Nodes["node"].State.Position);
        var invalid = node with { Revision = 2, State = node.State with { SectorId = "invented" } };
        Assert.Equal("invalid_batch", mirror.Apply(Batch(2, new NodeUpsert(invalid))).Code);
    }

    /// <summary>Полный snapshot публикует все модули одним барьером; исходные массивы не меняют принятые факты.</summary>
    [Fact]
    public void ExtendedSnapshotPublishesNodesSectorsAndServicesTogether()
    {
        var levels = new[] { 7, 2 };
        var sector = Sector() with { Levels = new(levels) };
        levels[0] = 99;
        var chunk = new SnapshotChunk("snapshot", 1,
            [new ServiceUpsert(Service()), new SectorUpsert(sector), new NodeUpsert(Node())]);
        using var session = new WorldSynchronization(Epochs);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(1, new SnapshotBegin("snapshot", 0, 2))).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(2, chunk)).Apply.Status);
        Assert.Empty(session.Nodes);
        Assert.Empty(session.Sectors);
        Assert.Empty(session.Services);
        var digest = SnapshotChecksum.Compute([chunk], 2);
        session.Apply(Batch(3, new SnapshotCommit("snapshot", 1, 3, digest)));
        Assert.Empty(session.Services);
        Assert.Equal(MirrorSyncState.Ready, session.Apply(Batch(4, new CatchUpComplete("snapshot", 0))).State);
        Assert.Single(session.Nodes);
        Assert.Single(session.Services);
        Assert.Equal(new[] { 7, 2 }, session.Sectors["sector"].Levels);
    }

    /// <summary>Старый checksum нельзя использовать для пропуска новых типов фактов.</summary>
    [Fact]
    public void LegacySnapshotCannotIgnoreExtendedFacts()
    {
        var chunk = new SnapshotChunk("snapshot", 1, [new SectorUpsert(Sector())]);
        Assert.Throws<ArgumentException>(() => SnapshotChecksum.Compute([chunk]));
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin("snapshot", 0)));
        Assert.Equal(MirrorSyncState.ResyncRequired, session.Apply(Batch(2, chunk)).State);
        Assert.Empty(session.Sectors);
    }

    /// <summary>Каталог не принимает неоднозначные уровни или дублирующиеся операции.</summary>
    [Fact]
    public void InvalidCatalogFactsCannotBypassDomainValidation()
    {
        Assert.False(FactValidation.IsValid(new SectorUpsert(Sector(7, 7))));
        Assert.False(FactValidation.IsValid(new ServiceUpsert(Service() with
        {
            Operations = new([
            new OperationContract("status"), new OperationContract("status")])
        })));
        Assert.False(FactValidation.IsValid(new NodeUpsert(Node() with
        {
            State = Node().State with
            {
                Network = new("area", "area", "unknown-department")
            }
        })));
    }

    /// <summary>Формат 2 имеет независимый Python golden vector, а не тест того же writer самим собой.</summary>
    [Fact]
    public void ExtendedChecksumMatchesIndependentBinaryVector()
    {
        var sector = new SectorFact("s", 1, 1, new([9, 3]));
        Assert.Equal("08c79561c405b6cdd4a3502019997ccfe2b291007bd7a01a94fe530efd2da516",
            SnapshotChecksum.Compute([new SnapshotChunk("snapshot", 1, [new SectorUpsert(sector)])], 2));
    }
}
