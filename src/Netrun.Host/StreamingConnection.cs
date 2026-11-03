using System.Threading.Channels;
using System.Diagnostics;
using Netrun.Protocol;

namespace Netrun.Host;

/// <summary>Bounded socket ingress/egress и один последовательный owner runtime без Task на игровой объект.</summary>
internal static class StreamingConnection
{
    /// <summary>Физический pulse продолжает готовую работу, сохраняя BYOND simulation time.</summary>
    private static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(25);
    /// <summary>Независимый низкочастотный heartbeat, не влияющий на admission или игровые часы.</summary>
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);

    /// <summary>Только scalar counters пересекают socket tasks; runtime остаётся внутри owner.</summary>
    private sealed class ConnectionCounters
    {
        internal long InputFrames, InputBytes, OutputFrames, OutputBytes;
        internal HostConnectionMetrics Snapshot() => new(Interlocked.Read(ref InputFrames), Interlocked.Read(ref InputBytes),
            Interlocked.Read(ref OutputFrames), Interlocked.Read(ref OutputBytes));
        internal void AddInput(int bytes) { Interlocked.Increment(ref InputFrames); Interlocked.Add(ref InputBytes, bytes); }
        internal void RemoveInput(int bytes) { Interlocked.Decrement(ref InputFrames); Interlocked.Add(ref InputBytes, -bytes); }
        internal void AddOutput(int bytes) { Interlocked.Increment(ref OutputFrames); Interlocked.Add(ref OutputBytes, bytes); }
        internal void RemoveOutput(int bytes) { Interlocked.Decrement(ref OutputFrames); Interlocked.Add(ref OutputBytes, -bytes); }
    }

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
    internal static async Task RunAsync(Stream stream, IStreamingHostSession session, TimeSpan idleTimeout, CancellationToken cancellationToken,
        bool monitoring = false)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputs = Queue(session.Limits.QueueCapacity);
        var outputs = Queue(session.Limits.QueueCapacity);
        var counters = new ConnectionCounters();
        var reader = ReadInputs(stream, inputs.Writer, counters, idleTimeout, stop.Token);
        var writer = WriteOutputs(stream, outputs.Reader, counters, idleTimeout, stop.Token);
        var owner = Own(session, inputs.Reader, outputs.Writer, counters, monitoring, stop.Token);
        try { await Task.WhenAny(reader, writer, owner); }
        finally
        {
            stop.Cancel();
        }
        // WhenAll observes every fault before the caller disposes or disconnects the single-owner runtime.
        await Task.WhenAll(reader, writer, owner);
    }

    /// <summary>Только читает immutable frame bodies; bounded channel переносит pressure обратно в TCP.</summary>
    private static async Task ReadInputs(Stream stream, ChannelWriter<byte[]> inputs, ConnectionCounters counters,
        TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);
            var body = await FramedStream.ReadAsync(stream, idle.Token);
            if (body is null) return;
            counters.AddInput(body.Length);
            await inputs.WriteAsync(body, cancellationToken);
        }
    }

    /// <summary>Последовательный writer сохраняет порядок output sequence и имеет deadline stalled peer.</summary>
    private static async Task WriteOutputs(Stream stream, ChannelReader<byte[]> outputs, ConnectionCounters counters,
        TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await foreach (var body in outputs.ReadAllAsync(cancellationToken))
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);
            await FramedStream.WriteAsync(stream, body, idle.Token);
            counters.RemoveOutput(body.Length);
        }
    }

    /// <summary>Только этот actor вызывает session: входной burst не вытесняет независимое продвижение выходов.</summary>
    private static async Task Own(IStreamingHostSession session, ChannelReader<byte[]> inputs, ChannelWriter<byte[]> outputs,
        ConnectionCounters counters, bool monitoring, CancellationToken cancellationToken)
    {
        var readable = inputs.WaitToReadAsync(cancellationToken).AsTask();
        var pulse = Task.Delay(Pulse, cancellationToken);
        var monitored = monitoring ? session as IMonitoredHostSession : null;
        var diagnostics = session as RuntimeHostSession;
        var lastMonitor = Stopwatch.GetTimestamp();
        void TryMonitor()
        {
            diagnostics?.RecordDiagnostics(counters.Snapshot());
            if (monitored is null || Stopwatch.GetElapsedTime(lastMonitor) < MonitorInterval) return;
            lastMonitor = Stopwatch.GetTimestamp();
            var snapshot = counters.Snapshot();
            // Не копить устаревшие samples за stalled writer и не занимать domain replay journal.
            if (snapshot.OutputQueueFrames != 0) return;
            var frame = monitored.CreateMonitorFrame(snapshot);
            counters.AddOutput(frame.Length);
            if (!outputs.TryWrite(frame)) counters.RemoveOutput(frame.Length);
        }
        async ValueTask EnqueueOutput(byte[] frame)
        {
            counters.AddOutput(frame.Length);
            await outputs.WriteAsync(frame, cancellationToken);
        }
        while (true)
        {
            await Task.WhenAny(readable, pulse);
            cancellationToken.ThrowIfCancellationRequested();
            if (readable.IsCompleted)
            {
                if (!await readable) return;
                var batch = new List<ReadOnlyMemory<byte>>(IStreamingHostSession.MaximumInputGroupFrames);
                for (var count = 0; count < IStreamingHostSession.MaximumInputGroupFrames && inputs.TryRead(out var body); count++)
                {
                    counters.RemoveInput(body.Length);
                    batch.Add(body);
                }
                var responses = await session.ProcessStreamingBatchAsync(batch, cancellationToken);
                foreach (var response in responses) await EnqueueOutput(response);
                await Task.Yield();
                readable = inputs.WaitToReadAsync(cancellationToken).AsTask();
            }
            if (pulse.IsCompleted) pulse = Task.Delay(Pulse, cancellationToken);
            while (session.TryProduceOutput() is { } pushed)
            {
                await EnqueueOutput(pushed);
                // Cooperative owner yield keeps socket tasks live without an application burst/window cap.
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                TryMonitor();
                // Готовый ingress не ждёт завершения всего большого graph output backlog.
                if (inputs.TryPeek(out _)) break;
            }
            TryMonitor();
        }
    }
}
