using System.Security.Cryptography;
using System.Text;

namespace Netrun.Host;

/// <summary>Конфигурация локального IPC; секрет не входит в диагностику или ToString.</summary>
public sealed class LoopbackHostOptions
{
    /// <summary>Байты секрета используются только для constant-time проверки handshake.</summary>
    private readonly byte[] _sessionToken;
    /// <summary>Только loopback TCP, выбранный явно; ноль разрешает ephemeral port в тестах.</summary>
    public int Port { get; }
    /// <summary>Опциональный физический срок handshake; default Infinite не ограничивает внешнюю обработку.</summary>
    public TimeSpan HandshakeTimeout { get; }
    /// <summary>Опциональный срок бездействия I/O; default Infinite отключает искусственный deadline.</summary>
    public TimeSpan IdleTimeout { get; }
    /// <summary>Предел retired binding history одной engine epoch; заполнение переводит Host в новую epoch.</summary>
    public int MaxRetiredBindings { get; }

    /// <summary>Проверяет конфигурацию до открытия listener, не сохраняет строку секрета для логов.</summary>
    public LoopbackHostOptions(int port, string sessionToken, TimeSpan? handshakeTimeout = null, TimeSpan? idleTimeout = null,
        int maxRetiredBindings = 4096)
    {
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (sessionToken is null || sessionToken.Length is < 32 or > 256 || sessionToken.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Invalid session token.", nameof(sessionToken));
        Port = port;
        HandshakeTimeout = handshakeTimeout ?? Timeout.InfiniteTimeSpan;
        IdleTimeout = idleTimeout ?? Timeout.InfiniteTimeSpan;
        if ((HandshakeTimeout != Timeout.InfiniteTimeSpan && HandshakeTimeout <= TimeSpan.Zero) ||
            (IdleTimeout != Timeout.InfiniteTimeSpan && IdleTimeout <= TimeSpan.Zero))
            throw new ArgumentException("Invalid host timeouts.");
        if (maxRetiredBindings is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxRetiredBindings));
        MaxRetiredBindings = maxRetiredBindings;
        _sessionToken = Encoding.UTF8.GetBytes(sessionToken);
    }

    /// <summary>Сравнивает только ограниченный секрет; значение никогда не включается в отказ.</summary>
    internal bool Authenticate(string? token) => token is { Length: >= 32 and <= 256 } &&
        CryptographicOperations.FixedTimeEquals(_sessionToken, Encoding.UTF8.GetBytes(token));

    /// <summary>Диагностика не раскрывает сессионный секрет.</summary>
    public override string ToString() => $"LoopbackHost(port={Port})";
}
