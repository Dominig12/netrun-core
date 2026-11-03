using Netrun.Core;

namespace Netrun.Host;

/// <summary>Последовательная доменная сессия после аутентификации; TCP и native ABI остаются снаружи.</summary>
public interface IHostSession : IDisposable
{
    /// <summary>Обрабатывает один ограниченный кадр и возвращает один ограниченный пакет результатов.</summary>
    ValueTask<byte[]> ProcessAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
    /// <summary>Классифицирует незавершённые операции при разрыве, не повторяя неизвестные побочные эффекты.</summary>
    void Disconnected();
}

/// <summary>Один owner обслуживает вход и фоновые результаты независимо; socket reader/writer не вызывают runtime.</summary>
public interface IStreamingHostSession : IHostSession
{
    /// <summary>Размер encoding/coalescing группы; не является admission/rate window.</summary>
    public const int MaximumInputGroupFrames = 8;
    /// <summary>Необязательные ограничения; default0 не ограничивает application throughput/queues.</summary>
    StreamingHostLimits Limits { get; }
    /// <summary>Включает duplex до первого кадра и резервирует bounded journal для push и ответов.</summary>
    void EnableStreaming();
    /// <summary>Применяет вход; ACK-only poll освобождает journal без ответного пустого output.</summary>
    ValueTask<byte[]?> ProcessStreamingAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
    /// <summary>Обрабатывает до восьми wire inputs; соседние world facts публикуют один graph перед следующим command.</summary>
    ValueTask<IReadOnlyList<byte[]>> ProcessStreamingBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> bodies, CancellationToken cancellationToken);
    /// <summary>Продвигает bounded работу, не меняя simulation time; null означает отсутствие доступного события/credit.</summary>
    byte[]? TryProduceOutput();
}

/// <summary>Опциональные soft quotas для profiling/tests; ноль означает отсутствие application cap.</summary>
/// <param name="MaximumInflightFrames">Input frames до cumulative ACK; 0 — без окна.</param>
/// <param name="MaximumUnacknowledgedOutputs">Push credit; 0 — отправка без ожидания ACK.</param>
/// <param name="QueueCapacity">Входная/выходная очередь socket actor; 0 — unbounded channel.</param>
/// <param name="MaximumEventsPerOutput">Event count внутри output; 0 — только wire byte limit.</param>
/// <param name="GraphPageWindow">Graph application page credit; 0 — без окна.</param>
public sealed record StreamingHostLimits(int MaximumInflightFrames = 0, int MaximumUnacknowledgedOutputs = 0,
    int QueueCapacity = 0, int MaximumEventsPerOutput = 0, int GraphPageWindow = 0)
{
    /// <summary>Rejects negative configuration before creating session queues.</summary>
    internal void Validate()
    {
        if (MaximumInflightFrames < 0 || MaximumUnacknowledgedOutputs < 0 || QueueCapacity < 0 ||
            MaximumEventsPerOutput < 0 || GraphPageWindow < 0)
            throw new ArgumentOutOfRangeException(nameof(StreamingHostLimits));
    }
}

/// <summary>Composition доменных модулей конкретного binding; новая связь создаёт отдельное состояние.</summary>
public interface IHostSessionFactory
{
    /// <summary>Создаёт сессию только после проверки секрета и полей handshake.</summary>
    IHostSession Create(Epochs epochs);
}
