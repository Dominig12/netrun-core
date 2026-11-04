using Netrun.Core.Sessions;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>DM публикует сырые факты консоли-сессии; Core владеет состоянием и выдаёт проекцию для UI-прокси.</summary>
public sealed record ConfigureConsoleSession(RequestKey Key, ConsoleSessionFact Fact) : RuntimeCommand;

/// <summary>Снимает ровно это поколение сессии; поздняя публикация прежнего поколения её не восстановит.</summary>
public sealed record RemoveConsoleSession(RequestKey Key, ConsoleSessionKey Session) : RuntimeCommand;

/// <summary>Читает неизменяемую проекцию сессии, ничего не меняя.</summary>
public sealed record QueryConsoleSession(RequestKey Key, ConsoleSessionKey Session) : RuntimeCommand;

/// <summary>UI→CORE намерение: оператор сменил рабочую область. Core владеет workspace и переиздаёт проекцию.</summary>
public sealed record SetConsoleWorkspace(RequestKey Key, ConsoleSessionKey Session, string Workspace) : RuntimeCommand;

/// <summary>Семантический результат: admission и проекция сессии для UI-прокси (тело команды наружу не идёт).</summary>
public sealed record ConsoleSessionCompleted(string BindingEpoch, RequestKey Key, ConsoleSessionKey Session,
    bool Accepted, string Code, ConsoleSessionView? View) : TransportEvent(BindingEpoch, Key);

/// <summary>Проверка формы команды без авторизации, чтения мира и планирования.</summary>
public static class ConsoleSessionRules
{
    public static bool IsValid(RuntimeCommand? command) => command switch
    {
        ConfigureConsoleSession configure => TransportFingerprint.ValidKey(configure.Key) && ValidFact(configure.Fact),
        RemoveConsoleSession remove => TransportFingerprint.ValidKey(remove.Key) && ValidSession(remove.Session),
        QueryConsoleSession query => TransportFingerprint.ValidKey(query.Key) && ValidSession(query.Session),
        SetConsoleWorkspace workspace => TransportFingerprint.ValidKey(workspace.Key) && ValidSession(workspace.Session) &&
            NodeFactRules.IsValidText(workspace.Workspace, 128),
        _ => false
    };

    public static bool ValidFact(ConsoleSessionFact? fact) => fact is not null && ValidSession(fact.Key) &&
        fact.Revision > 0 && NodeFactRules.IsValidText(fact.OperatorId, 128) && NodeFactRules.IsValidText(fact.Origin, 128) &&
        NodeFactRules.IsValidText(fact.Workspace, 128) && fact.LockUntilDs >= 0 && fact.Trace >= 0 &&
        (fact.Connection is null || fact.Connection.Generation > 0 && NodeFactRules.IsValidText(fact.Connection.NodeId, 128));

    private static bool ValidSession(ConsoleSessionKey? key) =>
        key is not null && key.Generation > 0 && NodeFactRules.IsValidText(key.Id, 128);
}
