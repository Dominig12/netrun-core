using System.Threading.Channels;
using Netrun.Protocol;

namespace Netrun.Host;

/// <summary>Bounded socket ingress/egress и один последовательный owner runtime без Task на игровой объект.</summary>
internal static class StreamingConnection
{
    /// <summary>Физический pulse продолжает готовую работу, сохраняя BYOND simulation time.</summary>
    private static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(25);

    /// <summary>Default не ставит application cap; конечный размер включается явно для profiling/tests.</summary>
    private static Channel<byte[]> Queue(int capacity) => capacity == 0
        ? Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        })
        : Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    /// <summary>Первое завершение/fault закрывает оба I/O направления; callbacks не переживают владельца session.</summary>
    internal static async Task RunAsync(Stream stream, IStreamingHostSession session, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputs = Queue(session.Limits.QueueCapacity);
        var outputs = Queue(session.Limits.QueueCapacity);
        var reader = ReadInputs(stream, inputs.Writer, idleTimeout, stop.Token);
        var writer = WriteOutputs(stream, outputs.Reader, idleTimeout, stop.Token);
        var owner = Own(session, inputs.Reader, outputs.Writer, stop.Token);
        try { await Task.WhenAny(reader, writer, owner); }
        finally
        {
            stop.Cancel();
        }
        // WhenAll observes every fault before the caller disposes or disconnects the single-owner runtime.
        await Task.WhenAll(reader, writer, owner);
    }

    /// <summary>Только читает immutable frame bodies; bounded channel переносит pressure обратно в TCP.</summary>
    private static async Task ReadInputs(Stream stream, ChannelWriter<byte[]> inputs, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);
            var body = await FramedStream.ReadAsync(stream, idle.Token);
            if (body is null) return;
            await inputs.WriteAsync(body, cancellationToken);
        }
    }

    /// <summary>Последовательный writer сохраняет порядок output sequence и имеет deadline stalled peer.</summary>
    private static async Task WriteOutputs(Stream stream, ChannelReader<byte[]> outputs, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await foreach (var body in outputs.ReadAllAsync(cancellationToken))
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);
            await FramedStream.WriteAsync(stream, body, idle.Token);
        }
    }

    /// <summary>Только этот actor вызывает session: входной burst не вытесняет независимое продвижение выходов.</summary>
    private static async Task Own(IStreamingHostSession session, ChannelReader<byte[]> inputs, ChannelWriter<byte[]> outputs,
        CancellationToken cancellationToken)
    {
        var readable = inputs.WaitToReadAsync(cancellationToken).AsTask();
        var pulse = Task.Delay(Pulse, cancellationToken);
        while (true)
        {
            await Task.WhenAny(readable, pulse);
            cancellationToken.ThrowIfCancellationRequested();
            if (readable.IsCompleted)
            {
                if (!await readable) return;
                var batch = new List<ReadOnlyMemory<byte>>(IStreamingHostSession.MaximumInputGroupFrames);
                for (var count = 0; count < IStreamingHostSession.MaximumInputGroupFrames && inputs.TryRead(out var body); count++)
                    batch.Add(body);
                var responses = await session.ProcessStreamingBatchAsync(batch, cancellationToken);
                foreach (var response in responses) await outputs.WriteAsync(response, cancellationToken);
                await Task.Yield();
                readable = inputs.WaitToReadAsync(cancellationToken).AsTask();
            }
            if (pulse.IsCompleted) pulse = Task.Delay(Pulse, cancellationToken);
            while (session.TryProduceOutput() is { } pushed)
            {
                await outputs.WriteAsync(pushed, cancellationToken);
                // Cooperative owner yield keeps socket tasks live without an application burst/window cap.
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                // Готовый ingress не ждёт завершения всего большого graph output backlog.
                if (inputs.TryPeek(out _)) break;
            }
        }
    }
}
