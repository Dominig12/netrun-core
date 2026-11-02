namespace Netrun.Core;

/// <summary>
/// Single-owner координатор snapshot → catch-up → публикация фактов.
/// Неизвестный или повреждённый снимок не обслуживает читателей и не создаёт игровых эффектов.
/// </summary>
public sealed class WorldSynchronization : IDisposable, INetworkWorldView
{
    /// <summary>Неизменяемая идентичность сессии; новый binding требует нового координатора.</summary>
    private readonly Epochs _epochs;
    /// <summary>Ограничения staging, chunks и окна replay.</summary>
    private readonly CoreLimits _limits;
    /// <summary>Последняя целиком опубликованная модель, независимая от staging.</summary>
    private WorldMirror _published;
    /// <summary>Единственный строящийся mirror; освобождается при потере целостности.</summary>
    private WorldMirror? _staging;
    /// <summary>Инкрементальный checksum всех принятых chunks без хранения их payload.</summary>
    private SnapshotChecksumAccumulator? _checksum;
    /// <summary>Идентификатор текущего обхода; не меняется внутри сессии.</summary>
    private string? _snapshotId;
    /// <summary>Последняя воспроизведённая запись журнала, независимо от input sequence.</summary>
    private ulong _journalSequence;
    /// <summary>Ограниченный журнал всех успешно применённых control/data frames.</summary>
    private readonly AppliedReplayWindow _recent;
    /// <summary>Блокирует повторный вход; параллельный доступ по-прежнему запрещён.</summary>
    private bool _applying;
    /// <summary>После освобождения hash-ресурсов обработка входов запрещена.</summary>
    private bool _disposed;

    /// <summary>Стадия синхронизации фактов; Ready не подтверждает готовность будущего графа.</summary>
    public MirrorSyncState State { get; private set; } = MirrorSyncState.AwaitingSnapshot;
    /// <summary>Последний атомарно принятый внешний frame, включая служебные записи.</summary>
    public ulong AppliedSequence { get; private set; }
    /// <summary>Монотонная граница принятого игрового времени, в децисекундах.</summary>
    public int SimulationTimeDs { get; private set; }
    /// <summary>Read-only опубликованное состояние; до финального catch-up барьера пусто.</summary>
    public IReadOnlyDictionary<string, NodeFact> Nodes => _published.Nodes;
    /// <summary>Секторы публикуются тем же барьером, что и узлы; chunk не выдаёт частичную геометрию.</summary>
    public IReadOnlyDictionary<string, SectorFact> Sectors => _published.Sectors;
    /// <summary>Сервисы публикуются атомарно с остальными фактами.</summary>
    public IReadOnlyDictionary<string, ServiceFact> Services => _published.Services;

    /// <summary>Создаёт пустую сессию, ожидающую полный снимок; выполняется без IPC и фоновых задач.</summary>
    public WorldSynchronization(Epochs epochs, CoreLimits? limits = null)
    {
        _epochs = epochs;
        _limits = limits ?? new CoreLimits();
        _published = new WorldMirror(epochs, _limits);
        if (_limits.MaxSnapshotChunks < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _recent = new AppliedReplayWindow(_limits.ReplayWindow);
    }

    /// <summary>Проверяет целый frame; semantic replay не исполняет control или chunk повторно.</summary>
    public SynchronizationResult Apply(InputBatch batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(batch);
        if (_applying)
            throw new InvalidOperationException("Reentrant synchronization is not supported.");
        _applying = true;
        try { return ApplyCore(batch); }
        finally { _applying = false; }
    }

    /// <summary>
    /// Закрывает admission по внешней потере целостности runtime и освобождает staging/hash,
    /// сохраняя последнюю опубликованную модель для диагностики. Новый binding требует нового владельца.
    /// </summary>
    /// <param name="code">Доверенный диагностический token из ASCII lowercase, цифр и underscore, до 64 символов.</param>
    public SynchronizationResult Invalidate(string code)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(code) || code.Length > 64 ||
            code.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("Invalid synchronization error code.", nameof(code));
        if (_applying)
            throw new InvalidOperationException("Reentrant synchronization is not supported.");
        return Fail(code);
    }

    /// <summary>Освобождает incremental hash и staging; опубликованные immutable факты остаются доступными для диагностики.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        ReleaseStaging();
        State = MirrorSyncState.ResyncRequired;
        _disposed = true;
    }

    /// <summary>Проверяет идентичность, причинность и переход состояния прежде, чем продвинуть cumulative ACK.</summary>
    private SynchronizationResult ApplyCore(InputBatch batch)
    {
        if (State == MirrorSyncState.ResyncRequired)
            return Result(ApplyStatus.ResyncRequired, "resync_required");
        if (batch.Epochs != _epochs)
            return Result(ApplyStatus.Rejected, "epoch_mismatch");
        var fingerprint = SynchronizationFrameRules.Fingerprint(batch, _limits.MaxEntries);
        if (fingerprint is null)
            return Result(ApplyStatus.Rejected, "invalid_batch");
        if (batch.Sequence <= AppliedSequence)
            return _recent.Matches(batch.Sequence, fingerprint) ? Result(ApplyStatus.Duplicate, "duplicate") : Fail("replay_conflict_or_expired");
        if (AppliedSequence == ulong.MaxValue || batch.Sequence != AppliedSequence + 1)
            return Fail("sequence_gap");
        if (batch.SimulationTimeDs < SimulationTimeDs)
            return Result(ApplyStatus.Rejected, "time_regression");

        var failure = ApplyChanges(batch);
        if (failure is not null)
            return failure;
        AppliedSequence = batch.Sequence;
        SimulationTimeDs = batch.SimulationTimeDs;
        _recent.Record(batch.Sequence, fingerprint);
        return Result(ApplyStatus.Applied, "applied");
    }

    /// <summary>Применяет ровно один control либо узловой delta; null означает успешный переход.</summary>
    private SynchronizationResult? ApplyChanges(InputBatch batch)
    {
        var control = batch.Entries.Count == 1 ? batch.Entries[0] : null;
        if (control is SnapshotBegin begin)
        {
            if (State != MirrorSyncState.AwaitingSnapshot)
                return Result(ApplyStatus.Rejected, "snapshot_state");
            _staging = new WorldMirror(_epochs, _limits);
            _checksum = new SnapshotChecksumAccumulator(begin.ChecksumVersion);
            _snapshotId = begin.SnapshotId;
            _journalSequence = begin.JournalSequence;
            State = MirrorSyncState.Snapshotting;
            return null;
        }
        if (control is SnapshotChunk chunk)
        {
            if (State != MirrorSyncState.Snapshotting)
                return Result(ApplyStatus.Rejected, "snapshot_state");
            if (chunk.SnapshotId != _snapshotId)
                return Fail("snapshot_id_mismatch");
            if (chunk.ChunkIndex != _checksum!.ChunkCount + 1)
                return Fail("snapshot_chunk_gap");
            if (_checksum.ChunkCount >= (ulong)_limits.MaxSnapshotChunks)
                return Fail("snapshot_capacity");
            var failure = ApplyNodes(_staging!, chunk.Changes, batch.SimulationTimeDs);
            if (failure is not null)
                return failure;
            if (!_checksum.TryAppend(chunk))
                return Fail("snapshot_integrity");
            return null;
        }
        if (control is SnapshotCommit commit)
        {
            if (State != MirrorSyncState.Snapshotting)
                return Result(ApplyStatus.Rejected, "snapshot_state");
            if (commit.SnapshotId != _snapshotId)
                return Fail("snapshot_id_mismatch");
            if (commit.ChunkCount != _checksum!.ChunkCount || commit.RecordCount != _checksum.RecordCount ||
                !string.Equals(commit.Sha256, _checksum.GetDigest(), StringComparison.Ordinal))
                return Fail("snapshot_integrity");
            _checksum.Dispose();
            _checksum = null;
            State = MirrorSyncState.CatchingUp;
            return null;
        }
        if (control is CatchUpDelta delta)
        {
            if (State != MirrorSyncState.CatchingUp)
                return Result(ApplyStatus.Rejected, "snapshot_state");
            if (delta.SnapshotId != _snapshotId)
                return Fail("snapshot_id_mismatch");
            if (_journalSequence == ulong.MaxValue || delta.JournalSequence != _journalSequence + 1)
                return Fail("journal_gap");
            var failure = ApplyNodes(_staging!, delta.Changes, batch.SimulationTimeDs);
            if (failure is not null)
                return failure;
            _journalSequence = delta.JournalSequence;
            return null;
        }
        if (control is CatchUpComplete complete)
        {
            if (State != MirrorSyncState.CatchingUp)
                return Result(ApplyStatus.Rejected, "snapshot_state");
            if (complete.SnapshotId != _snapshotId)
                return Fail("snapshot_id_mismatch");
            if (complete.ThroughJournalSequence != _journalSequence)
                return Fail("journal_boundary");
            _published = _staging!;
            _staging = null;
            State = MirrorSyncState.Ready;
            return null;
        }
        if (State != MirrorSyncState.Ready)
            return Result(ApplyStatus.Rejected, "not_ready");
        return ApplyNodes(_published, batch.Entries, batch.SimulationTimeDs);
    }

    /// <summary>Внутренняя последовательность mirror не смешивается с внешним sequence control frames.</summary>
    private SynchronizationResult? ApplyNodes(WorldMirror mirror, IReadOnlyList<WorldChange> changes, int timeDs)
    {
        var result = mirror.Apply(new InputBatch(_epochs, mirror.AppliedSequence + 1, timeDs, changes));
        if (result.Status == ApplyStatus.Applied)
            return null;
        return State == MirrorSyncState.Ready && result.Status == ApplyStatus.Rejected
            ? Result(ApplyStatus.Rejected, result.Code) : Fail(result.Code);
    }

    /// <summary>Диагностика не раскрывает staging как опубликованное состояние; ACK отражает только полные frames.</summary>
    private SynchronizationResult Result(ApplyStatus status, string code) => new(
        new ApplyResult(status, code, AppliedSequence, SimulationTimeDs, _published.Nodes.Count, _published.TrackedCount),
        State, _staging?.Nodes.Count ?? 0);

    /// <summary>Закрывает admission и немедленно освобождает незавершённый снимок, сохраняя последний опубликованный mirror.</summary>
    private SynchronizationResult Fail(string code)
    {
        State = MirrorSyncState.ResyncRequired;
        ReleaseStaging();
        return Result(ApplyStatus.ResyncRequired, code);
    }

    /// <summary>Общий bounded cleanup при resync/dispose; не очищает опубликованное состояние.</summary>
    private void ReleaseStaging()
    {
        _checksum?.Dispose();
        _checksum = null;
        _staging = null;
        _snapshotId = null;
    }
}
