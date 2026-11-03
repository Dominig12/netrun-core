using System.Text.Json;
using Netrun.Core.Entities.Combat;
using Netrun.Core.Runtime;

namespace Netrun.Protocol;

/// <summary>Закрытый combat codec; типы программ/политики и derived actor state не приходят из JSON.</summary>
public static partial class RuntimeJson
{
    /// <summary>Два allowlisted wire входа; произвольный kind не маршрутизируется в CLR/DM имя.</summary>
    private static bool IsCombatWireKind(string kind) => kind is "combat_intent" or "combat_actor_configure";

    /// <summary>Читает полную mandatory shape с actor generations только в canonical decimal strings.</summary>
    private static RuntimeCommand ReadCombatCommand(JsonElement entry)
    {
        RuntimeCommand command;
        if (Text(entry.GetProperty("kind"), 128) == "combat_actor_configure")
        {
            Fields(entry, "kind", "request_id", "actor", "installed_program_ids");
            var programs = entry.GetProperty("installed_program_ids");
            if (programs.ValueKind != JsonValueKind.Array || programs.GetArrayLength() > 4096) throw Schema();
            command = new ConfigureActorCombat(RequestId(entry.GetProperty("request_id")), ReadActor(entry.GetProperty("actor")),
                new(programs.EnumerateArray().Select(item => Text(item, 128))));
        }
        else
        {
            Fields(entry, "kind", "request_id", "intent");
            var intent = entry.GetProperty("intent");
            Fields(intent, "kind", "data");
            var data = intent.GetProperty("data");
            CombatIntent action;
            switch (Text(intent.GetProperty("kind"), 32))
            {
                case "admit":
                    Fields(data, "actor", "target", "cost"); action = EntityDataJson.Read<AdmitCombatAction>(data); break;
                case "join":
                    Fields(data, "actor", "node", "token"); action = EntityDataJson.Read<JoinCombat>(data); break;
                case "pass":
                    Fields(data, "actor", "token"); action = EntityDataJson.Read<PassCombatTurn>(data); break;
                case "use_program":
                    Fields(data, "actor", "program_id", "target", "token", "telegraph", "damage_scale", "executable_id");
                    action = EntityDataJson.Read<UseCombatProgram>(data); break;
                case "resolve_cast":
                    Fields(data, "actor"); action = EntityDataJson.Read<ResolveCombatCast>(data); break;
                case "cancel_cast":
                    Fields(data, "actor"); action = EntityDataJson.Read<CancelCombatCast>(data); break;
                case "spend_ap":
                    Fields(data, "actor", "cost", "deferred", "hostile", "token"); action = EntityDataJson.Read<SpendCombatAp>(data); break;
                case "resolve_action":
                    Fields(data, "actor"); action = EntityDataJson.Read<ResolveCombatAction>(data); break;
                default: throw Schema();
            }
            command = new SubmitCombatIntent(RequestId(entry.GetProperty("request_id")), action);
        }
        ValidateCombatCommand(command);
        return command;
    }

    /// <summary>Root уже открыл command object; helper пишет только его заранее проверенные поля.</summary>
    private static void WriteCombatCommand(Utf8JsonWriter writer, RuntimeCommand command)
    {
        if (command is ConfigureActorCombat configure)
        {
            writer.WriteString("kind", "combat_actor_configure"); writer.WriteString("request_id", configure.Key.Id);
            writer.WritePropertyName("actor"); EntityDataJson.Write(writer, configure.Actor);
            writer.WritePropertyName("installed_program_ids"); EntityDataJson.Write(writer, configure.InstalledProgramIds);
            return;
        }
        var submit = (SubmitCombatIntent)command;
        writer.WriteString("kind", "combat_intent"); writer.WriteString("request_id", submit.Key.Id);
        writer.WriteStartObject("intent");
        switch (submit.Intent)
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
        writer.WriteEndObject();
    }

    /// <summary>Конкретный DTO, не polymorphic type name, задаёт сериализуемую форму.</summary>
    private static void WriteCombatIntent<T>(Utf8JsonWriter writer, string kind, T intent)
    { writer.WriteString("kind", kind); writer.WritePropertyName("data"); EntityDataJson.Write(writer, intent); }

    /// <summary>Одинаковые shape rules применяются перед записью локальной модели и после чтения JSON.</summary>
    private static void ValidateCombatCommand(RuntimeCommand command)
    { if (!CombatCommandRules.IsValid(command)) throw Schema(); }
}
