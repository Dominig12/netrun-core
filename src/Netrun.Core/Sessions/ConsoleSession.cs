using Netrun.Core.Topology;

namespace Netrun.Core.Sessions;

/// <summary>Идентичность консоли-сессии; reconnect/replug использует новое поколение и не наследует прежнее состояние.</summary>
public sealed record ConsoleSessionKey(string Id, ulong Generation);

/// <summary>
/// Сырые факты консоли-сессии, публикуемые DM: оператор, точка подключения (origin), активная рабочая область
/// и блокировка сеанса. Core владеет этим состоянием и выдаёт по нему проекции для UI-прокси. Производные поля
/// (доступность подключения, маршруты, доступы) добавляются при подключении к владельцам Core — здесь их нет.
/// </summary>
public sealed record ConsoleSessionFact(
    ConsoleSessionKey Key,
    ulong Revision,
    string OperatorId,
    string Origin,
    string Workspace,
    NodeKey? Connection = null,
    bool Locked = false,
    int LockUntilDs = 0,
    int Trace = 0);

/// <summary>Неизменяемая проекция сессии для UI; единственный владелец — Core. DM её только отображает.</summary>
public sealed record ConsoleSessionView(
    ConsoleSessionKey Key,
    ulong Revision,
    string OperatorId,
    string Origin,
    string Workspace,
    NodeKey? Connection,
    bool ConnectionOnline,
    bool Locked,
    int LockUntilDs,
    int Trace);

/// <summary>
/// Единоличный владелец состояния консоли-сессий. DM публикует immutable факты; Core хранит их по ключу и выдаёт
/// проекции для UI-прокси. Не выполняет I/O, не вызывает DM procs и не хранит native references.
/// </summary>
public sealed class ConsoleSessionWorld
{
    private readonly Dictionary<string, ConsoleSessionFact> _sessions = [];
    /// <summary>Наибольшее виденное поколение по id: recycled ключ не оживает более ранним поколением.</summary>
    private readonly Dictionary<string, ulong> _generations = [];

    /// <summary>Число действующих сессий для диагностики; DM хранит лишь проекции.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Применяет новую immutable публикацию. Отвергает без мутации более раннее поколение, нестрого возрастающую
    /// revision того же поколения и невалидные поля. Возвращает TRUE только при принятой публикации.
    /// </summary>
    public bool Upsert(ConsoleSessionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!ValidKey(fact.Key) || fact.Revision == 0 || !ValidText(fact.OperatorId) ||
            !ValidText(fact.Origin) || !ValidText(fact.Workspace) || fact.LockUntilDs < 0 || fact.Trace < 0 || !ValidConnection(fact.Connection))
            return false;
        if (_sessions.TryGetValue(fact.Key.Id, out var current))
        {
            if (fact.Key.Generation < current.Key.Generation) return false;
            if (fact.Key == current.Key && fact.Revision <= current.Revision) return false;
        }
        if (_generations.TryGetValue(fact.Key.Id, out var generation) && fact.Key.Generation < generation)
            return false;
        _sessions[fact.Key.Id] = fact;
        _generations[fact.Key.Id] = fact.Key.Generation;
        return true;
    }

    /// <summary>UI→CORE намерение: меняет рабочую область существующей сессии, не трогая прочие факты и revision.</summary>
    public bool SetWorkspace(ConsoleSessionKey key, string workspace)
    {
        if (!ValidKey(key) || !ValidText(workspace) || !_sessions.TryGetValue(key.Id, out var fact) || fact.Key != key)
            return false;
        _sessions[key.Id] = fact with { Workspace = workspace };
        return true;
    }

    /// <summary>Снимает ровно это поколение; поздняя публикация прежнего поколения её не восстановит.</summary>
    public bool Remove(ConsoleSessionKey key) =>
        ValidKey(key) && _sessions.TryGetValue(key.Id, out var current) && current.Key == key && _sessions.Remove(key.Id);

    /// <summary>Неизменяемая проекция точного поколения сессии; чтение не меняет состояние.</summary>
    public ConsoleSessionView? Query(ConsoleSessionKey key)
    {
        if (!ValidKey(key) || !_sessions.TryGetValue(key.Id, out var fact) || fact.Key != key)
            return null;
        return new(fact.Key, fact.Revision, fact.OperatorId, fact.Origin, fact.Workspace, fact.Connection, false, fact.Locked, fact.LockUntilDs, fact.Trace);
    }

    private static bool ValidKey(ConsoleSessionKey? key) => key is not null && key.Generation > 0 && ValidText(key.Id);

    private static bool ValidText(string? text) => text is { Length: > 0 and <= 128 } && !text.Any(char.IsControl);

    private static bool ValidConnection(NodeKey? node) => node is null || node.Generation > 0 && ValidText(node.NodeId);
}
