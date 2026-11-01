using Netrun.Core;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет snapshot/catch-up как атомарный барьер публикации, независимо от JSON и Host.</summary>
public sealed class WorldSynchronizationTests
{
    /// <summary>Общая идентичность тестовой binding-сессии; каждый тест создаёт независимого владельца.</summary>
    private static readonly Epochs Epochs = new("sync-world", "sync-engine", "sync-binding");

    /// <summary>Устойчивый ID снимка отделён от sequence входного потока и номеров журнала.</summary>
    private const string SnapshotId = "snapshot-1";

    /// <summary>Полный синтетический факт; revision и generation меняются только сценарием теста.</summary>
    private static NodeFact Fact(string id = "node-1", ulong generation = 1, ulong revision = 1, bool online = true) =>
        new(id, generation, revision, new("address-" + id, "server", new(10, 20, 1), "sector:1", online));

    /// <summary>Игровое время в децисекундах совпадает с sequence для простого монотонного тестового trace.</summary>
    private static InputBatch Batch(ulong sequence, params WorldChange[] entries) =>
        new(Epochs, sequence, checked((int)sequence), entries);

    /// <summary>Собирает корректный commit по экспортированным chunks; число записей включает повторы.</summary>
    private static SnapshotCommit Commit(params SnapshotChunk[] chunks) =>
        new(SnapshotId, (ulong)chunks.Length, (ulong)chunks.Sum(chunk => chunk.Changes.Count), SnapshotChecksum.Compute(chunks));

    /// <summary>Публикует один узел за четыре кадра, оставляя дальнейшие sequence начиная с 5.</summary>
    private static void PublishSingleNode(WorldSynchronization session)
    {
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0))).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(2, chunk)).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(3, Commit(chunk))).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(4, new CatchUpComplete(SnapshotId, 0))).Apply.Status);
        Assert.Equal(MirrorSyncState.Ready, session.State);
    }

    /// <summary>Commit подтверждает только staging; опубликованный мир появляется после воспроизведения журнала.</summary>
    [Fact]
    public void SnapshotIsPublishedOnlyAfterCatchUpBarrier()
    {
        using var session = new WorldSynchronization(Epochs);
        Assert.Equal(MirrorSyncState.AwaitingSnapshot, session.State);
        Assert.Empty(session.Nodes);
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        var begin = session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 40)));
        Assert.Equal(MirrorSyncState.Snapshotting, begin.State);
        var staged = session.Apply(Batch(2, chunk));
        Assert.Equal(1, staged.StagedNodeCount);
        Assert.Equal(0, staged.Apply.NodeCount);
        Assert.Empty(session.Nodes);

        var committed = session.Apply(Batch(3, Commit(chunk)));
        Assert.Equal(MirrorSyncState.CatchingUp, committed.State);
        Assert.Empty(session.Nodes);
        var replacement = Fact(generation: 2, online: false);
        var catchUp = session.Apply(Batch(4, new CatchUpDelta(SnapshotId, 41,
            [new NodeRemove("node-1", 1, 2), new NodeUpsert(replacement)])));
        Assert.Equal(ApplyStatus.Applied, catchUp.Apply.Status);
        Assert.Empty(session.Nodes);

        var ready = session.Apply(Batch(5, new CatchUpComplete(SnapshotId, 41)));
        Assert.Equal(MirrorSyncState.Ready, ready.State);
        Assert.Equal(0, ready.StagedNodeCount);
        Assert.Equal(5UL, ready.Apply.AppliedSequence);
        Assert.Equal(1, ready.Apply.NodeCount);
        Assert.Equal(replacement, session.Nodes["node-1"]);

        var liveUpdate = Fact(generation: 2, revision: 2);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(6, new NodeUpsert(liveUpdate))).Apply.Status);
        Assert.Equal(liveUpdate, session.Nodes["node-1"]);
    }

    /// <summary>Повтор любой стадии возвращает текущий ACK и не добавляет chunk или journal второй раз.</summary>
    [Fact]
    public void EveryAcceptedSynchronizationFrameCanBeReplayedWithoutReapplying()
    {
        using var session = new WorldSynchronization(Epochs);
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        InputBatch[] frames =
        [
            Batch(1, new SnapshotBegin(SnapshotId, 0)),
            Batch(2, chunk),
            Batch(3, Commit(chunk)),
            Batch(4, new CatchUpDelta(SnapshotId, 1, [new NodeRemove("node-1", 1, 2)])),
            Batch(5, new CatchUpComplete(SnapshotId, 1))
        ];
        foreach (var frame in frames)
        {
            Assert.Equal(ApplyStatus.Applied, session.Apply(frame).Apply.Status);
            var duplicate = session.Apply(frame);
            Assert.Equal(ApplyStatus.Duplicate, duplicate.Apply.Status);
            Assert.Equal(frame.Sequence, duplicate.Apply.AppliedSequence);
        }
        Assert.Equal(MirrorSyncState.Ready, session.State);
        Assert.Empty(session.Nodes);
        foreach (var frame in frames)
        {
            var delayedDuplicate = session.Apply(frame);
            Assert.Equal(ApplyStatus.Duplicate, delayedDuplicate.Apply.Status);
            Assert.Equal(5UL, delayedDuplicate.Apply.AppliedSequence);
            Assert.Equal(MirrorSyncState.Ready, delayedDuplicate.State);
        }
    }

    /// <summary>Пустой снимок с пустым журналом корректно проходит commit и финальный барьер.</summary>
    [Fact]
    public void EmptySnapshotAndEmptyJournalCanBecomeReady()
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        Assert.Equal(MirrorSyncState.CatchingUp, session.Apply(Batch(2, Commit())).State);
        var result = session.Apply(Batch(3, new CatchUpComplete(SnapshotId, 0)));
        Assert.Equal(MirrorSyncState.Ready, result.State);
        Assert.Empty(session.Nodes);
        Assert.Equal(3UL, session.AppliedSequence);
    }

    /// <summary>Факты до Ready не обходят snapshot-барьер и не расходуют sequence исправленного входа.</summary>
    [Fact]
    public void OrdinaryDeltasAreRejectedUntilReadyWithoutAdvancingAck()
    {
        using var session = new WorldSynchronization(Epochs);
        Assert.Equal("not_ready", session.Apply(Batch(1, new NodeUpsert(Fact()))).Apply.Code);
        Assert.Equal(0UL, session.AppliedSequence);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        Assert.Equal("not_ready", session.Apply(Batch(2, new NodeUpsert(Fact()))).Apply.Code);
        Assert.Equal(1UL, session.AppliedSequence);
        session.Apply(Batch(2, Commit()));
        Assert.Equal("not_ready", session.Apply(Batch(3, new NodeUpsert(Fact()))).Apply.Code);
        Assert.Equal(2UL, session.AppliedSequence);
        Assert.Equal(MirrorSyncState.Ready, session.Apply(Batch(3, new CatchUpComplete(SnapshotId, 0))).State);
    }

    /// <summary>Потеря целостности после Ready сохраняет последний мир для диагностики, но закрывает admission.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GapOrConflictingReplayClosesSessionAndPreservesPublishedFacts(bool conflict)
    {
        using var session = new WorldSynchronization(Epochs);
        PublishSingleNode(session);
        var invalid = conflict
            ? Batch(4, new CatchUpComplete(SnapshotId, 1))
            : Batch(6, new NodeRemove("node-1", 1, 2));
        var result = session.Apply(invalid);
        Assert.Equal(ApplyStatus.ResyncRequired, result.Apply.Status);
        Assert.Equal(MirrorSyncState.ResyncRequired, result.State);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Equal(4UL, result.Apply.AppliedSequence);
        Assert.Equal(Fact(), session.Nodes["node-1"]);
        Assert.Equal(ApplyStatus.ResyncRequired,
            session.Apply(Batch(5, new NodeRemove("node-1", 1, 2))).Apply.Status);
        Assert.Single(session.Nodes);
    }

    /// <summary>Неизвестный epoch не разрушает текущую сборку и не подтверждается как применённый.</summary>
    [Fact]
    public void WrongEpochIsRejectedWithoutChangingStagingOrAck()
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        var wrong = Batch(2, chunk) with { Epochs = Epochs with { Binding = "old-binding" } };
        var result = session.Apply(wrong);
        Assert.Equal("epoch_mismatch", result.Apply.Code);
        Assert.Equal(1UL, session.AppliedSequence);
        Assert.Equal(MirrorSyncState.Snapshotting, session.State);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(2, chunk)).Apply.Status);
    }

    /// <summary>Обратное игровое время не коммитится и не повреждает состояние строящегося снимка.</summary>
    [Fact]
    public void BackwardsTimeRejectsFrameWithoutAdvancingAck()
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)) with { SimulationTimeDs = 20 });
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        var result = session.Apply(Batch(2, chunk) with { SimulationTimeDs = 19 });
        Assert.Equal("time_regression", result.Apply.Code);
        Assert.Equal(1UL, session.AppliedSequence);
        Assert.Equal(20, session.SimulationTimeDs);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Equal(ApplyStatus.Applied,
            session.Apply(Batch(2, chunk) with { SimulationTimeDs = 20 }).Apply.Status);
    }

    /// <summary>Валидация всех вложенных фактов предшествует изменению staging; исправленный chunk сохраняет sequence.</summary>
    [Fact]
    public void InvalidNestedFactCannotPartiallyModifyStaging()
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        var invalidFact = Fact("node-2") with { Generation = 0 };
        var invalid = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact()), new NodeUpsert(invalidFact)]);
        var result = session.Apply(Batch(2, invalid));
        Assert.Equal("invalid_batch", result.Apply.Code);
        Assert.Equal(ApplyStatus.Rejected, result.Apply.Status);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Equal(1UL, session.AppliedSequence);
        var corrected = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        Assert.Equal(1, session.Apply(Batch(2, corrected)).StagedNodeCount);
    }

    /// <summary>Локальный лимит записей применяется отдельно к chunk и catch-up, а не только к внешнему control.</summary>
    [Fact]
    public void NestedEntryLimitIsEnforcedBeforeStagingInBothPhases()
    {
        using var session = new WorldSynchronization(Epochs, new CoreLimits(MaxEntries: 1));
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        var tooLarge = new SnapshotChunk(SnapshotId, 1,
            [new NodeUpsert(Fact()), new NodeUpsert(Fact("node-2"))]);
        var rejectedChunk = session.Apply(Batch(2, tooLarge));
        Assert.Equal("invalid_batch", rejectedChunk.Apply.Code);
        Assert.Equal(0, rejectedChunk.StagedNodeCount);
        Assert.Equal(1UL, session.AppliedSequence);
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        session.Apply(Batch(2, chunk));
        session.Apply(Batch(3, Commit(chunk)));
        var rejectedDelta = session.Apply(Batch(4, new CatchUpDelta(SnapshotId, 1,
            [new NodeRemove("node-1", 1, 2), new NodeUpsert(Fact("node-2"))])));
        Assert.Equal("invalid_batch", rejectedDelta.Apply.Code);
        Assert.Equal(1, rejectedDelta.StagedNodeCount);
        Assert.Equal(3UL, session.AppliedSequence);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(4,
            new CatchUpDelta(SnapshotId, 1, [new NodeRemove("node-1", 1, 2)]))).Apply.Status);
        Assert.Equal(MirrorSyncState.Ready, session.Apply(Batch(5, new CatchUpComplete(SnapshotId, 1))).State);
        Assert.Empty(session.Nodes);
    }

    /// <summary>Control нельзя смешать с другой записью или спрятать внутрь chunk.</summary>
    [Fact]
    public void MixedOrNestedControlsAreRejectedAtomically()
    {
        using var session = new WorldSynchronization(Epochs);
        Assert.Equal("invalid_batch", session.Apply(Batch(1,
            new SnapshotBegin(SnapshotId, 0), new NodeUpsert(Fact()))).Apply.Code);
        Assert.Equal(0UL, session.AppliedSequence);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        var nested = new SnapshotChunk(SnapshotId, 1, [new SnapshotBegin("nested", 0)]);
        Assert.Equal("invalid_batch", session.Apply(Batch(2, nested)).Apply.Code);
        Assert.Equal(1UL, session.AppliedSequence);
    }

    /// <summary>Пропущенный индекс chunk уничтожает незавершённый staging и требует нового binding.</summary>
    [Fact]
    public void ChunkIndexGapClearsStagingAndRequiresResync()
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        session.Apply(Batch(2, new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())])));
        var result = session.Apply(Batch(3, new SnapshotChunk(SnapshotId, 3, [])));
        Assert.Equal("snapshot_chunk_gap", result.Apply.Code);
        Assert.Equal(MirrorSyncState.ResyncRequired, result.State);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Empty(session.Nodes);
        Assert.Equal(2UL, session.AppliedSequence);
    }

    /// <summary>Chunk чужого снимка не может дописать факты в текущую staging-модель.</summary>
    [Fact]
    public void WrongSnapshotIdentityRequiresResync()
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        var result = session.Apply(Batch(2, new SnapshotChunk("another-snapshot", 1, [new NodeUpsert(Fact())])));
        Assert.Equal("snapshot_id_mismatch", result.Apply.Code);
        Assert.Equal(ApplyStatus.ResyncRequired, result.Apply.Status);
        Assert.Equal(0, result.StagedNodeCount);
    }

    /// <summary>Количество chunks, количество записей и digest проверяются независимо перед catch-up.</summary>
    [Theory]
    [InlineData("checksum")]
    [InlineData("chunks")]
    [InlineData("records")]
    public void CommitIntegrityMismatchCannotPublishSnapshot(string mismatch)
    {
        using var session = new WorldSynchronization(Epochs);
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(Fact())]);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        session.Apply(Batch(2, chunk));
        var commit = Commit(chunk);
        commit = mismatch switch
        {
            "checksum" => commit with { Sha256 = new string('0', 64) },
            "chunks" => commit with { ChunkCount = 2 },
            _ => commit with { RecordCount = 2 }
        };
        var result = session.Apply(Batch(3, commit));
        Assert.Equal("snapshot_integrity", result.Apply.Code);
        Assert.Equal(MirrorSyncState.ResyncRequired, result.State);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Empty(session.Nodes);
        Assert.Equal(2UL, result.Apply.AppliedSequence);
    }

    /// <summary>Пропуск в журнале нельзя замаскировать непрерывной транспортной последовательностью.</summary>
    [Fact]
    public void CatchUpJournalGapIsIndependentOfInputSequence()
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 100)));
        session.Apply(Batch(2, Commit()));
        var result = session.Apply(Batch(3, new CatchUpDelta(SnapshotId, 102, [new NodeUpsert(Fact())])));
        Assert.Equal("journal_gap", result.Apply.Code);
        Assert.Equal(MirrorSyncState.ResyncRequired, result.State);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Equal(2UL, result.Apply.AppliedSequence);
    }

    /// <summary>Final barrier обязан назвать именно воспроизведённую границу, а не будущий или старый journal ID.</summary>
    [Theory]
    [InlineData(9UL)]
    [InlineData(11UL)]
    public void CatchUpBoundaryMustEqualLastAppliedJournalRecord(ulong boundary)
    {
        using var session = new WorldSynchronization(Epochs);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 9)));
        session.Apply(Batch(2, Commit()));
        session.Apply(Batch(3, new CatchUpDelta(SnapshotId, 10, [new NodeUpsert(Fact())])));
        var result = session.Apply(Batch(4, new CatchUpComplete(SnapshotId, boundary)));
        Assert.Equal("journal_boundary", result.Apply.Code);
        Assert.Equal(MirrorSyncState.ResyncRequired, result.State);
        Assert.Empty(session.Nodes);
    }

    /// <summary>Нарушение фаз отклоняется без потери валидной текущей стадии и без ACK.</summary>
    [Fact]
    public void ControlInWrongPhaseIsRejectedWithoutAdvancingAck()
    {
        using var session = new WorldSynchronization(Epochs);
        Assert.Equal("snapshot_state", session.Apply(Batch(1, Commit())).Apply.Code);
        Assert.Equal(MirrorSyncState.AwaitingSnapshot, session.State);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        Assert.Equal("snapshot_state", session.Apply(Batch(2, new CatchUpDelta(SnapshotId, 1, []))).Apply.Code);
        Assert.Equal(MirrorSyncState.Snapshotting, session.State);
        Assert.Equal(1UL, session.AppliedSequence);
    }

    /// <summary>Даже пустые chunks расходуют лимит работ snapshot и не допускают бесконечной сборки.</summary>
    [Fact]
    public void SnapshotChunkLimitIncludesEmptyChunks()
    {
        using var session = new WorldSynchronization(Epochs, new CoreLimits(MaxSnapshotChunks: 1));
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        session.Apply(Batch(2, new SnapshotChunk(SnapshotId, 1, [])));
        var result = session.Apply(Batch(3, new SnapshotChunk(SnapshotId, 2, [])));
        Assert.Equal("snapshot_capacity", result.Apply.Code);
        Assert.Equal(MirrorSyncState.ResyncRequired, result.State);
        Assert.Equal(0, result.StagedNodeCount);
    }

    /// <summary>Переполнение staging не публикует первые допустимые записи того же chunk.</summary>
    [Fact]
    public void StagingNodeCapacityFailureDiscardsTheWholeSnapshot()
    {
        using var session = new WorldSynchronization(Epochs, new CoreLimits(MaxTrackedNodes: 1));
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        var result = session.Apply(Batch(2, new SnapshotChunk(SnapshotId, 1,
            [new NodeUpsert(Fact()), new NodeRemove("unknown-node", 1, 1)])));
        Assert.Equal("mirror_capacity", result.Apply.Code);
        Assert.Equal(ApplyStatus.ResyncRequired, result.Apply.Status);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Empty(session.Nodes);
        Assert.Equal(1UL, session.AppliedSequence);
    }

    /// <summary>Более свежий факт, увиденный сканером, не откатывается старой записью журнала после commit.</summary>
    [Fact]
    public void CatchUpPreservesNewerRevisionAlreadyObservedBySnapshot()
    {
        using var session = new WorldSynchronization(Epochs);
        var newest = Fact(revision: 3, online: false);
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeUpsert(newest)]);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        session.Apply(Batch(2, chunk));
        session.Apply(Batch(3, Commit(chunk)));
        session.Apply(Batch(4, new CatchUpDelta(SnapshotId, 1, [new NodeUpsert(Fact(revision: 2))])));
        session.Apply(Batch(5, new CatchUpComplete(SnapshotId, 1)));
        Assert.Equal(newest, session.Nodes["node-1"]);
    }

    /// <summary>Поздно экспортированный старый факт не воскрешает поколение с более свежим tombstone в снимке.</summary>
    [Fact]
    public void OlderScanRecordCannotResurrectTombstonedGeneration()
    {
        using var session = new WorldSynchronization(Epochs);
        var first = new SnapshotChunk(SnapshotId, 1, [new NodeRemove("node-1", 1, 3)]);
        var second = new SnapshotChunk(SnapshotId, 2, [new NodeUpsert(Fact(revision: 2))]);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        session.Apply(Batch(2, first));
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(3, second)).Apply.Status);
        session.Apply(Batch(4, Commit(first, second)));
        var result = session.Apply(Batch(5, new CatchUpComplete(SnapshotId, 0)));
        Assert.Equal(MirrorSyncState.Ready, result.State);
        Assert.Empty(session.Nodes);
        Assert.Equal(1, result.Apply.TrackedCount);
    }

    /// <summary>Попытка вернуть удалённое поколение большей revision остаётся ошибкой и во время catch-up.</summary>
    [Fact]
    public void CatchUpCannotReopenRetiredGeneration()
    {
        using var session = new WorldSynchronization(Epochs);
        var chunk = new SnapshotChunk(SnapshotId, 1, [new NodeRemove("node-1", 1, 2)]);
        session.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)));
        session.Apply(Batch(2, chunk));
        session.Apply(Batch(3, Commit(chunk)));
        var result = session.Apply(Batch(4, new CatchUpDelta(SnapshotId, 1, [new NodeUpsert(Fact(revision: 3))])));
        Assert.Equal("generation_retired", result.Apply.Code);
        Assert.Equal(MirrorSyncState.ResyncRequired, result.State);
        Assert.Equal(0, result.StagedNodeCount);
        Assert.Empty(session.Nodes);
    }

    /// <summary>Вытеснённый replay не считается новым входом; восстановление проходит только в новом binding.</summary>
    [Fact]
    public void ExpiredReplayRequiresFreshBindingSession()
    {
        using var oldSession = new WorldSynchronization(Epochs, new CoreLimits(ReplayWindow: 1));
        var begin = Batch(1, new SnapshotBegin(SnapshotId, 0));
        oldSession.Apply(begin);
        oldSession.Apply(Batch(2, Commit()));
        Assert.Equal(ApplyStatus.ResyncRequired, oldSession.Apply(begin).Apply.Status);

        var freshEpochs = Epochs with { Binding = "fresh-binding" };
        using var fresh = new WorldSynchronization(freshEpochs);
        Assert.Equal(ApplyStatus.Applied,
            fresh.Apply(Batch(1, new SnapshotBegin(SnapshotId, 0)) with { Epochs = freshEpochs }).Apply.Status);
        Assert.Equal(MirrorSyncState.Snapshotting, fresh.State);
        Assert.Equal(1UL, fresh.AppliedSequence);
    }

    /// <summary>Детерминированный trace смешивает раннее/позднее чтение snapshot с удалением и пересозданием объектов.</summary>
    [Fact]
    public void SnapshotAndCatchUpConvergeUnderDeterministicChurnDuringScan()
    {
        var random = new Random(1985);
        var scan = new List<WorldChange>();
        var journal = new List<WorldChange[]>();
        var expected = new Dictionary<string, NodeFact>(StringComparer.Ordinal);
        for (var i = 0; i < 100; i++)
        {
            var id = "node-" + i;
            var original = Fact(id);
            var readBeforeChange = random.Next(2) == 0;
            switch (random.Next(4))
            {
                case 0:
                    scan.Add(new NodeUpsert(original));
                    expected[id] = original;
                    break;
                case 1:
                    var updated = Fact(id, revision: 3, online: false);
                    scan.Add(new NodeUpsert(readBeforeChange ? original : updated));
                    journal.Add([new NodeUpsert(updated)]);
                    expected[id] = updated;
                    break;
                case 2:
                    if (readBeforeChange)
                        scan.Add(new NodeUpsert(original));
                    journal.Add([new NodeRemove(id, 1, 2)]);
                    break;
                default:
                    var replacement = Fact(id, generation: 2);
                    scan.Add(new NodeUpsert(readBeforeChange ? original : replacement));
                    journal.Add([new NodeRemove(id, 1, 2), new NodeUpsert(replacement)]);
                    expected[id] = replacement;
                    break;
            }
        }
        var chunks = scan.Chunk(25).Select((changes, index) =>
            new SnapshotChunk(SnapshotId, (ulong)index + 1, changes)).ToArray();
        using var session = new WorldSynchronization(Epochs);
        ulong sequence = 1;
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(sequence++, new SnapshotBegin(SnapshotId, 0))).Apply.Status);
        foreach (var chunk in chunks)
            Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(sequence++, chunk)).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(sequence++, Commit(chunks))).Apply.Status);
        for (var i = 0; i < journal.Count; i++)
            Assert.Equal(ApplyStatus.Applied, session.Apply(Batch(sequence++,
                new CatchUpDelta(SnapshotId, (ulong)i + 1, journal[i]))).Apply.Status);
        Assert.Empty(session.Nodes);
        var ready = session.Apply(Batch(sequence, new CatchUpComplete(SnapshotId, (ulong)journal.Count)));
        Assert.Equal(MirrorSyncState.Ready, ready.State);
        Assert.Equal(expected.Count, session.Nodes.Count);
        foreach (var (id, fact) in expected)
            Assert.Equal(fact, session.Nodes[id]);
    }
}
