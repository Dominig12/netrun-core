using System.Text.Json;
using Netrun.Core;

namespace Netrun.Protocol;

/// <summary>JSON-адаптер явного immutable payload; Core не зависит от JsonElement или serializer.</summary>
public static class NetworkValueJson
{
    /// <summary>Полностью разбирает ограниченное дерево; caller отдельно ограничивает размер исходного JSON-документа.</summary>
    public static NetworkValue Decode(JsonElement element)
    {
        try
        {
            var remaining = NetworkValue.MaxTotalValues;
            return Read(element, 1, ref remaining);
        }
        catch (ProtocolException) { throw; }
        catch (ArgumentException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
    }

    /// <summary>Пишет только допустимое bounded дерево; null reference не подменяет явный NetworkValue.Null.</summary>
    public static void Write(Utf8JsonWriter writer, NetworkValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null || !value.Validate()) throw Invalid();
        var started = writer.BytesCommitted + writer.BytesPending;
        WriteCore(writer, value, started);
    }

    /// <summary>Проверяет глубину и общий work budget до спуска; null/array/object сохраняют разные типы.</summary>
    private static NetworkValue Read(JsonElement element, int depth, ref int remaining)
    {
        if (depth > NetworkValue.MaxDepth || remaining-- <= 0) throw Invalid();
        switch (element.ValueKind)
        {
            case JsonValueKind.Null: return NetworkValue.Null;
            case JsonValueKind.True: return NetworkValue.FromBoolean(true);
            case JsonValueKind.False: return NetworkValue.FromBoolean(false);
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out var number) || !double.IsFinite(number)) throw Invalid();
                return NetworkValue.FromNumber(number);
            case JsonValueKind.String:
                return NetworkValue.FromText(element.GetString()!);
            case JsonValueKind.Array:
                if (element.GetArrayLength() > NetworkValue.MaxContainerEntries) throw Invalid();
                var items = new List<NetworkValue>(element.GetArrayLength());
                foreach (var child in element.EnumerateArray()) items.Add(Read(child, depth + 1, ref remaining));
                return NetworkValue.FromArray(items);
            case JsonValueKind.Object:
                var properties = new Dictionary<string, NetworkValue>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (properties.Count == NetworkValue.MaxContainerEntries || properties.ContainsKey(property.Name)) throw Invalid();
                    // Фабрика дополнительно проверяет Unicode ключа, но длина отсекается до чтения child.
                    if (property.Name.Length > NetworkValue.MaxKeyLength) throw Invalid();
                    properties.Add(property.Name, Read(property.Value, depth + 1, ref remaining));
                }
                return NetworkValue.FromObject(properties);
            default: throw Invalid();
        }
    }

    /// <summary>Рекурсивно пишет уже проверенные immutable значения и проверяет фактический объём после каждого child.</summary>
    private static void WriteCore(Utf8JsonWriter writer, NetworkValue value, long started)
    {
        switch (value)
        {
            case NetworkNull: writer.WriteNullValue(); break;
            case NetworkBoolean boolean: writer.WriteBooleanValue(boolean.Value); break;
            case NetworkNumber number: writer.WriteNumberValue(number.Value); break;
            case NetworkText text: writer.WriteStringValue(text.Value); break;
            case NetworkArray array:
                writer.WriteStartArray();
                foreach (var child in array.Items) WriteCore(writer, child, started);
                writer.WriteEndArray();
                break;
            case NetworkObject map:
                writer.WriteStartObject();
                foreach (var (key, child) in map.Properties)
                {
                    writer.WritePropertyName(key);
                    WriteCore(writer, child, started);
                }
                writer.WriteEndObject();
                break;
            default: throw Invalid();
        }
        if (writer.BytesCommitted + writer.BytesPending - started > NetworkValue.MaxEstimatedJsonBytes)
            throw new ProtocolException("payload_size");
    }

    /// <summary>Безопасная диагностика не сохраняет текст или ключ из пользовательского payload.</summary>
    private static ProtocolException Invalid() => new("payload_invalid");
}
