using System.Security.Cryptography;

namespace Netrun.Core;

/// <summary>Ограниченный semantic replay-журнал без сохранения самих пакетов.</summary>
internal sealed class AppliedReplayWindow(int capacity)
{
    /// <summary>Хэши только успешно применённых пакетов; вытеснённый sequence не считается новым.</summary>
    private readonly Dictionary<ulong, byte[]> _hashes = [];
    /// <summary>FIFO порядка применения для O(1)-вытеснения.</summary>
    private readonly Queue<ulong> _order = [];

    /// <summary>Сравнивает содержимое только внутри сохранённого окна; неизвестный старый пакет требует resync.</summary>
    internal bool Matches(ulong sequence, ReadOnlySpan<byte> fingerprint) =>
        _hashes.TryGetValue(sequence, out var previous) && CryptographicOperations.FixedTimeEquals(previous, fingerprint);

    /// <summary>Забирает владение новым массивом fingerprint; вызывается ровно один раз после публикации.</summary>
    internal void Record(ulong sequence, byte[] fingerprint)
    {
        _hashes.Add(sequence, fingerprint);
        _order.Enqueue(sequence);
        if (_order.Count > capacity)
            _hashes.Remove(_order.Dequeue());
    }
}
