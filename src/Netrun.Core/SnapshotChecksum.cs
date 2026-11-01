using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Netrun.Core;

/// <summary>
/// Вычисляет межъязыковой SHA-256 упорядоченного потока chunks без зависимости от JSON.
/// Идентификатор нужен для проверки принадлежности chunks; в digest он намеренно не включается.
/// </summary>
public static class SnapshotChecksum
{
    /// <summary>
    /// Последовательно хэширует chunks с непрерывными индексами от 1; пустой поток допустим.
    /// Не сохраняет записи или их коллекции; перечисление и содержимое chunks должны быть стабильны до возврата.
    /// </summary>
    /// <param name="chunks">Chunks только с полными фактами узлов и tombstones, не более 128 записей в каждом.</param>
    /// <returns>64 lowercase hex символа SHA-256 canonical потока, включая domain prefix.</returns>
    /// <exception cref="ArgumentNullException">Коллекция chunks не задана.</exception>
    /// <exception cref="ArgumentException">Нарушен порядок chunks или допустимый контракт фактов.</exception>
    public static string Compute(IEnumerable<SnapshotChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        using var accumulator = new SnapshotChecksumAccumulator();
        foreach (var chunk in chunks)
            if (!accumulator.TryAppend(chunk))
                throw new ArgumentException("Invalid snapshot chunks.", nameof(chunks));
        return accumulator.GetDigest();
    }
}

/// <summary>
/// Инкрементальный checksum одного снимка. Хранит SHA-256, счётчики и ограниченный ID;
/// до hashing полностью валидирует отдельный ограниченный chunk.
/// </summary>
internal sealed class SnapshotChecksumAccumulator : IDisposable
{
    /// <summary>Строгий UTF-8 исключает изменение идентичности при непарных surrogate.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Состояние SHA-256; входные chunks и пользовательские строки здесь не удерживаются.</summary>
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>ID первого принятого chunk: запрещает смешивать снимки, но не входит в canonical digest.</summary>
    private string? _snapshotId;

    /// <summary>Запрещает чтение и добавление после освобождения криптографического состояния.</summary>
    private bool _disposed;

    /// <summary>Число успешно добавленных chunks, одновременно последний непрерывный chunk_index.</summary>
    public ulong ChunkCount { get; private set; }

    /// <summary>Количество всех экспортированных записей, включая повторные версии и tombstones.</summary>
    public ulong RecordCount { get; private set; }

    /// <summary>Начинает SHA-256 с фиксированного domain prefix, включая завершающий нулевой байт.</summary>
    public SnapshotChecksumAccumulator() => _hash.AppendData("NETRUN-SNAPSHOT-1\0"u8);

    /// <summary>
    /// Добавляет chunk только после полной проверки: отказ не меняет digest и счётчики.
    /// Результат нормализации временно занимает O(число записей chunk), а не размер всего снимка.
    /// </summary>
    public bool TryAppend(SnapshotChunk chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (chunk is null || !NodeFactRules.IsValidText(chunk.SnapshotId, 128) ||
            chunk.Changes is null || chunk.Changes.Count > 128 || ChunkCount == ulong.MaxValue ||
            chunk.ChunkIndex != ChunkCount + 1 || (ulong)chunk.Changes.Count > ulong.MaxValue - RecordCount ||
            _snapshotId is not null && chunk.SnapshotId != _snapshotId)
            return false;

        var mutations = new NodeMutation[chunk.Changes.Count];
        for (var i = 0; i < mutations.Length; i++)
        {
            var mutation = chunk.Changes[i] switch
            {
                NodeUpsert { Node: { } node } => new NodeMutation(node.NodeId, node.Generation, node.Revision, node),
                NodeRemove remove => new NodeMutation(remove.NodeId, remove.Generation, remove.Revision, null),
                _ => null
            };
            if (!NodeFactRules.IsValidMutation(mutation))
                return false;
            mutations[i] = mutation!;
        }

        AppendUInt64(chunk.ChunkIndex);
        AppendUInt32((uint)mutations.Length);
        foreach (var mutation in mutations)
        {
            AppendByte(mutation.Fact is null ? (byte)2 : (byte)1);
            AppendString(mutation.NodeId);
            AppendUInt64(mutation.Generation);
            AppendUInt64(mutation.Revision);
            if (mutation.Fact is not { } node)
                continue;
            AppendString(node.State.Address);
            AppendString(node.State.NodeType);
            AppendInt32(node.State.Position.X);
            AppendInt32(node.State.Position.Y);
            AppendInt32(node.State.Position.Z);
            AppendString(node.State.SectorId);
            AppendByte(node.State.Online ? (byte)1 : (byte)0);
        }
        _snapshotId ??= chunk.SnapshotId;
        ChunkCount++;
        RecordCount += (ulong)mutations.Length;
        return true;
    }

    /// <summary>Читает текущий digest, не сбрасывая SHA-256; последующие chunks продолжают тот же поток.</summary>
    public string GetDigest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToHexStringLower(_hash.GetCurrentHash());
    }

    /// <summary>Освобождает SHA-256 один раз; владение входными коллекциями никогда не принималось.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _hash.Dispose();
        _disposed = true;
    }

    /// <summary>Пишет длину в байтах UTF-8 как uint32 big endian, затем строгие байты без BOM.</summary>
    private void AppendString(string text)
    {
        var bytes = StrictUtf8.GetBytes(text);
        AppendUInt32((uint)bytes.Length);
        _hash.AppendData(bytes);
    }

    /// <summary>Пишет тег вида записи или boolean ровно одним байтом.</summary>
    private void AppendByte(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        _hash.AppendData(buffer);
    }

    /// <summary>Пишет счётчик поколения, ревизии или chunk_index без потери точности, big endian.</summary>
    private void AppendUInt64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        _hash.AppendData(buffer);
    }

    /// <summary>Пишет количество записей или длину строки как uint32 big endian.</summary>
    private void AppendUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _hash.AppendData(buffer);
    }

    /// <summary>Пишет координату тайла как signed int32 big endian; диапазон проверен до hashing.</summary>
    private void AppendInt32(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _hash.AppendData(buffer);
    }
}
