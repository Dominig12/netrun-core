namespace Netrun.Host;

/// <summary>Необязательная диагностика принадлежит тому же owner, но не расходует protocol sequence или replay journal.</summary>
public interface IMonitoredHostSession : IStreamingHostSession
{
    /// <summary>Снимает O(1) snapshot без Pump, изменения игрового времени и payload/native references.</summary>
    byte[] CreateMonitorFrame(HostConnectionMetrics connection);
}

/// <summary>Scalar socket counters; output включает frame, который writer ещё не передал целиком.</summary>
/// <param name="InputQueueFrames">Прочитанные кадры до передачи owner.</param>
/// <param name="InputQueueBytes">Байты входных тел до передачи owner.</param>
/// <param name="OutputQueueFrames">Кадры, ожидающие полного завершения socket write.</param>
/// <param name="OutputQueueBytes">Байты тел до полного завершения socket write.</param>
public readonly record struct HostConnectionMetrics(long InputQueueFrames, long InputQueueBytes,
    long OutputQueueFrames, long OutputQueueBytes);
