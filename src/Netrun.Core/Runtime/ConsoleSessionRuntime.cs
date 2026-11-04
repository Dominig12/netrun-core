using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Scheduling;
using Netrun.Core.Sessions;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>
/// FIFO-подключение владельца консоли-сессий (ConsoleSessionWorld) к общей очереди ядра. DM публикует сырые
/// факты сессии (ConfigureConsoleSession); Core хранит их и выдаёт проекцию для UI-прокси отдельным событием.
/// Синхронный holder — отдельного Pump-лейна не требует, в отличие от brain ботов.
/// </summary>
public sealed partial class NetworkRuntime
{
    /// <summary>Владелец состояния всех консоли-сессий binding; DM хранит лишь проекции.</summary>
    private ConsoleSessionWorld? _consoleSessions;
    /// <summary>Непереданные проекции сессий; тело команды сюда не попадает.</summary>
    private readonly Queue<TransportEvent> _consoleEvents = [];

    /// <summary>Непрочитанные события консоли-сессий для диагностики.</summary>
    public int ConsoleEventCount => _consoleEvents.Count;
    /// <summary>Число действующих сессий; DM хранит лишь проекции.</summary>
    public int ConsoleSessionCount => _consoleSessions?.Count ?? 0;
    /// <summary>Общий предел очереди событий применяется и к лейну консоли.</summary>
    private int ConsoleOutputLimit => _options.Tasks?.MaximumEvents ?? int.MaxValue;

    /// <summary>Создаёт пустой owner сессий; вызывается из InitializeEntities.</summary>
    private void InitializeConsoleSessions() => _consoleSessions = new ConsoleSessionWorld();

    /// <summary>Закрывает binding без повторного выпуска staged проекций при reconnect.</summary>
    private void StopConsoleSessions()
    {
        _consoleSessions = null;
        _consoleEvents.Clear();
    }

    /// <summary>Закрытый allowlist команд консоли-сессии направляется в общую FIFO.</summary>
    private static bool IsConsoleCommand(RuntimeCommand command) =>
        command is ConfigureConsoleSession or RemoveConsoleSession or QueryConsoleSession or SetConsoleWorkspace;

    /// <summary>Только admission/fingerprint; применение к owner выполняет последующая FIFO задача.</summary>
    private TransportAdmission EnqueueConsole(RuntimeCommand command)
    {
        var key = ConsoleCommandKey(command);
        if (State != MirrorSyncState.Ready) return new(AdmissionStatus.Rejected, "world_unavailable", key);
        var fingerprint = ConsoleCommandFingerprint(command);
        return fingerprint is null ? new(AdmissionStatus.Rejected, "console_command_invalid", key) :
            _tasks!.Enqueue(key, new EntityWork(command), fingerprint);
    }

    /// <summary>Одна FIFO задача применяет команду к owner и публикует проекцию сессии.</summary>
    private CoreTaskStep ExecuteConsoleTask(EntityWork work, int budget)
    {
        if (budget <= 0) return new(0, false);
        if (_consoleSessions is null) return new(1, true, "console_unavailable", false);
        if (_consoleEvents.Count >= ConsoleOutputLimit) return new(0, false);
        var (session, accepted, code) = ApplyConsoleCommand(work.Command);
        _consoleEvents.Enqueue(new ConsoleSessionCompleted(_epochs.Binding, ConsoleCommandKey(work.Command),
            session, accepted, code, ProjectConsoleSession(session)));
        return new(1, true, code, accepted);
    }

    /// <summary>
    /// Проекция сессии, обогащённая производными полями Core: доступность подключения берётся из владельцев
    /// topology/availability, а не из DM-факта. Так Core действительно владеет вычислением, а не эхом.
    /// </summary>
    private ConsoleSessionView? ProjectConsoleSession(ConsoleSessionKey session)
    {
        var view = _consoleSessions!.Query(session);
        if (view is { Connection: { } connection })
        {
            var available = _availability.Get(connection);
            view = view with { ConnectionOnline = Graph?.Nodes.ContainsKey(connection) == true && available.Present && available.Online };
        }
        return view;
    }

    /// <summary>Применяет ровно одну команду к owner сессий.</summary>
    private (ConsoleSessionKey Session, bool Accepted, string Code) ApplyConsoleCommand(RuntimeCommand command) => command switch
    {
        ConfigureConsoleSession configure => (configure.Fact.Key, _consoleSessions!.Upsert(configure.Fact), "console_configured"),
        RemoveConsoleSession remove => (remove.Session, _consoleSessions!.Remove(remove.Session), "console_removed"),
        QueryConsoleSession query => (query.Session, true, "console_query"),
        SetConsoleWorkspace workspace => (workspace.Session, _consoleSessions!.SetWorkspace(workspace.Session, workspace.Workspace), "console_workspace"),
        _ => throw new InvalidOperationException("Invalid console command.")
    };

    /// <summary>Output dequeue не продвигает часы и ничего не исполняет в native мире.</summary>
    private bool TryConsoleEvent(out TransportEvent? output) => _consoleEvents.TryDequeue(out output);

    /// <summary>Исходный producer:serial команды консоли для ACK и дедупликации.</summary>
    private static RequestKey ConsoleCommandKey(RuntimeCommand command) => command switch
    {
        ConfigureConsoleSession configure => configure.Key,
        RemoveConsoleSession remove => remove.Key,
        QueryConsoleSession query => query.Key,
        SetConsoleWorkspace workspace => workspace.Key,
        _ => throw new InvalidOperationException("Invalid console command.")
    };

    /// <summary>Полный deterministic fingerprint команды консоли для runtime replay и FIFO producer fences.</summary>
    internal static byte[]? ConsoleCommandFingerprint(RuntimeCommand command)
    {
        if (!ConsoleSessionRules.IsValid(command)) return null;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            switch (command)
            {
                case ConfigureConsoleSession configure:
                    writer.Write((byte)1); Key(writer, configure.Key); var fact = configure.Fact;
                    Session(writer, fact.Key); writer.Write(fact.Revision); writer.Write(fact.OperatorId);
                    writer.Write(fact.Origin); writer.Write(fact.Workspace);
                    writer.Write(fact.Connection is not null);
                    if (fact.Connection is { } connection) { writer.Write(connection.NodeId); writer.Write(connection.Generation); }
                    writer.Write(fact.Locked); writer.Write(fact.LockUntilDs); writer.Write(fact.Trace);
                    break;
                case RemoveConsoleSession remove:
                    writer.Write((byte)2); Key(writer, remove.Key); Session(writer, remove.Session); break;
                case QueryConsoleSession query:
                    writer.Write((byte)3); Key(writer, query.Key); Session(writer, query.Session); break;
                case SetConsoleWorkspace workspace:
                    writer.Write((byte)4); Key(writer, workspace.Key); Session(writer, workspace.Session); writer.Write(workspace.Workspace); break;
                default: return null;
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));

        static void Key(BinaryWriter writer, RequestKey key) { writer.Write(key.ProducerId); writer.Write(key.Sequence); }
        static void Session(BinaryWriter writer, ConsoleSessionKey key) { writer.Write(key.Id); writer.Write(key.Generation); }
    }
}
