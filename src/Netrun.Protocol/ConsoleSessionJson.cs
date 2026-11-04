using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Core.Sessions;
using Netrun.Core.Topology;

namespace Netrun.Protocol;

/// <summary>Closed console-session wire codec: raw session facts in (console_configure), immutable projection out.</summary>
public static partial class RuntimeJson
{
    private static bool IsConsoleWireKind(string kind) => kind is "console_configure" or "console_remove" or "console_query" or "console_set_workspace";

    private static RuntimeCommand ReadConsoleCommand(JsonElement entry)
    {
        RuntimeCommand command;
        switch (Text(entry.GetProperty("kind"), 128))
        {
            case "console_configure":
                Fields(entry, "kind", "request_id", "fact");
                command = new ConfigureConsoleSession(RequestId(entry.GetProperty("request_id")), ReadConsoleSessionFact(entry.GetProperty("fact")));
                break;
            case "console_remove":
                Fields(entry, "kind", "request_id", "session");
                command = new RemoveConsoleSession(RequestId(entry.GetProperty("request_id")), ReadConsoleSessionKey(entry.GetProperty("session")));
                break;
            case "console_query":
                Fields(entry, "kind", "request_id", "session");
                command = new QueryConsoleSession(RequestId(entry.GetProperty("request_id")), ReadConsoleSessionKey(entry.GetProperty("session")));
                break;
            case "console_set_workspace":
                Fields(entry, "kind", "request_id", "session", "workspace");
                command = new SetConsoleWorkspace(RequestId(entry.GetProperty("request_id")), ReadConsoleSessionKey(entry.GetProperty("session")), Text(entry.GetProperty("workspace"), 128));
                break;
            default: throw Schema();
        }
        if (!ConsoleSessionRules.IsValid(command)) throw Schema();
        return command;
    }

    private static ConsoleSessionFact ReadConsoleSessionFact(JsonElement fact)
    {
        Fields(fact, "session", "revision", "operator_id", "origin", "workspace", "connection", "locked", "lock_until_ds", "trace");
        var connection = fact.GetProperty("connection");
        return new(ReadConsoleSessionKey(fact.GetProperty("session")), Counter(fact.GetProperty("revision")),
            Text(fact.GetProperty("operator_id"), 128), Text(fact.GetProperty("origin"), 128), Text(fact.GetProperty("workspace"), 128),
            connection.ValueKind == JsonValueKind.Null ? null : ReadNode(connection),
            Boolean(fact.GetProperty("locked")), Integer(fact.GetProperty("lock_until_ds"), 0, int.MaxValue), Integer(fact.GetProperty("trace"), 0, int.MaxValue));
    }

    private static ConsoleSessionKey ReadConsoleSessionKey(JsonElement value)
    {
        Fields(value, "session_id", "generation");
        return new(Text(value.GetProperty("session_id"), 128), Counter(value.GetProperty("generation")));
    }

    /// <summary>Stable console-session identity, shared by configure, commands and the immutable read model.</summary>
    public static void WriteConsoleSessionKey(Utf8JsonWriter writer, ConsoleSessionKey key)
    {
        ValidateText(key.Id, 128);
        if (key.Generation == 0) throw Schema();
        writer.WriteStartObject();
        writer.WriteString("session_id", key.Id);
        writer.WriteString("generation", Decimal(key.Generation));
        writer.WriteEndObject();
    }

    /// <summary>Serialize only declared read-model fields of the console-session projection.</summary>
    public static void WriteConsoleSessionView(Utf8JsonWriter writer, ConsoleSessionView? view)
    {
        if (view is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName("session"); WriteConsoleSessionKey(writer, view.Key);
        writer.WriteString("revision", Decimal(view.Revision));
        writer.WriteString("operator_id", view.OperatorId);
        writer.WriteString("origin", view.Origin);
        writer.WriteString("workspace", view.Workspace);
        writer.WritePropertyName("connection");
        if (view.Connection is { } connection) WriteConsoleNode(writer, connection); else writer.WriteNullValue();
        writer.WriteBoolean("connection_online", view.ConnectionOnline);
        writer.WriteBoolean("locked", view.Locked);
        writer.WriteNumber("lock_until_ds", view.LockUntilDs);
        writer.WriteNumber("trace", view.Trace);
        writer.WriteEndObject();
    }

    private static void WriteConsoleNode(Utf8JsonWriter writer, NodeKey node)
    {
        writer.WriteStartObject();
        writer.WriteString("node_id", node.NodeId);
        writer.WriteString("generation", Decimal(node.Generation));
        writer.WriteEndObject();
    }
}
