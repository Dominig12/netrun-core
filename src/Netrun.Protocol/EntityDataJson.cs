using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netrun.Core;
using Netrun.Core.Access;
using Netrun.Core.Entities;

namespace Netrun.Protocol;

/// <summary>Строгий codec immutable entity DTO: unknown/duplicate fields запрещены, UInt64 только canonical строки.</summary>
public static class EntityDataJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Читает закрытый DTO после проверки всего дерева; размер кадра ограничен внешним framing.</summary>
    public static T Read<T>(JsonElement element)
    {
        ValidateTree(element, 0);
        try { return element.Deserialize<T>(Options) ?? throw new ProtocolException("schema_invalid"); }
        catch (JsonException) { throw new ProtocolException("schema_invalid"); }
        catch (ArgumentException) { throw new ProtocolException("schema_invalid"); }
    }

    /// <summary>Пишет DTO по стабильным snake_case полям, без сериализации произвольных runtime типов.</summary>
    public static void Write<T>(Utf8JsonWriter writer, T value) => JsonSerializer.Serialize(writer, value, Options);

    /// <summary>DTO закрыты типами Core; dynamic extension data и числовые counters не принимаются.</summary>
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new CounterConverter());
        options.Converters.Add(new ActorKeyConverter());
        options.Converters.Add(new PayloadConverter());
        options.Converters.Add(new ValueListConverterFactory());
        return options;
    }

    /// <summary>JsonSerializer не проверяет повторные свойства по умолчанию; проверяем их до создания DTO.</summary>
    private static void ValidateTree(JsonElement element, int depth)
    {
        if (depth > 16) throw new ProtocolException("schema_invalid");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || names.Count > 64) throw new ProtocolException("schema_invalid");
                ValidateTree(property.Value, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > EntityAccessAuthority.MaximumPolicies) throw new ProtocolException("schema_invalid");
            foreach (var item in element.EnumerateArray()) ValidateTree(item, depth + 1);
        }
    }

    /// <summary>Не допускает float округление поколений и counters при прохождении через BYOND JSON.</summary>
    private sealed class CounterConverter : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            var value = reader.GetString();
            if (value is not { Length: > 0 and <= 20 } || value.Length > 1 && value[0] == '0' ||
                value.Any(character => character is < '0' or > '9') ||
                !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)) throw new JsonException();
            return result;
        }
        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Одна wire форма actor identity для outer proof, create definition и read model.</summary>
    private sealed class ActorKeyConverter : JsonConverter<ActorKey>
    {
        public override ActorKey Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
                !value.TryGetProperty("actor_id", out var id) || id.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("generation", out var generation)) throw new JsonException();
            return new(id.GetString()!, generation.Deserialize<ulong>(options));
        }
        public override void Write(Utf8JsonWriter writer, ActorKey value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("actor_id", value.Id);
            writer.WritePropertyName("generation");
            JsonSerializer.Serialize(writer, value.Generation, options);
            writer.WriteEndObject();
        }
    }

    /// <summary>NetworkValue имеет явный bounded wire формат, а не CLR type metadata.</summary>
    private sealed class PayloadConverter : JsonConverter<NetworkValue>
    {
        public override bool HandleNull => true;
        public override NetworkValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return NetworkValue.Null;
            using var document = JsonDocument.ParseValue(ref reader);
            return NetworkValueJson.Decode(document.RootElement);
        }
        public override void Write(Utf8JsonWriter writer, NetworkValue value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue(); else NetworkValueJson.Write(writer, value);
        }
    }

    /// <summary>ValueList всегда имеет форму массива, в том числе для пустых ACL/credentials.</summary>
    private sealed class ValueListConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueList<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ValueListConverter<>).MakeGenericType(type.GetGenericArguments()[0]))!;
    }

    /// <summary>Ограничивает копирование коллекции до domain проверки конкретных credential/policy пределов.</summary>
    private sealed class ValueListConverter<T> : JsonConverter<ValueList<T>>
    {
        public override ValueList<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
            var values = new List<T>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return new(values, EntityAccessAuthority.MaximumPolicies);
                if (values.Count >= EntityAccessAuthority.MaximumPolicies) throw new JsonException();
                var item = JsonSerializer.Deserialize<T>(ref reader, options);
                if (item is null) throw new JsonException();
                values.Add(item);
            }
            throw new JsonException();
        }
        public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }
}
