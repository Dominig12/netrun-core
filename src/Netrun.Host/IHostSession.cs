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

/// <summary>Composition доменных модулей конкретного binding; новая связь создаёт отдельное состояние.</summary>
public interface IHostSessionFactory
{
    /// <summary>Создаёт сессию только после проверки секрета и полей handshake.</summary>
    IHostSession Create(Epochs epochs);
}
