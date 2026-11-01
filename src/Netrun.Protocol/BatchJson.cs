using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;

namespace Netrun.Protocol;

/// <summary>
/// Строгий JSON-кодек экспериментальных схем v1.0/v1.1 для изменений узлов и синхронизации снимка.
/// Изолирует wire-формат от доменных записей Core и отвергает неизвестные поля и виды изменений.
/// Не применяет изменения к миру и не предоставляет контракты ещё не реализованных сервисов.
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
            if (Integer(root.GetProperty("protocol_major"), 0, int.MaxValue) != 1 || minor > 1)
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
    /// Для записей синхронизации выбирает minor 1; обычные узловые пакеты сохраняют minor 0.
    /// Не добавляет заголовок кадра и не меняет пакет; возвращённый массив принадлежит вызывающей стороне.
    /// </summary>
    /// <param name="batch">Полный пакет; коллекция изменений должна оставаться стабильной до завершения вызова.</param>
    /// <returns>Новое JSON-тело UTF-8, пригодное для FramedStream.WriteAsync.</returns>
    /// <exception cref="ProtocolException">schema_invalid для недопустимой модели; frame_size при превышении лимита.</exception>
    public static byte[] Encode(InputBatch batch)
    {
        var minor = Validate(batch);
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
                WriteChange(writer, change);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.Length > FramedStream.MaxFrameBytes)
            throw new ProtocolException("frame_size");
        return buffer.ToArray();
    }

    /// <summary>
    /// Читает ограниченный упорядоченный массив; nodeOnly запрещает управляющие записи во вложенных changes.
    /// Каждый вызов создаёт собственный список, не удерживающий JsonDocument после Decode.
    /// </summary>
    private static IReadOnlyList<WorldChange> ReadChanges(JsonElement entries, int minor, bool nodeOnly = false)
    {
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaxEntries)
            throw Schema();
        var changes = new List<WorldChange>(entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
            changes.Add(ReadChange(entry, minor, nodeOnly));
        return changes.AsReadOnly();
    }

    /// <summary>
    /// Разбирает один allowlisted вид записи с точным набором полей.
    /// Sync доступен только в minor 1 и только на верхнем уровне; рекурсивного control nesting нет.
    /// </summary>
    private static WorldChange ReadChange(JsonElement entry, int minor, bool nodeOnly)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("kind", out var kindValue))
            throw Schema();
        var kind = Text(kindValue, 128);
        if (kind is "snapshot_begin" or "snapshot_chunk" or "snapshot_commit" or "catch_up_delta" or "catch_up_complete")
        {
            if (nodeOnly)
                throw Schema();
            if (minor == 0)
                throw new ProtocolException("protocol_version");
        }
        switch (kind)
        {
            case "node_upsert":
                Fields(entry, "kind", "node_id", "generation", "node_revision", "state");
                return new NodeUpsert(new NodeFact(Text(entry.GetProperty("node_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("node_revision")),
                    ReadState(entry.GetProperty("state"))));
            case "node_remove":
                Fields(entry, "kind", "node_id", "generation", "node_revision");
                return new NodeRemove(Text(entry.GetProperty("node_id"), 128),
                    Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("node_revision")));
            case "snapshot_begin":
                Fields(entry, "kind", "snapshot_id", "journal_sequence");
                return new SnapshotBegin(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("journal_sequence"), allowZero: true));
            case "snapshot_chunk":
                Fields(entry, "kind", "snapshot_id", "chunk_index", "changes");
                return new SnapshotChunk(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("chunk_index")), ReadChanges(entry.GetProperty("changes"), minor, nodeOnly: true));
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
                    ReadChanges(entry.GetProperty("changes"), minor, nodeOnly: true));
            case "catch_up_complete":
                Fields(entry, "kind", "snapshot_id", "through_journal_sequence");
                return new CatchUpComplete(Text(entry.GetProperty("snapshot_id"), 128),
                    Counter(entry.GetProperty("through_journal_sequence"), allowZero: true));
            default:
                throw new ProtocolException("entry_kind");
        }
    }

    /// <summary>
    /// Пишет одну заранее проверенную запись; вложенные changes ограничены узловыми фактами.
    /// После каждой записи проверяет накопленный размер, прекращая построение слишком большого кадра.
    /// </summary>
    private static void WriteChange(Utf8JsonWriter writer, WorldChange change)
    {
        writer.WriteStartObject();
        switch (change)
        {
            case NodeUpsert upsert:
                var node = upsert.Node;
                writer.WriteString("kind", "node_upsert");
                WriteIdentity(writer, node.NodeId, node.Generation, node.Revision);
                writer.WriteStartObject("state");
                writer.WriteString("address", node.State.Address);
                writer.WriteString("node_type", node.State.NodeType);
                writer.WriteStartObject("position");
                writer.WriteNumber("x", node.State.Position.X);
                writer.WriteNumber("y", node.State.Position.Y);
                writer.WriteNumber("z", node.State.Position.Z);
                writer.WriteEndObject();
                writer.WriteString("sector_id", node.State.SectorId);
                writer.WriteBoolean("online", node.State.Online);
                writer.WriteEndObject();
                break;
            case NodeRemove remove:
                writer.WriteString("kind", "node_remove");
                WriteIdentity(writer, remove.NodeId, remove.Generation, remove.Revision);
                break;
            case SnapshotBegin begin:
                WriteSnapshotIdentity(writer, "snapshot_begin", begin.SnapshotId);
                writer.WriteString("journal_sequence", Decimal(begin.JournalSequence));
                break;
            case SnapshotChunk chunk:
                WriteSnapshotIdentity(writer, "snapshot_chunk", chunk.SnapshotId);
                writer.WriteString("chunk_index", Decimal(chunk.ChunkIndex));
                WriteChanges(writer, chunk.Changes);
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
                WriteChanges(writer, delta.Changes);
                break;
            case CatchUpComplete complete:
                WriteSnapshotIdentity(writer, "catch_up_complete", complete.SnapshotId);
                writer.WriteString("through_journal_sequence", Decimal(complete.ThroughJournalSequence));
                break;
            default:
                throw Schema();
        }
        writer.WriteEndObject();
        if (writer.BytesCommitted + writer.BytesPending > FramedStream.MaxFrameBytes)
            throw new ProtocolException("frame_size");
    }

    /// <summary>Пишет общие kind и snapshot_id, не закрывая заимствованный JSON writer.</summary>
    private static void WriteSnapshotIdentity(Utf8JsonWriter writer, string kind, string snapshotId)
    {
        writer.WriteString("kind", kind);
        writer.WriteString("snapshot_id", snapshotId);
    }

    /// <summary>Пишет упорядоченные node-only changes; контроль итогового размера выполняется для каждого факта.</summary>
    private static void WriteChanges(Utf8JsonWriter writer, IReadOnlyList<WorldChange> changes)
    {
        writer.WriteStartArray("changes");
        foreach (var change in changes)
            WriteChange(writer, change);
        writer.WriteEndArray();
    }

    /// <summary>
    /// Читает полное состояние узла, требуя точные поля и явный boolean online.
    /// Координаты X/Y ограничены 1..32767, Z — 1..4096; строки состояния — 256 единицами UTF-16.
    /// </summary>
    private static NodeState ReadState(JsonElement state)
    {
        Fields(state, "address", "node_type", "position", "sector_id", "online");
        var position = state.GetProperty("position");
        Fields(position, "x", "y", "z");
        var online = state.GetProperty("online");
        if (online.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Schema();
        return new NodeState(Text(state.GetProperty("address"), 256), Text(state.GetProperty("node_type"), 256),
            new Position(Integer(position.GetProperty("x"), 1, 32_767),
                Integer(position.GetProperty("y"), 1, 32_767), Integer(position.GetProperty("z"), 1, 4_096)),
            Text(state.GetProperty("sector_id"), 256), online.GetBoolean());
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
            if (ValidateChange(change))
                minor = 1;
        return minor;
    }

    /// <summary>
    /// Проверяет локальную запись и возвращает true для sync, требующего minor 1.
    /// nodeOnly закрывает вложенность управляющих записей даже для модели, созданной без wire-кодека.
    /// </summary>
    private static bool ValidateChange(WorldChange? change, bool nodeOnly = false)
    {
        switch (change)
        {
            case NodeUpsert { Node: { State: { Position: { } position } state } node }:
                ValidateText(node.NodeId, 128);
                ValidateCounters(node.Generation, node.Revision);
                ValidateText(state.Address, 256);
                ValidateText(state.NodeType, 256);
                ValidateText(state.SectorId, 256);
                if (position.X is < 1 or > 32_767 || position.Y is < 1 or > 32_767 || position.Z is < 1 or > 4_096)
                    throw Schema();
                return false;
            case NodeRemove remove:
                ValidateText(remove.NodeId, 128);
                ValidateCounters(remove.Generation, remove.Revision);
                return false;
            case SnapshotBegin begin when !nodeOnly:
                ValidateText(begin.SnapshotId, 128);
                return true;
            case SnapshotChunk chunk when !nodeOnly:
                ValidateText(chunk.SnapshotId, 128);
                if (chunk.ChunkIndex == 0)
                    throw Schema();
                ValidateNodeChanges(chunk.Changes);
                return true;
            case SnapshotCommit commit when !nodeOnly:
                ValidateText(commit.SnapshotId, 128);
                ValidateHash(commit.Sha256);
                return true;
            case CatchUpDelta delta when !nodeOnly:
                ValidateText(delta.SnapshotId, 128);
                ValidateNodeChanges(delta.Changes);
                return true;
            case CatchUpComplete complete when !nodeOnly:
                ValidateText(complete.SnapshotId, 128);
                return true;
            default:
                throw Schema();
        }
    }

    /// <summary>Проверяет наличие и лимит вложенного списка; только штатные node upsert/remove проходят allowlist.</summary>
    private static void ValidateNodeChanges(IReadOnlyList<WorldChange>? changes)
    {
        if (changes is null || changes.Count > MaxEntries)
            throw Schema();
        foreach (var change in changes)
            ValidateChange(change, nodeOnly: true);
    }

    /// <summary>Принимает ровно 64 ASCII lowercase hex символа без нормализации или исправления checksum.</summary>
    private static void ValidateHash(string? hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Schema();
    }

    /// <summary>
    /// Проверяет положительные поколение и ревизию локальной доменной записи; ноль зарезервирован как отсутствие значения.
    /// </summary>
    private static void ValidateCounters(ulong generation, ulong revision)
    {
        if (generation == 0 || revision == 0)
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
