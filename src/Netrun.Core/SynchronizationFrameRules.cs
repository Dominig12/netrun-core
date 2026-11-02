using System.Security.Cryptography;
using System.Text;

namespace Netrun.Core;

/// <summary>Валидация полного control/data frame до изменения состояния синхронизации.</summary>
internal static class SynchronizationFrameRules
{
    /// <summary>Проверяет форму пакета и строит bounded semantic fingerprint; это внутренний, не wire-формат.</summary>
    internal static byte[]? Fingerprint(InputBatch batch, int maxEntries)
    {
        if (batch.Sequence == 0 || batch.SimulationTimeDs < 0 || batch.Entries is null || batch.Entries.Count > maxEntries)
            return null;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(batch.Epochs.World);
            writer.Write(batch.Epochs.Engine);
            writer.Write(batch.Epochs.Binding);
            writer.Write(batch.Sequence);
            writer.Write(batch.SimulationTimeDs);
            writer.Write(batch.Entries.Count);
            foreach (var change in batch.Entries)
            {
                if (change is NodeUpsert or NodeRemove or SectorUpsert or SectorRemove or ServiceUpsert or ServiceRemove)
                {
                    if (!WriteNode(writer, change))
                        return null;
                    continue;
                }
                if (batch.Entries.Count != 1)
                    return null;
                switch (change)
                {
                    case SnapshotBegin begin when ValidId(begin.SnapshotId) && begin.ChecksumVersion is 1 or 2 or 3:
                        writer.Write((byte)3);
                        writer.Write(begin.SnapshotId);
                        writer.Write(begin.JournalSequence);
                        writer.Write(begin.ChecksumVersion);
                        break;
                    case SnapshotChunk chunk when ValidId(chunk.SnapshotId) && chunk.ChunkIndex > 0:
                        writer.Write((byte)4);
                        writer.Write(chunk.SnapshotId);
                        writer.Write(chunk.ChunkIndex);
                        if (!WriteNodes(writer, chunk.Changes, maxEntries))
                            return null;
                        writer.Write(chunk.WireDigest is not null);
                        if (chunk.WireDigest is not null)
                        {
                            if (!ValidDigest(chunk.WireDigest))
                                return null;
                            writer.Write(chunk.WireDigest);
                        }
                        break;
                    case SnapshotCommit commit when ValidId(commit.SnapshotId) && ValidDigest(commit.Sha256):
                        writer.Write((byte)5);
                        writer.Write(commit.SnapshotId);
                        writer.Write(commit.ChunkCount);
                        writer.Write(commit.RecordCount);
                        writer.Write(commit.Sha256);
                        break;
                    case CatchUpDelta delta when ValidId(delta.SnapshotId):
                        writer.Write((byte)6);
                        writer.Write(delta.SnapshotId);
                        writer.Write(delta.JournalSequence);
                        if (!WriteNodes(writer, delta.Changes, maxEntries))
                            return null;
                        break;
                    case CatchUpComplete complete when ValidId(complete.SnapshotId):
                        writer.Write((byte)7);
                        writer.Write(complete.SnapshotId);
                        writer.Write(complete.ThroughJournalSequence);
                        break;
                    default:
                        return null;
                }
            }
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    /// <summary>Идентификатор снимка имеет те же ограничения, что и ID остальных доменных записей.</summary>
    private static bool ValidId(string? value) => NodeFactRules.IsValidText(value, 128);

    /// <summary>Checksum передаётся только в canonical lowercase hex, без альтернативных представлений.</summary>
    private static bool ValidDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>В control frame допускается ограниченный список фактов, без рекурсивных controls.</summary>
    private static bool WriteNodes(BinaryWriter writer, IReadOnlyList<WorldChange>? changes, int maximum)
    {
        if (changes is null || changes.Count > maximum)
            return false;
        writer.Write(changes.Count);
        foreach (var change in changes)
            if (!WriteNode(writer, change))
                return false;
        return true;
    }

    /// <summary>Валидирует полный узловой факт перед hashing; теги различают deletion и upsert.</summary>
    private static bool WriteNode(BinaryWriter writer, WorldChange? change)
    {
        if (!FactValidation.IsValid(change))
            return false;
        var mutation = change switch
        {
            NodeUpsert { Node: { } node } => new NodeMutation(node.NodeId, node.Generation, node.Revision, node),
            NodeRemove remove => new NodeMutation(remove.NodeId, remove.Generation, remove.Revision, null),
            _ => null
        };
        if (mutation is not null)
            WorldFactEncoding.WriteNode(writer, mutation);
        else
            WorldFactEncoding.WriteCatalog(writer, change!);
        return true;
    }
}
