using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Transport;

namespace Netrun.Core.Plugins;

/// <summary>Единые shape/fingerprint правила mod-команд до FIFO admission или создания module task.</summary>
public static class ModuleRequestRules
{
    /// <summary>Проверяет полный bounded envelope без вызова кода расширения.</summary>
    public static bool IsValid(ModuleRequest? request) => request is not null && TransportFingerprint.ValidKey(request.Key) &&
        PluginModuleRegistry.ValidToken(request.ModuleId) && PluginModuleRegistry.ValidToken(request.Operation) &&
        request.Payload is not null && request.Payload.Validate();

    /// <summary>Включает все scalar fields и типы payload; разные module operations не смешиваются в replay.</summary>
    public static byte[]? Fingerprint(ModuleRequest? request)
    {
        if (!IsValid(request)) return null;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-MODULE-1"); writer.Write(request!.Key.Id); writer.Write(request.ModuleId); writer.Write(request.Operation);
            TransportFingerprint.WriteValue(writer, request.Payload);
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }
}

/// <summary>Terminal immutable результат команды мода; произвольные native callbacks не поддерживаются.</summary>
public sealed record ModuleCompleted(string BindingEpoch, RequestKey Key, string ModuleId,
    bool Success, string Code, NetworkValue Result) : TransportEvent(BindingEpoch, Key);

/// <summary>Версия/содержимое проекции определяется модом; событие не исполняет DM код.</summary>
public sealed record ModuleProjectionChanged(string BindingEpoch, RequestKey Key, string ModuleId,
    string Topic, NetworkValue Data) : TransportEvent(BindingEpoch, Key);
