namespace Netrun.Host;

/// <summary>Ограниченное окно уже отправленных кадров; ACK означает обработку DM, а не запись в сокет.</summary>
internal sealed class OutputReplayJournal(int maximumFrames = 64, int maximumBytes = 8 * 1024 * 1024)
{
    /// <summary>Кадры удерживаются до cumulative ACK и не меняются при replay.</summary>
    private readonly Queue<(ulong Sequence, byte[] Body)> _frames = [];
    /// <summary>Занятые байты тел, без сетевого framing header.</summary>
    private int _bytes;
    /// <summary>Последний назначенный output sequence.</summary>
    internal ulong LastSequence { get; private set; }
    /// <summary>Последняя подтверждённая DM граница; старый ACK не откатывает её.</summary>
    internal ulong Acknowledged { get; private set; }
    /// <summary>Резервирует возможность максимального кадра до изменения engine.</summary>
    internal bool CanReserve => _frames.Count < maximumFrames &&
        _bytes <= maximumBytes - Netrun.Protocol.FramedStream.MaxFrameBytes && LastSequence < ulong.MaxValue;

    /// <summary>Удаляет только целиком обработанные кадры; будущий ACK является ошибкой протокола.</summary>
    internal bool Acknowledge(ulong sequence)
    {
        if (sequence > LastSequence)
            return false;
        if (sequence <= Acknowledged)
            return true;
        while (_frames.TryPeek(out var frame) && frame.Sequence <= sequence)
        {
            _frames.Dequeue();
            _bytes -= frame.Body.Length;
        }
        Acknowledged = sequence;
        return true;
    }

    /// <summary>Забирает владение immutable frame body после успешной резервации.</summary>
    internal void Append(byte[] body)
    {
        if (!CanReserve || body.Length > Netrun.Protocol.FramedStream.MaxFrameBytes)
            throw new InvalidOperationException("Output reservation invariant failed.");
        LastSequence++;
        _frames.Enqueue((LastSequence, body));
        _bytes += body.Length;
    }

    /// <summary>Старейший неподтверждённый кадр повторяется byte-for-byte без нового исполнения команды.</summary>
    internal byte[]? Replay() => _frames.TryPeek(out var frame) ? frame.Body : null;
}
