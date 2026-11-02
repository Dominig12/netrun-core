using System.Security.Cryptography;
using System.Text;

namespace Netrun.Core.Transport;

/// <summary>Валидация и deterministic content identity; process-local GetHashCode не используется для replay.</summary>
internal static class TransportFingerprint
{
    /// <summary>Request producer/code tokens не содержат разделителей канонического request ID.</summary>
    internal static bool ValidToken(string? text, int maximum) => text is { Length: > 0 } && text.Length <= maximum &&
        text.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    /// <summary>Ключ остаётся пригодным для watermark независимо от наличия history.</summary>
    internal static bool ValidKey(RequestKey? key) => key is not null && key.Sequence > 0 && ValidToken(key.ProducerId, 64);

    /// <summary>Проверяет полный immutable request до любых admission-изменений.</summary>
    internal static bool ValidRequest(NetworkRequest? request) => request is { Source: not null, Destination: not null, Payload: not null } &&
        ValidKey(request.Key) && ValidService(request.Source) && ValidService(request.Destination) &&
        NodeFactRules.IsValidText(request.OperationId, 128) && request.Payload.Validate() &&
        request.DeadlineDs is not < 0 && OptionalId(request.CoalescingKey) && OptionalId(request.AuthorizationContext) && OptionalId(request.NativeContext) &&
        Enum.IsDefined(request.Traffic) && request.Frequency is not <= 0 &&
        (request.Traffic == TrafficClass.Communications || request.Frequency is null);

    /// <summary>Node generation от сервиса проверяется отдельно по текущему world view.</summary>
    private static bool ValidService(ServiceKey key) => key.Generation > 0 && NodeFactRules.IsValidText(key.ServiceId, 128);

    /// <summary>Opaque контекст не является сериализованной ссылкой или произвольным большим payload.</summary>
    private static bool OptionalId(string? value) => value is null || NodeFactRules.IsValidText(value, 128);

    /// <summary>Хэширует все семантические поля request в явном порядке и точные типы дерева.</summary>
    internal static byte[] Request(NetworkRequest request)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-REQUEST-1");
            writer.Write(request.Key.ProducerId);
            writer.Write(request.Key.Sequence);
            writer.Write(request.Source.ServiceId);
            writer.Write(request.Source.Generation);
            writer.Write(request.Destination.ServiceId);
            writer.Write(request.Destination.Generation);
            writer.Write(request.OperationId);
            writer.Write(request.DeadlineDs.HasValue);
            if (request.DeadlineDs is { } deadline) writer.Write(deadline);
            NullableText(writer, request.CoalescingKey);
            writer.Write(request.StateRevision.HasValue);
            if (request.StateRevision is { } revision) writer.Write(revision);
            NullableText(writer, request.AuthorizationContext);
            NullableText(writer, request.NativeContext);
            writer.Write((byte)request.Traffic);
            writer.Write(request.Frequency.HasValue);
            if (request.Frequency is { } frequency) writer.Write(frequency);
            WriteValue(writer, request.Payload);
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    /// <summary>Повтор результата invocation сравнивает success, типизированный ответ и безопасный code.</summary>
    internal static byte[] Result(InvocationResult result)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-INVOCATION-RESULT-1");
            writer.Write(result.BindingEpoch);
            writer.Write(result.InvocationId);
            writer.Write(result.Success);
            NullableText(writer, result.Code);
            writer.Write(result.Payload is not null);
            if (result.Payload is { } payload) WriteValue(writer, payload);
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    /// <summary>Null не совпадает с пустой строкой и не теряется при hashing optional metadata.</summary>
    private static void NullableText(BinaryWriter writer, string? text)
    {
        writer.Write(text is not null);
        if (text is not null) writer.Write(text);
    }

    /// <summary>Пишет bounded дерево; object keys сортируются ordinal, array order сохраняется.</summary>
    internal static void WriteValue(BinaryWriter writer, NetworkValue value)
    {
        writer.Write((byte)value.Kind);
        switch (value)
        {
            case NetworkNull: break;
            case NetworkBoolean boolean: writer.Write(boolean.Value); break;
            case NetworkNumber number: writer.Write(number.Value == 0 ? 0d : number.Value); break;
            case NetworkText text: writer.Write(text.Value); break;
            case NetworkArray array:
                writer.Write(array.Items.Count);
                foreach (var item in array.Items) WriteValue(writer, item);
                break;
            case NetworkObject map:
                writer.Write(map.Properties.Count);
                foreach (var (key, child) in map.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.Write(key);
                    WriteValue(writer, child);
                }
                break;
        }
    }
}
