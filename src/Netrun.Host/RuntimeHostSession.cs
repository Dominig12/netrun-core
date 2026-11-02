using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Protocol;

namespace Netrun.Host;

/// <summary>Composition доменной сессии и подтверждаемого output journal после аутентификации.</summary>
public sealed class RuntimeHostSession : IHostSession
{
    /// <summary>Bounded native apply burst: не более 256 projection rows и одного diagnostic result за ответ.</summary>
    private const int MaximumEventsPerReply = 8;
    /// <summary>Идентичность одного соединения; poll не может изменить binding или engine.</summary>
    private readonly Epochs _epochs;
    /// <summary>Единственный mutable владелец mirror, graph, routes и packets.</summary>
    private readonly NetworkRuntime _runtime;
    /// <summary>Кадры удерживаются до обработки DM; запись в сокет не удаляет их из журнала.</summary>
    private readonly OutputReplayJournal _outputs = new();
    /// <summary>Максимум одного не поместившегося immutable события; не теряется при границе кадра.</summary>
    private byte[]? _pendingEvent;

    /// <summary>Создаёт изолированное доменное состояние после успешного hello.</summary>
    public RuntimeHostSession(Epochs epochs, RuntimeOptions? options = null)
    {
        _epochs = epochs;
        _runtime = new NetworkRuntime(epochs, options);
    }

    /// <summary>Проверяет output ACK перед admission; heartbeat не двигает логическое время мира.</summary>
    public ValueTask<byte[]> ProcessAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = RuntimeJson.Decode(body.Span);
        if (message.Epochs != _epochs)
            throw new ProtocolException("epoch_mismatch");
        if (!_outputs.Acknowledge(message.AckOutputSequence))
            throw new ProtocolException("output_ack_future");
        if (message is RuntimePoll && _outputs.Replay() is { } replay)
            return ValueTask.FromResult(replay);
        if (!_outputs.CanReserve)
            throw new ProtocolException("output_pressure");

        byte[]? result = null;
        RuntimeResult? outcome = null;
        ulong inputSequence = 0;
        var maximumEvents = MaximumEventsPerReply;
        if (message is RuntimePacket packet)
        {
            outcome = _runtime.Apply(packet.Input);
            inputSequence = packet.Input.Sequence;
        }
        else if (message is RuntimePoll poll)
        {
            maximumEvents = Math.Min(poll.MaxEvents, MaximumEventsPerReply);
        }
        _runtime.Pump();
        // Deferred build выполняется в Pump; native peer должен увидеть его readiness в том же logical reply.
        if (outcome is not null)
        {
            var graph = _runtime.Graph;
            result = RuntimeOutputJson.Result(outcome with
            {
                GraphVersion = graph?.Version ?? 0,
                GraphInputSequence = graph?.InputSequence ?? 0,
                TopologyPending = _runtime.TopologyPending,
                ErrorCode = _runtime.TopologyError ?? outcome.ErrorCode
            }, inputSequence, _runtime);
        }
        var response = Pack(result, maximumEvents);
        _outputs.Append(response);
        return ValueTask.FromResult(response);
    }

    /// <summary>Неизвестные native-эффекты не повторяются после потери соединения.</summary>
    public void Disconnected() => _runtime.MarkDisconnected();

    /// <summary>Освобождает доменное staging/queues; journal принадлежал только этой connection-сессии.</summary>
    public void Dispose() => _runtime.Dispose();

    /// <summary>Формирует ограниченный кадр; event переходит в журнал до передачи сетевому writer.</summary>
    private byte[] Pack(byte[]? result, int maximumEvents)
    {
        var entries = new List<byte[]>();
        var total = 1024;
        if (result is not null)
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
            if (entry.Length + 1024 > FramedStream.MaxFrameBytes)
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
public sealed class RuntimeHostSessionFactory(RuntimeOptions? options = null) : IHostSessionFactory
{
    /// <summary>Не разделяет mutable состояние разных миров или соединений.</summary>
    public IHostSession Create(Epochs epochs) => new RuntimeHostSession(epochs, options);
}
