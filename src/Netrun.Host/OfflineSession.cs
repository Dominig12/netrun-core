using System.Globalization;
using System.Text.Json;
using Netrun.Core;

namespace Netrun.Host;

/// <summary>Composition одного прогона; объединяет legacy-стенд и обязательный snapshot без дублирования I/O.</summary>
internal sealed class OfflineSession : IDisposable
{
    /// <summary>Прямой mirror только для сохранённого режима replay v1.0.</summary>
    private readonly IWorldMirror? _mirror;
    /// <summary>Координатор обязательной синхронизации в режимах *-sync.</summary>
    private readonly WorldSynchronization? _synchronization;
    /// <summary>Независимая исходящая последовательность диагностических результатов.</summary>
    private ulong _outputSequence;

    /// <summary>EOF допустим только после публикации полного mirror в synchronized-режиме.</summary>
    internal bool IsComplete => _synchronization is null || _synchronization.State == MirrorSyncState.Ready;

    /// <summary>Явно выбирает контракт прогона; legacy не включается автоматически при ошибке snapshot.</summary>
    internal OfflineSession(Epochs epochs, bool requireSnapshot)
    {
        if (requireSnapshot)
            _synchronization = new WorldSynchronization(epochs);
        else
            _mirror = new WorldMirror(epochs);
    }

    /// <summary>Применяет вход и кодирует только безопасную диагностику; не сохраняет входной пакет.</summary>
    internal (byte[] Body, ApplyStatus Status) Apply(InputBatch batch)
    {
        var synchronization = _synchronization?.Apply(batch);
        var result = synchronization?.Apply ?? _mirror!.Apply(batch);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "apply_result");
            writer.WriteString("output_sequence", (++_outputSequence).ToString(CultureInfo.InvariantCulture));
            writer.WriteString("status", result.Status switch
            {
                ApplyStatus.Applied => "applied",
                ApplyStatus.Duplicate => "duplicate",
                ApplyStatus.Rejected => "rejected",
                ApplyStatus.ResyncRequired => "resync_required",
                _ => throw new InvalidOperationException()
            });
            writer.WriteString("code", result.Code);
            writer.WriteString("applied_sequence", result.AppliedSequence.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("simulation_time_ds", result.SimulationTimeDs);
            writer.WriteNumber("node_count", result.NodeCount);
            writer.WriteNumber("tracked_count", result.TrackedCount);
            if (synchronization is not null)
            {
                writer.WriteString("sync_state", synchronization.State switch
                {
                    MirrorSyncState.AwaitingSnapshot => "awaiting_snapshot",
                    MirrorSyncState.Snapshotting => "snapshotting",
                    MirrorSyncState.CatchingUp => "catching_up",
                    MirrorSyncState.Ready => "ready",
                    MirrorSyncState.ResyncRequired => "resync_required",
                    _ => throw new InvalidOperationException()
                });
                writer.WriteNumber("staged_node_count", synchronization.StagedNodeCount);
            }
            writer.WriteEndObject();
        }
        return (buffer.ToArray(), result.Status);
    }

    /// <summary>Освобождает staging/hash, в том числе после protocol error или отмены чтения.</summary>
    public void Dispose() => _synchronization?.Dispose();
}
