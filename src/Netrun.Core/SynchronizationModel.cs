namespace Netrun.Core;

/// <summary>Начинает изолированную сборку снимка; один control entry занимает целый входной пакет.</summary>
/// <param name="SnapshotId">Идентификатор снимка, до 128 символов.</param>
/// <param name="JournalSequence">Граница журнала изменений мира до начала обхода; ноль разрешён.</param>
/// <param name="ChecksumVersion">1 — исходные узлы, 2 — binary факты, 3 — raw-JSON chain для строкового ABI BYOND.</param>
public sealed record SnapshotBegin(string SnapshotId, ulong JournalSequence, int ChecksumVersion = 1) : WorldChange;

/// <summary>Ограниченный упорядоченный фрагмент снимка; вложены только node_upsert/node_remove.</summary>
/// <param name="SnapshotId">Снимок, которому принадлежит chunk.</param>
/// <param name="ChunkIndex">Непрерывный индекс от 1, независимый от input sequence.</param>
/// <param name="Changes">До 128 фактов/удалений; порядок входит в checksum.</param>
/// <param name="WireDigest">В версии 3 кодек вычисляет SHA-256 точных UTF-8 байтов changes; поле не принимается из JSON.</param>
public sealed record SnapshotChunk(string SnapshotId, ulong ChunkIndex, IReadOnlyList<WorldChange> Changes,
    string? WireDigest = null) : WorldChange;

/// <summary>Подтверждает целостность обхода; не публикует mirror до завершения catch-up.</summary>
/// <param name="SnapshotId">Завершаемый снимок.</param>
/// <param name="ChunkCount">Общее число chunks; ноль допустим для пустого мира.</param>
/// <param name="RecordCount">Число экспортированных записей, включая повторы и tombstones.</param>
/// <param name="Sha256">64 lowercase hex символа canonical digest всех chunks.</param>
public sealed record SnapshotCommit(string SnapshotId, ulong ChunkCount, ulong RecordCount, string Sha256) : WorldChange;

/// <summary>Воспроизводит один непрерывный элемент журнала, накопленного во время обхода.</summary>
/// <param name="SnapshotId">Снимок после commit.</param>
/// <param name="JournalSequence">Следующий номер после границы begin или предыдущей записи журнала.</param>
/// <param name="Changes">До 128 узловых изменений; не сохраняются как отдельная очередь ядра.</param>
public sealed record CatchUpDelta(string SnapshotId, ulong JournalSequence, IReadOnlyList<WorldChange> Changes) : WorldChange;

/// <summary>Публикует подготовленный mirror после точного воспроизведения объявленной границы журнала.</summary>
/// <param name="SnapshotId">Снимок, готовый к публикации.</param>
/// <param name="ThroughJournalSequence">Последняя воспроизведённая запись; может совпадать с begin для пустого журнала.</param>
public sealed record CatchUpComplete(string SnapshotId, ulong ThroughJournalSequence) : WorldChange;

/// <summary>Состояние синхронизации фактов; Ready не означает готовность ещё не реализованного графа.</summary>
public enum MirrorSyncState
{
    /// <summary>Полный снимок ещё не начат.</summary>
    AwaitingSnapshot,
    /// <summary>Факты поступают только в изолированную staging-модель.</summary>
    Snapshotting,
    /// <summary>Checksum подтверждён, воспроизводятся изменения после границы обхода.</summary>
    CatchingUp,
    /// <summary>Согласованный mirror опубликован для чтения.</summary>
    Ready,
    /// <summary>Целостность потеряна; staging освобождён, нужна новая binding-сессия.</summary>
    ResyncRequired
}

/// <summary>Результат входа синхронизации; read view остаётся прежней до финального барьера.</summary>
/// <param name="Apply">Статус, ACK и число опубликованных узлов; не размер staging.</param>
/// <param name="State">Текущая стадия синхронизации.</param>
/// <param name="StagedNodeCount">Число живых узлов в строящемся снимке.</param>
public sealed record SynchronizationResult(ApplyResult Apply, MirrorSyncState State, int StagedNodeCount);
