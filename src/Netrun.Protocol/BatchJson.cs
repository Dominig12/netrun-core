using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;

namespace Netrun.Protocol;

/// <summary>
/// Строгий JSON-кодек экспериментальной схемы v1.0 для упорядоченных изменений узлов.
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
    /// Порядок entries сохраняется; счётчики читаются только из канонических положительных десятичных строк.
    /// </summary>
    /// <param name="body">От 1 до 1 MiB байт UTF-8 без заголовка кадра; входная память не сохраняется.</param>
    /// <returns>Пакет v1.0 с логическим временем в целых децисекундах и неизменяемым списком изменений.</returns>
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
            if (Integer(root.GetProperty("protocol_major"), 0, int.MaxValue) != 1 ||
                Integer(root.GetProperty("protocol_minor"), 0, int.MaxValue) != 0)
                throw new ProtocolException("protocol_version");
            var epochs = new Epochs(Text(root.GetProperty("world_epoch"), 128),
                Text(root.GetProperty("engine_epoch"), 128), Text(root.GetProperty("binding_epoch"), 128));
            var sequence = Counter(root.GetProperty("sequence"));
            var time = Integer(root.GetProperty("simulation_time_ds"), 0, int.MaxValue);
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaxEntries)
                throw Schema();
            var changes = new List<WorldChange>(entries.GetArrayLength());
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("kind", out var kind))
                    throw Schema();
                switch (Text(kind, 128))
                {
                    case "node_upsert":
                        Fields(entry, "kind", "node_id", "generation", "node_revision", "state");
                        changes.Add(new NodeUpsert(new NodeFact(Text(entry.GetProperty("node_id"), 128),
                            Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("node_revision")),
                            ReadState(entry.GetProperty("state")))));
                        break;
                    case "node_remove":
                        Fields(entry, "kind", "node_id", "generation", "node_revision");
                        changes.Add(new NodeRemove(Text(entry.GetProperty("node_id"), 128),
                            Counter(entry.GetProperty("generation")), Counter(entry.GetProperty("node_revision"))));
                        break;
                    default:
                        throw new ProtocolException("entry_kind");
                }
            }
            return new InputBatch(epochs, sequence, time, changes.AsReadOnly());
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
    /// Проверяет доменный пакет и кодирует тело v1.0 с фиксированными именами полей и порядком entries.
    /// Не добавляет заголовок кадра и не меняет пакет; возвращённый массив принадлежит вызывающей стороне.
    /// </summary>
    /// <param name="batch">Полный пакет; коллекция изменений должна оставаться стабильной до завершения вызова.</param>
    /// <returns>Новое JSON-тело UTF-8, пригодное для FramedStream.WriteAsync.</returns>
    /// <exception cref="ProtocolException">schema_invalid для недопустимой модели; frame_size при превышении лимита.</exception>
    public static byte[] Encode(InputBatch batch)
    {
        Validate(batch);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol_major", 1);
            writer.WriteNumber("protocol_minor", 0);
            writer.WriteString("world_epoch", batch.Epochs.World);
            writer.WriteString("engine_epoch", batch.Epochs.Engine);
            writer.WriteString("binding_epoch", batch.Epochs.Binding);
            writer.WriteString("sequence", Decimal(batch.Sequence));
            writer.WriteNumber("simulation_time_ds", batch.SimulationTimeDs);
            writer.WriteStartArray("entries");
            foreach (var change in batch.Entries)
            {
                writer.WriteStartObject();
                if (change is NodeUpsert upsert)
                {
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
                }
                else if (change is NodeRemove remove)
                {
                    writer.WriteString("kind", "node_remove");
                    WriteIdentity(writer, remove.NodeId, remove.Generation, remove.Revision);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.Length > FramedStream.MaxFrameBytes)
            throw new ProtocolException("frame_size");
        return buffer.ToArray();
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
    /// Читает положительный ulong только из строки длиной до 20 десятичных цифр ASCII.
    /// Отвергает ведущие нули, знаки, пробелы, числовой JSON и переполнение без потери точности счётчика.
    /// </summary>
    private static ulong Counter(JsonElement value)
    {
        var text = Text(value, 20);
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
    /// Объём проверки ограничен MaxEntries; никаких изменений Core и выделения JSON-тела здесь нет.
    /// </summary>
    private static void Validate(InputBatch? batch)
    {
        if (batch?.Epochs is null || batch.Sequence == 0 || batch.SimulationTimeDs < 0 ||
            batch.Entries is null || batch.Entries.Count > MaxEntries)
            throw Schema();
        ValidateText(batch.Epochs.World, 128);
        ValidateText(batch.Epochs.Engine, 128);
        ValidateText(batch.Epochs.Binding, 128);
        foreach (var change in batch.Entries)
        {
            switch (change)
            {
                case NodeUpsert { Node: { State: { Position: { } position } state } node }:
                    ValidateText(node.NodeId, 128);
                    ValidateCounters(node.Generation, node.Revision);
                    ValidateText(state.Address, 256);
                    ValidateText(state.NodeType, 256);
                    ValidateText(state.SectorId, 256);
                    if (position.X is < 1 or > 32_767 || position.Y is < 1 or > 32_767 ||
                        position.Z is < 1 or > 4_096)
                        throw Schema();
                    break;
                case NodeRemove remove:
                    ValidateText(remove.NodeId, 128);
                    ValidateCounters(remove.Generation, remove.Revision);
                    break;
                default:
                    throw Schema();
            }
        }
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
