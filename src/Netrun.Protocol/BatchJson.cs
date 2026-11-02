using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Communication;

namespace Netrun.Protocol;

/// <summary>
/// Строгий JSON-кодек схем v1.0–v1.3 для фактов мира и синхронизации снимка.
/// Изолирует wire-формат от доменных записей Core и отвергает неизвестные поля и виды изменений.
/// Не применяет изменения к миру и не исполняет объявленные операции сервисов.
/// </summary>
public static class BatchJson
{
    /// <summary>
    /// Максимум упорядоченных изменений в одном пакете; ограничивает объём валидации и атомарного применения.
    /// </summary>
    public const int MaxEntries = 128;

    /// <summary>
    /// Предельная глубина JSON до проверки схемы; защищает парсер от чрезмерно вложенного входа.
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// Кодировка без BOM с отказом на повреждённый UTF-8 и непарные UTF-16 surrogates.
    /// Исключает молчаливую замену символов в идентификаторах при преобразовании в доменную модель.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Полностью проверяет одно JSON-тело и создаёт независимый пакет без ссылок на временный JSON-документ.
    /// Порядок entries сохраняется; счётчики читаются из канонических десятичных строк с явным правилом нуля.
    /// </summary>
    /// <param name="body">От 1 до 1 MiB байт UTF-8 без заголовка кадра; входная память не сохраняется.</param>
    /// <returns>Пакет с логическим временем в целых децисекундах и независимыми read-only списками изменений.</returns>
    /// <exception cref="ProtocolException">
    /// Безопасный код ошибки размера, UTF-8, JSON, версии, вида записи или схемы; содержимое входа не включается.
    /// </exception>
    public static InputBatch Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length is 0 or > FramedStream.MaxFrameBytes)
            throw new ProtocolException("frame_size");
        try
        {
            // До разбора запрещаем парсеру молчаливо исправлять повреждённый wire-текст.
            _ = StrictUtf8.GetCharCount(body);
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
            var root = document.RootElement;
            Fields(root, "protocol_major", "protocol_minor", "world_epoch", "engine_epoch", "binding_epoch",
                "sequence", "simulation_time_ds", "entries");
            var minor = Integer(root.GetProperty("protocol_minor"), 0, int.MaxValue);
            if (Integer(root.GetProperty("protocol_major"), 0, int.MaxValue) != 1 || minor > 3)
                throw new ProtocolException("protocol_version");
            var epochs = new Epochs(Text(root.GetProperty("world_epoch"), 128),
                Text(root.GetProperty("engine_epoch"), 128), Text(root.GetProperty("binding_epoch"), 128));
            var sequence = Counter(root.GetProperty("sequence"));
            var time = Integer(root.GetProperty("simulation_time_ds"), 0, int.MaxValue);
            return new InputBatch(epochs, sequence, time, ReadChanges(root.GetProperty("entries"), minor));
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException("invalid_utf8");
        }
        catch (JsonException)
        {
            throw new ProtocolException("invalid_json");
        }
        catch (InvalidOperationException)
        {
            throw Schema();
        }
    }

    /// <summary>
    /// Проверяет доменный пакет и кодирует тело с фиксированными именами полей и порядком entries.
    /// Выбирает минимальный minor: 0 для исходных узлов, 1 для sync, 2 для полных фактов,
    /// 3 для native communications metadata и raw-wire checksum.
    /// Не добавляет заголовок кадра и не меняет пакет; возвращённый массив принадлежит вызывающей стороне.
    /// </summary>
    /// <param name="batch">Полный пакет; коллекция изменений должна оставаться стабильной до завершения вызова.</param>
    /// <returns>Новое JSON-тело UTF-8, пригодное для FramedStream.WriteAsync.</returns>
    /// <exception cref="ProtocolException">schema_invalid для недопустимой модели; frame_size при превышении лимита.</exception>
    public static byte[] Encode(InputBatch batch) => Encode(batch, 0);

    /// <summary>Кодирует факты в общей версии runtime; minimumMinor не может понизить требуемую записью схему.</summary>
    public static byte[] Encode(InputBatch batch, int minimumMinor)
    {
        if (minimumMinor is < 0 or > 3)
            throw new ProtocolException("protocol_version");
        var minor = Math.Max(Validate(batch), minimumMinor);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol_major", 1);
            writer.WriteNumber("protocol_minor", minor);
            writer.WriteString("world_epoch", batch.Epochs.World);
            writer.WriteString("engine_epoch", batch.Epochs.Engine);
            writer.WriteString("binding_epoch", batch.Epochs.Binding);
            writer.WriteString("sequence", Decimal(batch.Sequence));
            writer.WriteNumber("simulation_time_ds", batch.SimulationTimeDs);
            writer.WriteStartArray("entries");
            foreach (var change in batch.Entries)
                WriteChange(writer, change, minor);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.Length > FramedStream.MaxFrameBytes)
            throw new ProtocolException("frame_size");
        return buffer.ToArray();
    }

    /// <summary>
    /// Читает ограниченный упорядоченный массив; factsOnly запрещает controls во вложенных changes.
    /// Каждый вызов создаёт собственный список, не удерживающий JsonDocument после Decode.
    /// </summary>
    private static IReadOnlyList<WorldChange> ReadChanges(JsonElement entries, int minor, bool factsOnly = false)
    {
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaxEntries)
            throw Schema();
        var changes = new List<WorldChange>(entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
            changes.Add(ReadChange(entry, minor, factsOnly));
        return changes.AsReadOnly();
    }

    /// <summary>
    /// Разбирает один allowlisted вид записи с точным набором полей.
    /// Sync доступен начиная с minor 1 только на верхнем уровне; catalog-факты требуют minor 2.
    /// </summary>
    private static WorldChange ReadChange(JsonElement entry, int minor, bool factsOnly)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("kind", out var kindValue))
            throw Schema();
        var kind = Text(kindValue, 128);
        if (kind is "snapshot_begin" or "snapshot_chunk" or "snapshot_commit" or "catch_up_delta" or "catch_up_complete")
        {
            if (factsOnly)
                throw Schema();
            if (minor == 0)
                throw new ProtocolException("protocol_version");
        }
        if (minor < 2 && kind is "sector_upsert" or "sector_remove" or "service_upsert" or "service_remove")
            throw new ProtocolException("protocol_version");
        switch (kind)
        {
            case "node_upsert":
                Fields(entry, "kind", "node_id", "generation", "node_revision", "state");
                return CheckedFact(new NodeUpsert(new NodeFact(Text(entry.GetProperty("node_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("node_revision")),
                    ReadState(entry.GetProperty("state"), minor))));
            case "node_remove":
                Fields(entry, "kind", "node_id", "generation", "node_revision");
                return CheckedFact(new NodeRemove(Text(entry.GetProperty("node_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("node_revision"))));
            case "sector_upsert":
                Fields(entry, "kind", "sector_id", "generation", "sector_revision", "levels");
                return CheckedFact(new SectorUpsert(new SectorFact(Text(entry.GetProperty("sector_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("sector_revision")),
                    ReadLevels(entry.GetProperty("levels")))));
            case "sector_remove":
                Fields(entry, "kind", "sector_id", "generation", "sector_revision");
                return CheckedFact(new SectorRemove(Text(entry.GetProperty("sector_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("sector_revision"))));
            case "service_upsert":
                if (minor >= 3)
                    FieldsWithOptional(entry, ["kind", "service_id", "generation", "service_revision", "owner", "family", "tag", "online", "operations"], "communication");
                else
                    Fields(entry, "kind", "service_id", "generation", "service_revision", "owner", "family", "tag", "online", "operations");
                var serviceOwner = ReadNodeKey(entry.GetProperty("owner"));
                return CheckedFact(new ServiceUpsert(new ServiceFact(Text(entry.GetProperty("service_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("service_revision")),
                    serviceOwner, Text(entry.GetProperty("family"), 128),
                    NullableText(entry.GetProperty("tag"), 256), Boolean(entry.GetProperty("online")),
                    ReadOperations(entry.GetProperty("operations")),
                    entry.TryGetProperty("communication", out var communication) ? ReadCommunication(communication, serviceOwner) : null)));
            case "service_remove":
                Fields(entry, "kind", "service_id", "generation", "service_revision");
                return CheckedFact(new ServiceRemove(Text(entry.GetProperty("service_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("service_revision"))));
            case "snapshot_begin":
                if (minor >= 2)
                    Fields(entry, "kind", "snapshot_id", "journal_sequence", "checksum_version");
                else
                    Fields(entry, "kind", "snapshot_id", "journal_sequence");
                return new SnapshotBegin(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("journal_sequence"), allowZero: true),
                    minor >= 2 ? Integer(entry.GetProperty("checksum_version"), 1, minor >= 3 ? 3 : 2) : 1);
            case "snapshot_chunk":
                Fields(entry, "kind", "snapshot_id", "chunk_index", "changes");
                return new SnapshotChunk(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("chunk_index")), ReadChanges(entry.GetProperty("changes"), minor, factsOnly: true),
                    minor >= 3 ? Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(entry.GetProperty("changes").GetRawText()))) : null);
            case "snapshot_commit":
                Fields(entry, "kind", "snapshot_id", "chunk_count", "record_count", "sha256");
                var hash = Text(entry.GetProperty("sha256"), 64);
                ValidateHash(hash);
                return new SnapshotCommit(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("chunk_count"), allowZero: true),
                    Counter(entry.GetProperty("record_count"), allowZero: true), hash);
            case "catch_up_delta":
                Fields(entry, "kind", "snapshot_id", "journal_sequence", "changes");
                return new CatchUpDelta(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("journal_sequence"), allowZero: true),
                    ReadChanges(entry.GetProperty("changes"), minor, factsOnly: true));
            case "catch_up_complete":
                Fields(entry, "kind", "snapshot_id", "through_journal_sequence");
                return new CatchUpComplete(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("through_journal_sequence"), allowZero: true));
            default:
                throw new ProtocolException("entry_kind");
        }
    }

    /// <summary>
    /// Пишет одну проверенную запись; minor задаёт точную форму состояния и metadata в общей оболочке.
    /// После каждой записи проверяет накопленный размер, прекращая построение слишком большого кадра.
    /// </summary>
    private static void WriteChange(Utf8JsonWriter writer, WorldChange change, int minor)
    {
        writer.WriteStartObject();
        switch (change)
        {
            case NodeUpsert upsert:
                var node = upsert.Node;
                writer.WriteString("kind", "node_upsert");
                WriteIdentity(writer, node.NodeId, node.Generation, node.Revision);
                WriteState(writer, node.State, minor);
                break;
            case NodeRemove remove:
                writer.WriteString("kind", "node_remove");
                WriteIdentity(writer, remove.NodeId, remove.Generation, remove.Revision);
                break;
            case SectorUpsert sectorUpsert:
                writer.WriteString("kind", "sector_upsert");
                WriteCatalogIdentity(writer, "sector", sectorUpsert.Sector.SectorId, sectorUpsert.Sector.Generation, sectorUpsert.Sector.Revision);
                writer.WriteStartArray("levels");
                foreach (var level in sectorUpsert.Sector.Levels)
                {
                    writer.WriteNumberValue(level);
                    CheckSize(writer);
                }
                writer.WriteEndArray();
                break;
            case SectorRemove sectorRemove:
                writer.WriteString("kind", "sector_remove");
                WriteCatalogIdentity(writer, "sector", sectorRemove.SectorId, sectorRemove.Generation, sectorRemove.Revision);
                break;
            case ServiceUpsert serviceUpsert:
                var service = serviceUpsert.Service;
                writer.WriteString("kind", "service_upsert");
                WriteCatalogIdentity(writer, "service", service.ServiceId, service.Generation, service.Revision);
                WriteNodeKey(writer, "owner", service.Owner);
                writer.WriteString("family", service.Family);
                writer.WriteString("tag", service.Tag);
                writer.WriteBoolean("online", service.Online);
                WriteOperations(writer, service.Operations);
                if (service.Communication is { } communicationPort)
                    WriteCommunication(writer, communicationPort);
                break;
            case ServiceRemove serviceRemove:
                writer.WriteString("kind", "service_remove");
                WriteCatalogIdentity(writer, "service", serviceRemove.ServiceId, serviceRemove.Generation, serviceRemove.Revision);
                break;
            case SnapshotBegin begin:
                WriteSnapshotIdentity(writer, "snapshot_begin", begin.SnapshotId);
                writer.WriteString("journal_sequence", Decimal(begin.JournalSequence));
                if (minor >= 2)
                    writer.WriteNumber("checksum_version", begin.ChecksumVersion);
                break;
            case SnapshotChunk chunk:
                WriteSnapshotIdentity(writer, "snapshot_chunk", chunk.SnapshotId);
                writer.WriteString("chunk_index", Decimal(chunk.ChunkIndex));
                WriteChanges(writer, chunk.Changes, minor);
                break;
            case SnapshotCommit commit:
                WriteSnapshotIdentity(writer, "snapshot_commit", commit.SnapshotId);
                writer.WriteString("chunk_count", Decimal(commit.ChunkCount));
                writer.WriteString("record_count", Decimal(commit.RecordCount));
                writer.WriteString("sha256", commit.Sha256);
                break;
            case CatchUpDelta delta:
                WriteSnapshotIdentity(writer, "catch_up_delta", delta.SnapshotId);
                writer.WriteString("journal_sequence", Decimal(delta.JournalSequence));
                WriteChanges(writer, delta.Changes, minor);
                break;
            case CatchUpComplete complete:
                WriteSnapshotIdentity(writer, "catch_up_complete", complete.SnapshotId);
                writer.WriteString("through_journal_sequence", Decimal(complete.ThroughJournalSequence));
                break;
            default:
                throw Schema();
        }
        writer.WriteEndObject();
        CheckSize(writer);
    }

    /// <summary>Пишет общие kind и snapshot_id, не закрывая заимствованный JSON writer.</summary>
    private static void WriteSnapshotIdentity(Utf8JsonWriter writer, string kind, string snapshotId)
    {
        writer.WriteString("kind", kind);
        writer.WriteString("snapshot_id", snapshotId);
    }

    /// <summary>Пишет упорядоченные facts-only changes; контроль размера выполняется после каждого факта.</summary>
    private static void WriteChanges(Utf8JsonWriter writer, IReadOnlyList<WorldChange> changes, int minor)
    {
        writer.WriteStartArray("changes");
        foreach (var change in changes)
            WriteChange(writer, change, minor);
        writer.WriteEndArray();
    }

    /// <summary>
    /// Читает точную версионную форму состояния; только minor 2 допускает отсутствие физического положения.
    /// Nullable-поля должны присутствовать явно; отсутствие свойства не подменяется null.
    /// </summary>
    private static NodeState ReadState(JsonElement state, int minor)
    {
        if (minor >= 2)
            Fields(state, "address", "node_type", "position", "sector_id", "online", "network");
        else
            Fields(state, "address", "node_type", "position", "sector_id", "online");
        var position = state.GetProperty("position");
        Position? coordinates = null;
        if (minor < 2 || position.ValueKind != JsonValueKind.Null)
        {
            Fields(position, "x", "y", "z");
            coordinates = new Position(Integer(position.GetProperty("x"), 1, 32_767),
                Integer(position.GetProperty("y"), 1, 32_767), Integer(position.GetProperty("z"), 1, 4_096));
        }
        return new NodeState(Text(state.GetProperty("address"), 256), Text(state.GetProperty("node_type"), 256),
            coordinates, minor >= 2 ? NullableText(state.GetProperty("sector_id"), 256) : Text(state.GetProperty("sector_id"), 256),
            Boolean(state.GetProperty("online")), minor >= 2 ? ReadNetwork(state.GetProperty("network")) : null);
    }

    /// <summary>Читает полные исходные network-факты; null означает отсутствие расширения, а не default patch.</summary>
    private static NodeNetworkFacts? ReadNetwork(JsonElement network)
    {
        if (network.ValueKind == JsonValueKind.Null)
            return null;
        Fields(network, "base_area_id", "supplied_area_id", "department", "is_ntnet_relay", "relay_operational",
            "is_station_level", "is_mining_level", "initial_anchor");
        var anchor = network.GetProperty("initial_anchor");
        return new NodeNetworkFacts(NullableText(network.GetProperty("base_area_id"), 128),
            NullableText(network.GetProperty("supplied_area_id"), 128), Text(network.GetProperty("department"), 128),
            Boolean(network.GetProperty("is_ntnet_relay")), Boolean(network.GetProperty("relay_operational")),
            Boolean(network.GetProperty("is_station_level")), Boolean(network.GetProperty("is_mining_level")),
            anchor.ValueKind == JsonValueKind.Null ? null : ReadNodeKey(anchor));
    }

    /// <summary>Читает идентичность узла без потери поколения и без преобразования числового JSON в строку.</summary>
    private static NodeKey ReadNodeKey(JsonElement value)
    {
        Fields(value, "node_id", "generation");
        return new NodeKey(Text(value.GetProperty("node_id"), 128), Counter(value.GetProperty("generation")));
    }

    /// <summary>Ограничивает native-порядок этажей до allocation; общая доменная проверка отвергнет дубли.</summary>
    private static ValueList<int> ReadLevels(JsonElement levels)
    {
        if (levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() is < 1 or > 4096)
            throw Schema();
        var copy = new List<int>(levels.GetArrayLength());
        foreach (var level in levels.EnumerateArray())
            copy.Add(Integer(level, 1, 4096));
        return new ValueList<int>(copy, 4096);
    }

    /// <summary>Читает ограниченный allowlist операций; opaque ID не превращается в имя исполняемого proc.</summary>
    private static ValueList<OperationContract> ReadOperations(JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > 64)
            throw Schema();
        var copy = new List<OperationContract>(operations.GetArrayLength());
        foreach (var operation in operations.EnumerateArray())
        {
            Fields(operation, "operation_id", "safety", "delivery", "ordered");
            var safety = Text(operation.GetProperty("safety"), 32) switch
            {
                "read_only" => OperationSafety.ReadOnly,
                "idempotent" => OperationSafety.Idempotent,
                "mutating" => OperationSafety.Mutating,
                _ => throw Schema()
            };
            var delivery = Text(operation.GetProperty("delivery"), 32) switch
            {
                "delivery_only" => DeliverySemantics.DeliveryOnly,
                "request_reply" => DeliverySemantics.RequestReply,
                _ => throw Schema()
            };
            copy.Add(new OperationContract(Text(operation.GetProperty("operation_id"), 128), safety, delivery,
                Boolean(operation.GetProperty("ordered"))));
        }
        return new ValueList<OperationContract>(copy, 64);
    }

    /// <summary>Читает raw native port только из minor 3; owner наследуется сервисом и не может подменяться отдельным полем.</summary>
    private static CommunicationPortFact? ReadCommunication(JsonElement value, NodeKey owner)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        Fields(value, "kind", "on", "toggled", "operational", "native_z", "long_range", "receiving", "broadcasting",
            "listening_frequencies", "configured_links");
        var kind = Text(value.GetProperty("kind"), 16) switch
        {
            "hub" => CommunicationPortKind.Hub,
            "relay" => CommunicationPortKind.Relay,
            _ => throw Schema()
        };
        var frequencies = value.GetProperty("listening_frequencies");
        var links = value.GetProperty("configured_links");
        if (frequencies.ValueKind != JsonValueKind.Array || frequencies.GetArrayLength() > 128 ||
            links.ValueKind != JsonValueKind.Array || links.GetArrayLength() > 128)
            throw Schema();
        var port = new CommunicationPortFact(owner, kind, Boolean(value.GetProperty("on")), Boolean(value.GetProperty("toggled")),
            Boolean(value.GetProperty("operational")), Integer(value.GetProperty("native_z"), 0, 4096),
            Boolean(value.GetProperty("long_range")), Boolean(value.GetProperty("receiving")), Boolean(value.GetProperty("broadcasting")),
            new ValueList<int>(frequencies.EnumerateArray().Select(frequency => Integer(frequency, 1, int.MaxValue)), 128),
            new ValueList<NodeKey>(links.EnumerateArray().Select(ReadNodeKey), 128));
        return CommunicationPortCatalog.IsValid(port) ? port : throw Schema();
    }

    /// <summary>Пишет optional minor 3 metadata без owner field и без рассчитанных маршрутов.</summary>
    private static void WriteCommunication(Utf8JsonWriter writer, CommunicationPortFact port)
    {
        writer.WriteStartObject("communication");
        writer.WriteString("kind", port.Kind == CommunicationPortKind.Hub ? "hub" : "relay");
        writer.WriteBoolean("on", port.On);
        writer.WriteBoolean("toggled", port.Toggled);
        writer.WriteBoolean("operational", port.Operational);
        writer.WriteNumber("native_z", port.NativeZ);
        writer.WriteBoolean("long_range", port.LongRange);
        writer.WriteBoolean("receiving", port.Receiving);
        writer.WriteBoolean("broadcasting", port.Broadcasting);
        writer.WriteStartArray("listening_frequencies");
        foreach (var frequency in port.ListeningFrequencies)
            writer.WriteNumberValue(frequency);
        writer.WriteEndArray();
        writer.WriteStartArray("configured_links");
        foreach (var link in port.ConfiguredDirectedLinks)
        {
            writer.WriteStartObject();
            writer.WriteString("node_id", link.NodeId);
            writer.WriteString("generation", Decimal(link.Generation));
            writer.WriteEndObject();
            CheckSize(writer);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Применяет общую доменную валидацию к полностью прочитанному факту до возврата caller.</summary>
    private static WorldChange CheckedFact(WorldChange change) => FactValidation.IsValid(change) ? change : throw Schema();

    /// <summary>Читает только явные JSON true/false; числа и строки не приводятся к boolean.</summary>
    private static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Schema()
    };

    /// <summary>Читает обязательное nullable-свойство; non-null содержимое сохраняет правила идентификатора.</summary>
    private static string? NullableText(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.Null ? null : Text(value, maximum);

    /// <summary>Пишет полное состояние узла, сохраняя точные старые поля для minor 0/1.</summary>
    private static void WriteState(Utf8JsonWriter writer, NodeState state, int minor)
    {
        writer.WriteStartObject("state");
        writer.WriteString("address", state.Address);
        writer.WriteString("node_type", state.NodeType);
        if (state.Position is { } position)
        {
            writer.WriteStartObject("position");
            writer.WriteNumber("x", position.X);
            writer.WriteNumber("y", position.Y);
            writer.WriteNumber("z", position.Z);
            writer.WriteEndObject();
        }
        else
            writer.WriteNull("position");
        writer.WriteString("sector_id", state.SectorId);
        writer.WriteBoolean("online", state.Online);
        if (minor >= 2)
            WriteNetwork(writer, state.Network);
        writer.WriteEndObject();
    }

    /// <summary>Пишет все поля network даже для false/null, чтобы отсутствующие свойства не стали неявным patch.</summary>
    private static void WriteNetwork(Utf8JsonWriter writer, NodeNetworkFacts? network)
    {
        if (network is null)
        {
            writer.WriteNull("network");
            return;
        }
        writer.WriteStartObject("network");
        writer.WriteString("base_area_id", network.BaseAreaId);
        writer.WriteString("supplied_area_id", network.SuppliedAreaId);
        writer.WriteString("department", network.Department);
        writer.WriteBoolean("is_ntnet_relay", network.IsNtnetRelay);
        writer.WriteBoolean("relay_operational", network.RelayOperational);
        writer.WriteBoolean("is_station_level", network.IsStationLevel);
        writer.WriteBoolean("is_mining_level", network.IsMiningLevel);
        if (network.InitialAnchor is { } anchor)
            WriteNodeKey(writer, "initial_anchor", anchor);
        else
            writer.WriteNull("initial_anchor");
        writer.WriteEndObject();
    }

    /// <summary>Пишет node reference со строковым положительным поколением.</summary>
    private static void WriteNodeKey(Utf8JsonWriter writer, string property, NodeKey key)
    {
        writer.WriteStartObject(property);
        writer.WriteString("node_id", key.NodeId);
        writer.WriteString("generation", Decimal(key.Generation));
        writer.WriteEndObject();
    }

    /// <summary>Пишет неизменяемый allowlist и проверяет накопленный размер после каждой операции.</summary>
    private static void WriteOperations(Utf8JsonWriter writer, ValueList<OperationContract> operations)
    {
        writer.WriteStartArray("operations");
        foreach (var operation in operations)
        {
            writer.WriteStartObject();
            writer.WriteString("operation_id", operation.OperationId);
            writer.WriteString("safety", operation.Safety switch
            {
                OperationSafety.ReadOnly => "read_only",
                OperationSafety.Idempotent => "idempotent",
                OperationSafety.Mutating => "mutating",
                _ => throw Schema()
            });
            writer.WriteString("delivery", operation.Delivery switch
            {
                DeliverySemantics.DeliveryOnly => "delivery_only",
                DeliverySemantics.RequestReply => "request_reply",
                _ => throw Schema()
            });
            writer.WriteBoolean("ordered", operation.Ordered);
            writer.WriteEndObject();
            CheckSize(writer);
        }
        writer.WriteEndArray();
    }

    /// <summary>Общий identity секторного/сервисного факта; prefix берётся только из allowlist codec.</summary>
    private static void WriteCatalogIdentity(Utf8JsonWriter writer, string prefix, string id, ulong generation, ulong revision)
    {
        writer.WriteString(prefix + "_id", id);
        writer.WriteString("generation", Decimal(generation));
        writer.WriteString(prefix + "_revision", Decimal(revision));
    }

    /// <summary>Прекращает рост JSON сразу после очередной ограниченной записи, не дожидаясь конца outer массива.</summary>
    private static void CheckSize(Utf8JsonWriter writer)
    {
        if (writer.BytesCommitted + writer.BytesPending > FramedStream.MaxFrameBytes)
            throw new ProtocolException("frame_size");
    }

    /// <summary>
    /// Проверяет точный набор обязательных свойств объекта за один обход, отвергая пропуски, повторы и расширения.
    /// Сравнение имён ordinal и регистрозависимое; ошибка возвращается без имени чужого свойства.
    /// </summary>
    private static void Fields(JsonElement value, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Schema();
        var remaining = new HashSet<string>(required, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name))
                throw Schema();
        if (remaining.Count != 0)
            throw Schema();
    }

    /// <summary>Явный optional allowlist для одной версии записи; остальные kinds сохраняют exact mandatory schema.</summary>
    private static void FieldsWithOptional(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Schema();
        var remaining = new HashSet<string>(required, StringComparer.Ordinal);
        var allowed = new HashSet<string>(required.Concat(optional), StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Remove(property.Name))
                throw Schema();
            remaining.Remove(property.Name);
        }
        if (remaining.Count != 0)
            throw Schema();
    }

    /// <summary>
    /// Извлекает только JSON-строку и передаёт её общей проверке текста; null и преобразования других типов запрещены.
    /// maximum задаётся в единицах UTF-16, как длина доменных строк C#.
    /// </summary>
    private static string Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Schema();
        var text = value.GetString();
        ValidateText(text, maximum);
        return text!;
    }

    /// <summary>
    /// Запрещает пустой/пробельный текст, управляющие символы, непарные surrogates и превышение maximum единиц UTF-16.
    /// Применяется одинаково к входному JSON и к записям, созданным потребителем библиотеки напрямую.
    /// </summary>
    private static void ValidateText(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw Schema();
        // Локально созданные записи также не должны менять identity при замене непарного surrogate.
        try { _ = StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw Schema(); }
    }

    /// <summary>
    /// Читает ulong только из строки длиной до 20 десятичных цифр ASCII; ноль разрешён лишь явным allowZero.
    /// Отвергает ведущие нули, знаки, пробелы, числовой JSON и переполнение без потери точности счётчика.
    /// </summary>
    private static ulong Counter(JsonElement value, bool allowZero = false)
    {
        var text = Text(value, 20);
        if (allowZero && text == "0")
            return 0;
        if (text[0] is < '1' or > '9' || text.Any(character => character is < '0' or > '9') ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw Schema();
        return result;
    }

    /// <summary>
    /// Читает JSON-целое Int32 внутри включительных границ; строки и дробные формы не приводятся к числу.
    /// Используется для версий, координат и логического времени в децисекундах.
    /// </summary>
    private static int Integer(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) ||
            result < minimum || result > maximum)
            throw Schema();
        return result;
    }

    /// <summary>
    /// Проверяет весь пакет перед кодированием, включая локально созданные null, неизвестные подтипы и диапазоны.
    /// Возвращает минимальную требуемую minor-версию. Каждый список ограничен MaxEntries; Core не изменяется.
    /// </summary>
    private static int Validate(InputBatch? batch)
    {
        if (batch?.Epochs is null || batch.Sequence == 0 || batch.SimulationTimeDs < 0 ||
            batch.Entries is null || batch.Entries.Count > MaxEntries)
            throw Schema();
        ValidateText(batch.Epochs.World, 128);
        ValidateText(batch.Epochs.Engine, 128);
        ValidateText(batch.Epochs.Binding, 128);
        var minor = 0;
        foreach (var change in batch.Entries)
            minor = Math.Max(minor, ValidateChange(change));
        return minor;
    }

    /// <summary>
    /// Проверяет локальную запись и возвращает минимальную minor-версию.
    /// factsOnly закрывает вложенность controls даже для модели, созданной без wire-кодека.
    /// </summary>
    private static int ValidateChange(WorldChange? change, bool factsOnly = false)
    {
        if (change is NodeUpsert or NodeRemove or SectorUpsert or SectorRemove or ServiceUpsert or ServiceRemove)
        {
            if (!FactValidation.IsValid(change))
                throw Schema();
            return FactValidation.RequiresChecksum3(change) ? 3 : FactValidation.RequiresExtendedFormat(change) ? 2 : 0;
        }
        switch (change)
        {
            case SnapshotBegin begin when !factsOnly:
                ValidateText(begin.SnapshotId, 128);
                if (begin.ChecksumVersion is < 1 or > 3)
                    throw Schema();
                return begin.ChecksumVersion >= 2 ? begin.ChecksumVersion : 1;
            case SnapshotChunk chunk when !factsOnly:
                ValidateText(chunk.SnapshotId, 128);
                if (chunk.ChunkIndex == 0)
                    throw Schema();
                if (chunk.WireDigest is not null)
                    ValidateHash(chunk.WireDigest);
                return Math.Max(chunk.WireDigest is null ? 1 : 3, ValidateFactChanges(chunk.Changes));
            case SnapshotCommit commit when !factsOnly:
                ValidateText(commit.SnapshotId, 128);
                ValidateHash(commit.Sha256);
                return 1;
            case CatchUpDelta delta when !factsOnly:
                ValidateText(delta.SnapshotId, 128);
                return Math.Max(1, ValidateFactChanges(delta.Changes));
            case CatchUpComplete complete when !factsOnly:
                ValidateText(complete.SnapshotId, 128);
                return 1;
            default:
                throw Schema();
        }
    }

    /// <summary>Проверяет ограниченный список штатных фактов и учитывает minor всех вложенных изменений.</summary>
    private static int ValidateFactChanges(IReadOnlyList<WorldChange>? changes)
    {
        if (changes is null || changes.Count > MaxEntries)
            throw Schema();
        var minor = 0;
        foreach (var change in changes)
            minor = Math.Max(minor, ValidateChange(change, factsOnly: true));
        return minor;
    }

    /// <summary>Принимает ровно 64 ASCII lowercase hex символа без нормализации или исправления checksum.</summary>
    private static void ValidateHash(string? hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Schema();
    }

    /// <summary>
    /// Пишет общий адресуемый identity узла в текущий JSON-объект; поколение и ревизия остаются строками без округления.
    /// writer заимствуется у Encode и здесь не закрывается.
    /// </summary>
    private static void WriteIdentity(Utf8JsonWriter writer, string id, ulong generation, ulong revision)
    {
        writer.WriteString("node_id", id);
        writer.WriteString("generation", Decimal(generation));
        writer.WriteString("node_revision", Decimal(revision));
    }

    /// <summary>Форматирует wire-счётчик десятичными цифрами независимо от региональных настроек процесса.</summary>
    private static string Decimal(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Создаёт единый безопасный отказ схемы, не сохраняя JSON, значения полей или внутреннее исключение.</summary>
    private static ProtocolException Schema() => new("schema_invalid");
}
