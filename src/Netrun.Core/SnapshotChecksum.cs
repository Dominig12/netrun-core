using System.Buffers.Binary;
using System.Globalization;
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
    /// <param name="formatVersion">1 сохраняет исходные golden vectors, 2 включает catalog без communications, 3 подтверждает точные raw JSON bytes через WireDigest.</param>
    public static string Compute(IEnumerable<SnapshotChunk> chunks, int formatVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        using var accumulator = new SnapshotChecksumAccumulator(formatVersion);
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
    /// <summary>Версия canonical stream фиксируется в begin, не меняется после первого chunk.</summary>
    private readonly int _formatVersion;
    /// <summary>Строковый chain версии 3 совместим с rust_g hash_string без передачи бинарного NUL через DM ABI.</summary>
    private string _wireChain = Convert.ToHexStringLower(SHA256.HashData("NETRUN-SNAPSHOT-3"u8));

    /// <summary>Число успешно добавленных chunks, одновременно последний непрерывный chunk_index.</summary>
    public ulong ChunkCount { get; private set; }

    /// <summary>Количество всех экспортированных записей, включая повторные версии и tombstones.</summary>
    public ulong RecordCount { get; private set; }

    /// <summary>Начинает SHA-256 с фиксированного domain prefix, включая завершающий нулевой байт.</summary>
    public SnapshotChecksumAccumulator(int formatVersion = 1)
    {
        if (formatVersion is not (1 or 2 or 3))
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        _formatVersion = formatVersion;
        _hash.AppendData(formatVersion == 1 ? "NETRUN-SNAPSHOT-1\0"u8 : "NETRUN-SNAPSHOT-2\0"u8);
    }

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

        var mutations = new NodeMutation?[chunk.Changes.Count];
        for (var i = 0; i < mutations.Length; i++)
        {
            if (!FactValidation.IsValid(chunk.Changes[i]) || _formatVersion == 1 && FactValidation.RequiresExtendedFormat(chunk.Changes[i]) ||
                _formatVersion < 3 && FactValidation.RequiresChecksum3(chunk.Changes[i]))
                return false;
            var mutation = chunk.Changes[i] switch
            {
                NodeUpsert { Node: { } node } => new NodeMutation(node.NodeId, node.Generation, node.Revision, node),
                NodeRemove remove => new NodeMutation(remove.NodeId, remove.Generation, remove.Revision, null),
                _ => null
            };
            mutations[i] = mutation;
        }

        if (_formatVersion == 3)
        {
            if (chunk.WireDigest is not { Length: 64 } digest || !digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
            var value = _wireChain + "|" + chunk.ChunkIndex.ToString(CultureInfo.InvariantCulture) + "|" +
                mutations.Length.ToString(CultureInfo.InvariantCulture) + "|" + digest;
            _wireChain = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            _snapshotId ??= chunk.SnapshotId;
            ChunkCount++;
            RecordCount += (ulong)mutations.Length;
            return true;
        }

        AppendUInt64(chunk.ChunkIndex);
        AppendUInt32((uint)mutations.Length);
        for (var i = 0; i < mutations.Length; i++)
        {
            if (mutations[i] is not { } mutation)
            {
                AppendCatalog(chunk.Changes[i]);
                continue;
            }
            AppendByte(mutation.Fact is null ? (byte)2 : (byte)1);
            AppendString(mutation.NodeId);
            AppendUInt64(mutation.Generation);
            AppendUInt64(mutation.Revision);
            if (mutation.Fact is not { } node)
                continue;
            AppendString(node.State.Address);
            AppendString(node.State.NodeType);
            if (_formatVersion == 2)
                AppendByte(node.State.Position is null ? (byte)0 : (byte)1);
            if (node.State.Position is { } position)
            {
                AppendInt32(position.X);
                AppendInt32(position.Y);
                AppendInt32(position.Z);
            }
            if (_formatVersion == 2)
                AppendOptional(node.State.SectorId);
            else
                AppendString(node.State.SectorId!);
            AppendByte(node.State.Online ? (byte)1 : (byte)0);
            if (_formatVersion == 2)
                AppendNetwork(node.State.Network);
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
        return _formatVersion == 3 ? _wireChain : Convert.ToHexStringLower(_hash.GetCurrentHash());
    }

    /// <summary>Освобождает SHA-256 один раз; владение входными коллекциями никогда не принималось.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _hash.Dispose();
        _disposed = true;
    }

    /// <summary>Полная metadata в формате 2; отсутствие поля отделено presence byte.</summary>
    private void AppendNetwork(NodeNetworkFacts? network)
    {
        AppendByte(network is null ? (byte)0 : (byte)1);
        if (network is null)
            return;
        AppendOptional(network.BaseAreaId);
        AppendOptional(network.SuppliedAreaId);
        AppendString(network.Department);
        AppendByte(network.IsNtnetRelay ? (byte)1 : (byte)0);
        AppendByte(network.RelayOperational ? (byte)1 : (byte)0);
        AppendByte(network.IsStationLevel ? (byte)1 : (byte)0);
        AppendByte(network.IsMiningLevel ? (byte)1 : (byte)0);
        AppendByte(network.InitialAnchor is null ? (byte)0 : (byte)1);
        if (network.InitialAnchor is { } anchor)
        {
            AppendString(anchor.NodeId);
            AppendUInt64(anchor.Generation);
        }
    }

    /// <summary>Кодирует секторные и сервисные факты отдельными тегами без JSON и native-ссылок.</summary>
    private void AppendCatalog(WorldChange change)
    {
        switch (change)
        {
            case SectorUpsert upsert:
                var sector = upsert.Sector;
                AppendIdentity(8, sector.SectorId, sector.Generation, sector.Revision);
                AppendUInt32((uint)sector.Levels.Count);
                foreach (var level in sector.Levels)
                    AppendInt32(level);
                break;
            case SectorRemove remove:
                AppendIdentity(9, remove.SectorId, remove.Generation, remove.Revision);
                break;
            case ServiceUpsert upsert:
                var service = upsert.Service;
                AppendIdentity(10, service.ServiceId, service.Generation, service.Revision);
                AppendString(service.Owner.NodeId);
                AppendUInt64(service.Owner.Generation);
                AppendString(service.Family);
                AppendOptional(service.Tag);
                AppendByte(service.Online ? (byte)1 : (byte)0);
                AppendUInt32((uint)service.Operations.Count);
                foreach (var operation in service.Operations)
                {
                    AppendString(operation.OperationId);
                    AppendByte((byte)operation.Safety);
                    AppendByte((byte)operation.Delivery);
                    AppendByte(operation.Ordered ? (byte)1 : (byte)0);
                }
                break;
            case ServiceRemove remove:
                AppendIdentity(11, remove.ServiceId, remove.Generation, remove.Revision);
                break;
            default:
                throw new InvalidOperationException("Validated fact is unsupported.");
        }
    }

    /// <summary>Заголовок нового вида факта в canonical формате 2.</summary>
    private void AppendIdentity(byte kind, string id, ulong generation, ulong revision)
    {
        AppendByte(kind);
        AppendString(id);
        AppendUInt64(generation);
        AppendUInt64(revision);
    }

    /// <summary>Nullable строка: presence byte, затем обычная length-prefixed UTF-8 строка.</summary>
    private void AppendOptional(string? text)
    {
        AppendByte(text is null ? (byte)0 : (byte)1);
        if (text is not null)
            AppendString(text);
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
