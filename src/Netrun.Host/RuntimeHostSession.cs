using System.Diagnostics;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Protocol;

namespace Netrun.Host;

/// <summary>Composition доменной сессии и подтверждаемого output journal после аутентификации.</summary>
public sealed class RuntimeHostSession : IStreamingHostSession
{
    /// <summary>Bounded native apply burst: не более 256 projection rows за ответ, независимо от числа команд.</summary>
    private const int MaximumEventsPerReply = 8;
    /// <summary>Идентичность одного соединения; poll не может изменить binding или engine.</summary>
    private readonly Epochs _epochs;
    /// <summary>Единственный mutable владелец mirror, graph, routes и packets.</summary>
    private readonly NetworkRuntime _runtime;
    /// <summary>Кадры удерживаются до обработки DM; запись в сокет не удаляет их из журнала.</summary>
    private OutputReplayJournal _outputs = new();
    /// <summary>Output ACK освобождает отдельный slot входного кадра; push не расходует эти восемь slots.</summary>
    private readonly Queue<ulong> _inputReplies = [];
    /// <summary>Legacy request/reply остаётся самостоятельным режимом.</summary>
    private bool _streaming;
    /// <summary>Статистика transport, не protocol identities; не удерживает native objects или payload.</summary>
    private ulong _receivedMessages, _pushedOutputs;
    /// <summary>World inputs, объединённые с предшествующим fact frame до одного topology Pump.</summary>
    private ulong _coalescedFactFrames;
    /// <summary>Микросекунды текущего owner slice и наблюдавшийся максимум, без сетевого ожидания.</summary>
    private double _lastApplyUs, _lastPumpUs, _maxApplyUs, _maxPumpUs, _maxProcessingUs;
    /// <summary>Максимум одного не поместившегося immutable события; не теряется при границе кадра.</summary>
    private byte[]? _pendingEvent;

    /// <summary>Создаёт изолированное доменное состояние после успешного hello.</summary>
    public RuntimeHostSession(Epochs epochs, RuntimeOptions? options = null, StreamingHostLimits? streamingLimits = null)
    {
        _epochs = epochs;
        Limits = streamingLimits ?? new();
        Limits.Validate();
        _runtime = new NetworkRuntime(epochs, options);
    }

    /// <summary>Default quotas отключены; wire framing и causal/replay identities остаются строгими.</summary>
    public StreamingHostLimits Limits { get; }

    /// <summary>Проверяет output ACK перед admission; heartbeat не двигает логическое время мира.</summary>
    public ValueTask<byte[]> ProcessAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        => ValueTask.FromResult(Process(body, cancellationToken, false)!);

    /// <summary>Включает независимые push/input; default без quotas, finite credit резервируется при явной настройке.</summary>
    public void EnableStreaming()
    {
        if (_streaming) return;
        if (_outputs.LastSequence != 0) throw new InvalidOperationException("Streaming must be selected before processing input.");
        var finiteJournal = Limits.MaximumInflightFrames > 0 && Limits.MaximumUnacknowledgedOutputs > 0;
        var maximumFrames = finiteJournal ? (int)Math.Min(int.MaxValue,
            (long)Limits.MaximumInflightFrames + Limits.MaximumUnacknowledgedOutputs) : int.MaxValue;
        _outputs = new(maximumFrames, finiteJournal ? (long)maximumFrames * FramedStream.MaxFrameBytes : long.MaxValue);
        _runtime.ConfigureGraphSynchronizationWindow(Limits.GraphPageWindow);
        _runtime.EnableDeferredTopologyBuild();
        _streaming = true;
    }

    /// <summary>Ни один выход не ожидается в ACK-only path, поэтому нет бесконечной цепочки ACK пустых outputs.</summary>
    public ValueTask<byte[]?> ProcessStreamingAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (!_streaming) throw new InvalidOperationException("Streaming is not enabled.");
        return ValueTask.FromResult(Process(body, cancellationToken, true));
    }

    /// <summary>Последовательный owner объединяет только соседние facts; перед command/poll их topology уже опубликована.</summary>
    public ValueTask<IReadOnlyList<byte[]>> ProcessStreamingBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> bodies, CancellationToken cancellationToken)
    {
        if (!_streaming) throw new InvalidOperationException("Streaming is not enabled.");
        ArgumentNullException.ThrowIfNull(bodies);
        if (bodies.Count is < 1 or > IStreamingHostSession.MaximumInputGroupFrames) throw new ArgumentOutOfRangeException(nameof(bodies));
        var responses = new List<byte[]>(bodies.Count);
        var facts = new List<(RuntimeResult Result, ulong Sequence)>(bodies.Count);
        long groupStarted = 0;
        double applyUs = 0;
        void FlushFacts()
        {
            if (facts.Count == 0) return;
            _lastApplyUs = applyUs;
            _maxApplyUs = Math.Max(_maxApplyUs, applyUs);
            _coalescedFactFrames += (ulong)(facts.Count - 1);
            var response = Complete(facts, StreamingEventBudget, true, groupStarted, facts.Count)!;
            responses.Add(response);
            facts.Clear();
            applyUs = 0;
        }
        foreach (var body in bodies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            // Decode ровно один раз; world checksum bytes по-прежнему проверяет production parser.
            var message = RuntimeJson.Decode(body.Span);
            if (message is RuntimePacket { Input.Command: ApplyWorld } packet)
            {
                if (facts.Count == 0) groupStarted = started;
                Receive(message);
                Reserve(message, true, facts.Count);
                var applyStarted = Stopwatch.GetTimestamp();
                facts.Add((_runtime.Apply(packet.Input), packet.Input.Sequence));
                applyUs += Stopwatch.GetElapsedTime(applyStarted).TotalMicroseconds;
            }
            else
            {
                FlushFacts();
                var response = ProcessMessage(message, cancellationToken, true, started);
                if (response is not null) responses.Add(response);
            }
        }
        FlushFacts();
        return ValueTask.FromResult<IReadOnlyList<byte[]>>(responses);
    }

    /// <summary>Push credit оставляет резерв для всего advertised input window.</summary>
    private int StreamingEventBudget => Limits.MaximumEventsPerOutput == 0 ? int.MaxValue : Limits.MaximumEventsPerOutput;

    /// <summary>Не извлекает domain event, если stalled peer ещё не обработал прежние bounded outputs.</summary>
    public byte[]? TryProduceOutput()
    {
        if (!_streaming) throw new InvalidOperationException("Streaming is not enabled.");
        if ((Limits.MaximumUnacknowledgedOutputs > 0 && _outputs.LastSequence - _outputs.Acknowledged >= (ulong)Limits.MaximumUnacknowledgedOutputs) || !_outputs.CanReserve) return null;
        var started = Stopwatch.GetTimestamp();
        _lastApplyUs = 0;
        Pump();
        var response = Pack([], StreamingEventBudget, allowEmpty: false, started, push: true);
        if (response is not null)
        {
            _outputs.Append(response);
            _pushedOutputs++;
        }
        return response;
    }

    /// <summary>Проверяет общий epoch/ACK ledger до любого domain admission.</summary>
    private byte[]? Process(ReadOnlyMemory<byte> body, CancellationToken cancellationToken, bool streaming)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var message = RuntimeJson.Decode(body.Span);
        return ProcessMessage(message, cancellationToken, streaming, started);
    }

    /// <summary>Общий admission уже decoded сообщения; streaming batch не пересобирает JSON второй раз.</summary>
    private byte[]? ProcessMessage(RuntimeMessage message, CancellationToken cancellationToken, bool streaming, long started)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Receive(message);
        if (streaming && message is RuntimePoll { MaxEvents: 0 }) return null;
        if (!streaming && message is RuntimePoll && _outputs.Replay() is { } replay)
            return replay;
        Reserve(message, streaming);

        var outcomes = new List<(RuntimeResult Result, ulong Sequence)>();
        var maximumEvents = streaming ? StreamingEventBudget : MaximumEventsPerReply;
        var applyStarted = Stopwatch.GetTimestamp();
        if (message is RuntimePacket packet)
        {
            outcomes.Add((_runtime.Apply(packet.Input), packet.Input.Sequence));
        }
        else if (message is RuntimeInputBatch batch)
        {
            foreach (var input in batch.Inputs)
            {
                var outcome = _runtime.Apply(input);
                outcomes.Add((outcome, input.Sequence));
                // Не применять хвост после нарушенного causal prefix. DM получит отказ и выполнит resync.
                if (outcome.Apply.Status is not (ApplyStatus.Applied or ApplyStatus.Duplicate)) break;
            }
        }
        else if (message is RuntimePoll poll)
        {
            maximumEvents = Math.Min(poll.MaxEvents, maximumEvents);
        }
        _lastApplyUs = message is RuntimePoll ? 0 : Stopwatch.GetElapsedTime(applyStarted).TotalMicroseconds;
        _maxApplyUs = Math.Max(_maxApplyUs, _lastApplyUs);
        return Complete(outcomes, maximumEvents, !streaming || message is not RuntimePoll, started,
            streaming && message is not RuntimePoll ? 1 : 0);
    }

    /// <summary>ACK/correlation учитываются по каждому wire input, даже если diagnostics нескольких facts пойдут вместе.</summary>
    private void Receive(RuntimeMessage message)
    {
        if (message.Epochs != _epochs)
            throw new ProtocolException("epoch_mismatch");
        if (!_outputs.Acknowledge(message.AckOutputSequence))
            throw new ProtocolException("output_ack_future");
        _receivedMessages++;
        while (_inputReplies.TryPeek(out var reply) && reply <= _outputs.Acknowledged) _inputReplies.Dequeue();
    }

    /// <summary>Группа занимает по slot на каждый input; будущий общий output ACK освободит их все.</summary>
    private void Reserve(RuntimeMessage message, bool streaming, int groupedInputs = 0)
    {
        if (!_outputs.CanReserve)
            throw new ProtocolException("output_pressure");
        if (streaming && message is not RuntimePoll && Limits.MaximumInflightFrames > 0 &&
            (long)_inputReplies.Count + groupedInputs >= Limits.MaximumInflightFrames)
            throw new ProtocolException("input_window_exceeded");
    }

    /// <summary>Один topology Pump и все diagnostics перед событиями сохраняют causal barrier новой публикации.</summary>
    private byte[]? Complete(IReadOnlyList<(RuntimeResult Result, ulong Sequence)> outcomes, int maximumEvents,
        bool allowEmpty, long started, int inputReplies)
    {
        Pump();
        // Deferred build выполняется в Pump; native peer должен увидеть его readiness в том же logical reply.
        var results = new List<byte[]>(outcomes.Count);
        foreach (var (outcome, inputSequence) in outcomes)
        {
            var graph = _runtime.Graph;
            results.Add(RuntimeOutputJson.Result(outcome with
            {
                GraphVersion = graph?.Version ?? 0,
                GraphInputSequence = graph?.InputSequence ?? 0,
                TopologyPending = _runtime.TopologyPending,
                ErrorCode = _runtime.TopologyError ?? outcome.ErrorCode
            }, inputSequence, _runtime));
        }
        var response = Pack(results, maximumEvents, allowEmpty, started, inputReplies);
        if (response is null) return null;
        _outputs.Append(response);
        for (var index = 0; index < inputReplies; index++) _inputReplies.Enqueue(_outputs.LastSequence);
        return response;
    }

    /// <summary>Неизвестные native-эффекты не повторяются после потери соединения.</summary>
    public void Disconnected() => _runtime.MarkDisconnected();

    /// <summary>Освобождает доменное staging/queues; journal принадлежал только этой connection-сессии.</summary>
    public void Dispose() => _runtime.Dispose();

    /// <summary>Измеряет работу Core отдельно от decode, ACK, serialization и ожидания TCP.</summary>
    private void Pump()
    {
        var started = Stopwatch.GetTimestamp();
        _runtime.Pump();
        _lastPumpUs = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        _maxPumpUs = Math.Max(_maxPumpUs, _lastPumpUs);
    }

    /// <summary>Формирует ограниченный кадр; event переходит в журнал до передачи сетевому writer.</summary>
    private byte[]? Pack(IReadOnlyList<byte[]> results, int maximumEvents, bool allowEmpty, long started,
        int inputReplies = 0, bool push = false)
    {
        var entries = new List<byte[]>();
        var overhead = _streaming ? 2048 : 1024;
        var total = overhead;
        foreach (var result in results)
        {
            entries.Add(result);
            total += result.Length + 1;
        }
        for (var i = 0; i < maximumEvents; i++)
        {
            var entry = _pendingEvent;
            if (entry is null)
            {
                if (!_runtime.TryDequeueEvent(out var next) || next is null)
                    break;
                entry = RuntimeOutputJson.Event(next);
            }
            if (entry.Length + overhead > FramedStream.MaxFrameBytes)
                throw new ProtocolException("event_size");
            if (total + entry.Length + 1 > FramedStream.MaxFrameBytes)
            {
                _pendingEvent = entry;
                break;
            }
            entries.Add(entry);
            total += entry.Length + 1;
            _pendingEvent = null;
        }
        if (!allowEmpty && entries.Count == 0) return null;
        var processingUs = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        _maxProcessingUs = Math.Max(_maxProcessingUs, processingUs);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "output");
            writer.WriteString("world_epoch", _epochs.World);
            writer.WriteString("engine_epoch", _epochs.Engine);
            writer.WriteString("binding_epoch", _epochs.Binding);
            RuntimeOutputJson.Counter(writer, "output_sequence", _outputs.LastSequence + 1);
            RuntimeOutputJson.Counter(writer, "applied_sequence", _runtime.AppliedSequence);
            if (_streaming)
            {
                writer.WriteStartObject("host_metrics");
                writer.WriteNumber("input_unacked", _inputReplies.Count + inputReplies);
                writer.WriteNumber("output_unacked", _outputs.LastSequence - _outputs.Acknowledged + 1);
                writer.WriteNumber("received_messages", _receivedMessages);
                writer.WriteNumber("push_outputs", _pushedOutputs + (push ? 1UL : 0));
                writer.WriteNumber("coalesced_fact_frames", _coalescedFactFrames);
                writer.WriteNumber("apply_us", _lastApplyUs);
                writer.WriteNumber("pump_us", _lastPumpUs);
                writer.WriteNumber("max_apply_us", _maxApplyUs);
                writer.WriteNumber("max_pump_us", _maxPumpUs);
                writer.WriteNumber("processing_us", processingUs);
                writer.WriteNumber("max_processing_us", _maxProcessingUs);
                writer.WriteEndObject();
            }
            writer.WriteStartArray("results");
            foreach (var entry in entries)
                writer.WriteRawValue(entry, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.Length > FramedStream.MaxFrameBytes)
            throw new ProtocolException("frame_size");
        return buffer.ToArray();
    }
}

/// <summary>Фабрика самостоятельных runtime binding-сессий с общей проверенной конфигурацией.</summary>
public sealed class RuntimeHostSessionFactory(RuntimeOptions? options = null, StreamingHostLimits? streamingLimits = null) : IHostSessionFactory
{
    /// <summary>Не разделяет mutable состояние разных миров или соединений.</summary>
    public IHostSession Create(Epochs epochs) => new RuntimeHostSession(epochs, options, streamingLimits);
}
