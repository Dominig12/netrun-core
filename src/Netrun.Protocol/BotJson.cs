using System.Text.Json;
using Netrun.Core.Access;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Entities.Combat;
using Netrun.Core.Runtime;
using Netrun.Core.Storage;

namespace Netrun.Protocol;

/// <summary>Closed bot wire intents accept raw identities/credentials, never externally computed perception.</summary>
public static partial class RuntimeJson
{
    private static bool IsBotWireKind(string kind) => kind is "bot_configure" or "bot_remove" or "bot_order" or
        "bot_attacker" or "bot_wake" or "bot_effect_result" or "bot_query";

    private static RuntimeCommand ReadBotCommand(JsonElement entry)
    {
        RuntimeCommand command;
        switch (Text(entry.GetProperty("kind"), 128))
        {
            case "bot_configure":
                Fields(entry, "kind", "request_id", "configuration");
                command = new ConfigureBot(RequestId(entry.GetProperty("request_id")), ReadBotConfiguration(entry.GetProperty("configuration")));
                break;
            case "bot_remove":
                Fields(entry, "kind", "request_id", "bot");
                command = new RemoveBot(RequestId(entry.GetProperty("request_id")), ReadBotKey(entry.GetProperty("bot")));
                break;
            case "bot_order":
                Fields(entry, "kind", "request_id", "bot", "order", "issuer");
                var order = entry.GetProperty("order");
                Fields(order, "kind", "target", "command_id");
                var target = order.GetProperty("target");
                command = new SubmitBotOrder(RequestId(entry.GetProperty("request_id")), ReadBotKey(entry.GetProperty("bot")),
                    new(Text(order.GetProperty("kind"), 128), target.ValueKind == JsonValueKind.Null ? null : ReadNode(target),
                        NullableText(order.GetProperty("command_id"), 128)), ReadActor(entry.GetProperty("issuer")));
                break;
            case "bot_attacker":
                Fields(entry, "kind", "request_id", "bot", "attacker");
                command = new RememberBotAttacker(RequestId(entry.GetProperty("request_id")), ReadBotKey(entry.GetProperty("bot")),
                    ReadActor(entry.GetProperty("attacker")));
                break;
            case "bot_wake":
                Fields(entry, "kind", "request_id", "bot");
                command = new WakeBot(RequestId(entry.GetProperty("request_id")), ReadBotKey(entry.GetProperty("bot")));
                break;
            case "bot_query":
                Fields(entry, "kind", "request_id", "bot");
                command = new QueryBot(RequestId(entry.GetProperty("request_id")), ReadBotKey(entry.GetProperty("bot")));
                break;
            case "bot_effect_result":
                Fields(entry, "kind", "request_id", "result");
                var result = entry.GetProperty("result");
                Fields(result, "bot", "actor", "action_serial", "expected_revision", "phase", "response");
                command = new FinishBotEffect(RequestId(entry.GetProperty("request_id")),
                    new(ReadBotKey(result.GetProperty("bot")), ReadActor(result.GetProperty("actor")),
                        Counter(result.GetProperty("action_serial")), Counter(result.GetProperty("expected_revision")),
                        Text(result.GetProperty("phase"), 32), NullableText(result.GetProperty("response"), 4096)));
                break;
            default: throw Schema();
        }
        ValidateBotCommand(command);
        return command;
    }

    private static BotConfiguration ReadBotConfiguration(JsonElement configuration)
    {
        Fields(configuration, "bot", "actor", "program_id", "access", "home", "owner", "inventory", "behavior", "console_available", "goals_enabled");
        var home = configuration.GetProperty("home");
        var owner = configuration.GetProperty("owner");
        var inventory = configuration.GetProperty("inventory");
        var behavior = configuration.GetProperty("behavior");
        if (behavior.ValueKind != JsonValueKind.Null) Fields(behavior, "strategy", "aggression", "caution", "cooperation");
        return new(ReadBotKey(configuration.GetProperty("bot")), ReadActor(configuration.GetProperty("actor")),
            Text(configuration.GetProperty("program_id"), 128), EntityDataJson.Read<EntityAccessSnapshot>(configuration.GetProperty("access")),
            home.ValueKind == JsonValueKind.Null ? null : ReadNode(home),
            owner.ValueKind == JsonValueKind.Null ? null : ReadActor(owner),
            inventory.ValueKind == JsonValueKind.Null ? null : EntityDataJson.Read<StoreKey>(inventory),
            behavior.ValueKind == JsonValueKind.Null ? null : EntityDataJson.Read<BotBehavior>(behavior),
            Boolean(configuration.GetProperty("console_available")), Boolean(configuration.GetProperty("goals_enabled")));
    }

    private static BotKey ReadBotKey(JsonElement value)
    {
        Fields(value, "bot_id", "generation");
        return new(Text(value.GetProperty("bot_id"), 128), Counter(value.GetProperty("generation")));
    }

    /// <summary>Use the same explicit bot identity for configuration, commands and immutable read models.</summary>
    public static void WriteBotKey(Utf8JsonWriter writer, BotKey key)
    {
        ValidateText(key.Id, 128);
        if (key.Generation == 0) throw Schema();
        writer.WriteStartObject();
        writer.WriteString("bot_id", key.Id);
        writer.WriteString("generation", Decimal(key.Generation));
        writer.WriteEndObject();
    }

    private static void ValidateBotCommand(RuntimeCommand command)
    {
        if (!BotCommandRules.IsValid(command) || command is SubmitBotOrder { Issuer: null }) throw Schema();
    }

    private static void WriteBotCommand(Utf8JsonWriter writer, RuntimeCommand command)
    {
        switch (command)
        {
            case ConfigureBot configure:
                WriteBotCommandHeader(writer, "bot_configure", configure.Key);
                writer.WritePropertyName("configuration");
                WriteBotConfiguration(writer, configure.Configuration);
                break;
            case RemoveBot remove:
                WriteBotCommandHeader(writer, "bot_remove", remove.Key);
                writer.WritePropertyName("bot"); WriteBotKey(writer, remove.Bot);
                break;
            case SubmitBotOrder order:
                WriteBotCommandHeader(writer, "bot_order", order.Key);
                writer.WritePropertyName("bot"); WriteBotKey(writer, order.Bot);
                writer.WritePropertyName("order"); EntityDataJson.Write(writer, order.Order);
                writer.WritePropertyName("issuer"); EntityDataJson.Write(writer, order.Issuer);
                break;
            case RememberBotAttacker attacker:
                WriteBotCommandHeader(writer, "bot_attacker", attacker.Key);
                writer.WritePropertyName("bot"); WriteBotKey(writer, attacker.Bot);
                writer.WritePropertyName("attacker"); EntityDataJson.Write(writer, attacker.Attacker);
                break;
            case WakeBot wake:
                WriteBotCommandHeader(writer, "bot_wake", wake.Key);
                writer.WritePropertyName("bot"); WriteBotKey(writer, wake.Bot);
                break;
            case QueryBot query:
                WriteBotCommandHeader(writer, "bot_query", query.Key);
                writer.WritePropertyName("bot"); WriteBotKey(writer, query.Bot);
                break;
            case FinishBotEffect finish:
                WriteBotCommandHeader(writer, "bot_effect_result", finish.Key);
                writer.WriteStartObject("result");
                writer.WritePropertyName("bot"); WriteBotKey(writer, finish.Result.Bot);
                writer.WritePropertyName("actor"); EntityDataJson.Write(writer, finish.Result.Actor);
                writer.WriteString("action_serial", Decimal(finish.Result.ActionSerial));
                writer.WriteString("expected_revision", Decimal(finish.Result.ExpectedRevision));
                writer.WriteString("phase", finish.Result.Phase);
                writer.WriteString("response", finish.Result.Response);
                writer.WriteEndObject();
                break;
            default: throw Schema();
        }
    }

    private static void WriteBotCommandHeader(Utf8JsonWriter writer, string kind, Netrun.Core.Transport.RequestKey key)
    { writer.WriteString("kind", kind); writer.WriteString("request_id", key.Id); }

    private static void WriteBotConfiguration(Utf8JsonWriter writer, BotConfiguration configuration)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("bot"); WriteBotKey(writer, configuration.Bot);
        writer.WritePropertyName("actor"); EntityDataJson.Write(writer, configuration.Actor);
        writer.WriteString("program_id", configuration.ProgramId);
        writer.WritePropertyName("access"); EntityDataJson.Write(writer, configuration.Access);
        writer.WritePropertyName("home"); EntityDataJson.Write(writer, configuration.Home);
        writer.WritePropertyName("owner"); EntityDataJson.Write(writer, configuration.Owner);
        writer.WritePropertyName("inventory"); EntityDataJson.Write(writer, configuration.Inventory);
        writer.WritePropertyName("behavior"); EntityDataJson.Write(writer, configuration.Behavior);
        writer.WriteBoolean("console_available", configuration.ConsoleAvailable);
        writer.WriteBoolean("goals_enabled", configuration.GoalsEnabled);
        writer.WriteEndObject();
    }

    /// <summary>Serialize only declared read-model fields; pending decisions never become executable CLR types.</summary>
    public static void WriteBotView(Utf8JsonWriter writer, BotView? view)
    {
        if (view is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName("bot"); WriteBotKey(writer, view.Bot);
        writer.WritePropertyName("actor"); EntityDataJson.Write(writer, view.Actor);
        writer.WriteString("revision", Decimal(view.Revision));
        writer.WriteString("state", view.State); writer.WriteString("order", view.Order);
        writer.WritePropertyName("goals"); EntityDataJson.Write(writer, view.Goals);
        writer.WritePropertyName("pending_effect");
        if (view.PendingEffect is { } pending) WriteBotEffect(writer, pending); else writer.WriteNullValue();
        writer.WriteBoolean("visitor_departing", view.VisitorDeparting);
        writer.WriteBoolean("visitor_completed", view.VisitorCompleted);
        writer.WriteString("visitor_objective", view.VisitorObjective);
        writer.WriteNumber("visitor_progress", view.VisitorProgress);
        writer.WriteNumber("visitor_required", view.VisitorRequired);
        if (view.NextWakeDs is { } nextWake) writer.WriteNumber("next_wake_ds", nextWake); else writer.WriteNull("next_wake_ds");
        writer.WriteEndObject();
    }

    /// <summary>Closed causal decision shape, shared by read-only pending effects and allowlisted native events.</summary>
    public static void WriteBotEffect(Utf8JsonWriter writer, BotEffect effect)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("bot"); WriteBotKey(writer, effect.Bot);
        writer.WritePropertyName("actor"); EntityDataJson.Write(writer, effect.Actor);
        writer.WriteString("action_serial", Decimal(effect.ActionSerial));
        writer.WriteString("expected_revision", Decimal(effect.ExpectedRevision));
        writer.WriteString("residence_revision", Decimal(effect.ResidenceRevision));
        writer.WritePropertyName("source"); EntityDataJson.Write(writer, effect.Source);
        writer.WriteStartObject("action");
        switch (effect.Action)
        {
            case BotNavigate action: WriteBotActionData(writer, "navigate", action); break;
            case BotCommand action: WriteBotActionData(writer, "command", action); break;
            case BotRecover action: WriteBotActionData(writer, "recover", action); break;
            case BotRecall action: WriteBotActionData(writer, "recall", action); break;
            case BotDissolve action: WriteBotActionData(writer, "dissolve", action); break;
            case BotLeaveNetwork action: WriteBotActionData(writer, "leave_network", action); break;
            case BotStartBreach action: WriteBotActionData(writer, "start_breach", action); break;
            case BotCancelAction action: WriteBotActionData(writer, "cancel_action", action); break;
            case BotCombat action:
                writer.WriteString("kind", "combat"); writer.WriteStartObject("data");
                writer.WriteStartObject("intent"); WriteBotCombatIntent(writer, action.Intent); writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case BotWarning action: WriteBotActionData(writer, "warning", action); break;
            case BotStoreReport action: WriteBotActionData(writer, "store_report", action); break;
            case BotIncident action: WriteBotActionData(writer, "incident", action); break;
            case BotSabotage action: WriteBotActionData(writer, "sabotage", action); break;
            default: throw Schema();
        }
        writer.WriteEndObject();
        writer.WriteNumber("deadline_ds", effect.DeadlineDs);
        writer.WriteEndObject();
    }

    private static void WriteBotActionData<T>(Utf8JsonWriter writer, string kind, T action)
    { writer.WriteString("kind", kind); writer.WritePropertyName("data"); EntityDataJson.Write(writer, action); }

    private static void WriteBotCombatIntent(Utf8JsonWriter writer, CombatIntent intent)
    {
        switch (intent)
        {
            case AdmitCombatAction action: WriteCombatIntent(writer, "admit", action); break;
            case JoinCombat action: WriteCombatIntent(writer, "join", action); break;
            case PassCombatTurn action: WriteCombatIntent(writer, "pass", action); break;
            case UseCombatProgram action: WriteCombatIntent(writer, "use_program", action); break;
            case ResolveCombatCast action: WriteCombatIntent(writer, "resolve_cast", action); break;
            case CancelCombatCast action: WriteCombatIntent(writer, "cancel_cast", action); break;
            case SpendCombatAp action: WriteCombatIntent(writer, "spend_ap", action); break;
            case ResolveCombatAction action: WriteCombatIntent(writer, "resolve_action", action); break;
            default: throw Schema();
        }
    }
}
