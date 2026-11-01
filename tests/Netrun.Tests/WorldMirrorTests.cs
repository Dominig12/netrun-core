using Netrun.Core;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет атомарное владение фактами, версионирование, replay и пределы памяти без wire-кодека.</summary>
public sealed class WorldMirrorTests
{
    /// <summary>Фиксированная независимая сессия для сценариев применения и повтора пакетов.</summary>
    private static readonly Epochs Epochs = new("test-world", "test-engine", "test-binding");

    /// <summary>Создаёт полный валидный факт; параметры меняют только проверяемые в конкретном сценарии свойства.</summary>
    private static NodeUpsert Upsert(string id = "node-1", ulong generation = 1, ulong revision = 1, bool online = true) =>
        new(new NodeFact(id, generation, revision, new NodeState("address-" + id, "server", new Position(10, 20, 1), "sector:1", online)));

    /// <summary>Создаёт упорядоченный пакет на отметке 10 ds; sequence задаётся явно для сценариев ACK и replay.</summary>
    private static InputBatch Batch(ulong sequence, params WorldChange[] entries) => new(Epochs, sequence, 10, entries);

    /// <summary>Конфликт последней записи отменяет весь пакет; исправленный пакет может использовать тот же sequence.</summary>
    [Fact]
    public void WholeBatchIsValidatedBeforePublishingAnyChanges()
    {
        var mirror = new WorldMirror(Epochs);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(1, Upsert())).Status);
        var result = mirror.Apply(Batch(2, Upsert("node-2"), Upsert(revision: 1, online: false)));
        Assert.Equal("revision_conflict", result.Code);
        Assert.Equal(1UL, mirror.AppliedSequence);
        Assert.Single(mirror.Nodes);
        Assert.True(mirror.Nodes["node-1"].State.Online);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(2, Upsert("node-2"))).Status);
    }

    /// <summary>Staging учитывает предыдущие записи того же пакета: создание, удаление и новое поколение идут по порядку.</summary>
    [Fact]
    public void OrderedChangesWithinABatchSeeEarlierChanges()
    {
        var mirror = new WorldMirror(Epochs);
        mirror.Apply(Batch(1, Upsert(), new NodeRemove("node-1", 1, 2), Upsert(generation: 2)));
        Assert.Equal(2UL, mirror.Nodes["node-1"].Generation);
    }

    /// <summary>Удалённое поколение остаётся закрытым; устаревшие записи игнорируются, новое поколение разрешено.</summary>
    [Fact]
    public void TombstoneCannotBeResurrectedWithinItsGeneration()
    {
        var mirror = new WorldMirror(Epochs);
        mirror.Apply(Batch(1, new NodeRemove("node-1", 2, 3)));
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(2, Upsert(generation: 1, revision: 100))).Status);
        Assert.Empty(mirror.Nodes);
        Assert.Equal("generation_retired", mirror.Apply(Batch(3, Upsert(generation: 2, revision: 4))).Code);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(3, Upsert(generation: 3))).Status);
        Assert.Single(mirror.Nodes);
    }

    /// <summary>Поздняя старая ревизия не возвращает узлу прежнее состояние доступности.</summary>
    [Fact]
    public void StaleRevisionsCannotReplaceNewerFacts()
    {
        var mirror = new WorldMirror(Epochs);
        mirror.Apply(Batch(1, Upsert(revision: 2, online: false)));
        mirror.Apply(Batch(2, Upsert(revision: 1)));
        Assert.False(mirror.Nodes["node-1"].State.Online);
    }

    /// <summary>Повтор старого пакета возвращает текущий cumulative ACK и не откатывает более свежий факт.</summary>
    [Fact]
    public void DuplicateReturnsCumulativeAppliedAckWithoutReapplying()
    {
        var mirror = new WorldMirror(Epochs);
        var first = Batch(1, Upsert());
        mirror.Apply(first);
        mirror.Apply(Batch(2, Upsert(revision: 2, online: false)));
        var duplicate = mirror.Apply(first);
        Assert.Equal(ApplyStatus.Duplicate, duplicate.Status);
        Assert.Equal(2UL, duplicate.AppliedSequence);
        Assert.False(mirror.Nodes["node-1"].State.Online);
    }

    /// <summary>Различное содержимое под прежним sequence навсегда закрывает admission этой сессии.</summary>
    [Fact]
    public void ConflictingReplayLatchesResyncAndPreventsFurtherMutation()
    {
        var mirror = new WorldMirror(Epochs);
        mirror.Apply(Batch(1, Upsert()));
        Assert.Equal(ApplyStatus.ResyncRequired, mirror.Apply(Batch(1, Upsert(online: false))).Status);
        Assert.Equal(ApplyStatus.ResyncRequired, mirror.Apply(Batch(2, Upsert("node-2"))).Status);
        Assert.Single(mirror.Nodes);
    }

    /// <summary>После вытеснения fingerprint старый sequence требует resync вместо повторного исполнения.</summary>
    [Fact]
    public void ExpiredReplayCannotBeTreatedAsANewBatch()
    {
        var mirror = new WorldMirror(Epochs, new CoreLimits(ReplayWindow: 1));
        mirror.Apply(Batch(1));
        mirror.Apply(Batch(2));
        Assert.Equal(ApplyStatus.ResyncRequired, mirror.Apply(Batch(1)).Status);
    }

    /// <summary>Пропущенный sequence фиксирует потерю целостности, которую нельзя исправить поздним пакетом в том же binding.</summary>
    [Fact]
    public void SequenceGapRequiresAFreshBinding()
    {
        var mirror = new WorldMirror(Epochs);
        Assert.Equal("sequence_gap", mirror.Apply(Batch(2, Upsert())).Code);
        Assert.Equal(ApplyStatus.ResyncRequired, mirror.Apply(Batch(1, Upsert())).Status);
        Assert.Empty(mirror.Nodes);
    }

    /// <summary>Чужие epochs и регресс игрового времени не меняют подтверждённую границу или состояние часов.</summary>
    [Fact]
    public void WrongEpochAndBackwardsTimeDoNotAdvanceAck()
    {
        var mirror = new WorldMirror(Epochs);
        Assert.Equal("epoch_mismatch", mirror.Apply(Batch(1) with { Epochs = Epochs with { Binding = "old" } }).Code);
        mirror.Apply(Batch(1, Upsert()));
        Assert.Equal("time_regression", mirror.Apply(Batch(2) with { SimulationTimeDs = 9 }).Code);
        Assert.Equal(1UL, mirror.AppliedSequence);
        Assert.Equal(10, mirror.SimulationTimeDs);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(2) with { SimulationTimeDs = 11 }).Status);
    }

    /// <summary>Неизвестное удаление тоже занимает tombstone-слот; превышение capacity не публикует часть пакета.</summary>
    [Fact]
    public void CapacityIncludesUnknownRemovalsAndRejectsWholeBatch()
    {
        var mirror = new WorldMirror(Epochs, new CoreLimits(MaxTrackedNodes: 1));
        var result = mirror.Apply(Batch(1, Upsert(), new NodeRemove("node-2", 1, 1)));
        Assert.Equal("mirror_capacity", result.Code);
        Assert.Equal(0, result.TrackedCount);
        Assert.Empty(mirror.Nodes);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(1, new NodeRemove("node-1", 1, 1))).Status);
        Assert.Equal("mirror_capacity", mirror.Apply(Batch(2, Upsert("node-2"))).Code);
    }

    /// <summary>Прямой API библиотеки проверяет доменные инварианты даже без предварительного разбора JSON.</summary>
    [Fact]
    public void InvalidDomainInputIsRejectedWithoutPartialUpdates()
    {
        var mirror = new WorldMirror(Epochs);
        var invalid = Upsert("node-2") with { Node = Upsert().Node with { Generation = 0 } };
        Assert.Equal("invalid_batch", mirror.Apply(Batch(1, Upsert(), invalid)).Code);
        Assert.Empty(mirror.Nodes);
    }

    /// <summary>Непарный surrogate в ID или состоянии не заменяется при hashing и не оставляет частичных изменений.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUnicodeInDirectDomainInputIsRejectedAtomically(bool invalidStateString)
    {
        var mirror = new WorldMirror(Epochs);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(1, Upsert())).Status);
        var original = mirror.Nodes["node-1"];
        var fact = Upsert("node-2").Node;
        var malformed = "invalid-" + '\ud800';
        fact = invalidStateString
            ? fact with { State = fact.State with { Address = malformed } }
            : fact with { NodeId = malformed };

        var result = mirror.Apply(Batch(2, Upsert("node-3"), new NodeUpsert(fact)));

        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal("invalid_batch", result.Code);
        Assert.Equal(1UL, result.AppliedSequence);
        Assert.Equal(1, result.TrackedCount);
        Assert.Equal(10, mirror.SimulationTimeDs);
        Assert.Single(mirror.Nodes);
        Assert.Equal(original, mirror.Nodes["node-1"]);
        Assert.Equal(ApplyStatus.Applied, mirror.Apply(Batch(2, Upsert("node-3"))).Status);
    }

    /// <summary>Повреждённый Unicode любого epoch отклоняется при создании сессии до приёма её первого пакета.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidUnicodeEpochIsRejectedAtConstruction(int epochField)
    {
        var malformed = "invalid-" + '\ud800';
        var epochs = epochField switch
        {
            0 => Epochs with { World = malformed },
            1 => Epochs with { Engine = malformed },
            _ => Epochs with { Binding = malformed }
        };

        Assert.Throws<ArgumentException>(() => new WorldMirror(epochs));
    }
}
