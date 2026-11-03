using System.Text.Json;
using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Runtime;

namespace Netrun.Protocol;

public static partial class RuntimeJson
{
    /// <summary>Allowlisted actor intent; payload не содержит proc names или исполняемого DM кода.</summary>
    private static SubmitEntityOperation ReadEntityOperation(JsonElement entry)
    {
        Fields(entry, "kind", "request_id", "actor", "expected_revision", "action", "access");
        var actor = ReadActor(entry.GetProperty("actor"));
        var revision = entry.GetProperty("expected_revision");
        var action = entry.GetProperty("action");
        Fields(action, "kind", "data");
        var data = action.GetProperty("data");
        EntityAction body = Text(action.GetProperty("kind"), 32) switch
        {
            "create" => EntityDataJson.Read<CreateEntity>(data),
            "delete" => EntityDataJson.Read<DeleteEntity>(data),
            "damage" => EntityDataJson.Read<DamageEntity>(data),
            "heal" => EntityDataJson.Read<HealEntity>(data),
            "stun" => EntityDataJson.Read<StunEntity>(data),
            "shield" => EntityDataJson.Read<ShieldEntity>(data),
            "move" => EntityDataJson.Read<MoveEntity>(data),
            "control" => EntityDataJson.Read<ControlEntity>(data),
            "persona" => EntityDataJson.Read<SetEntityPersona>(data),
            "faction" => EntityDataJson.Read<SetEntityFaction>(data),
            "read" => EntityDataJson.Read<ReadEntity>(data),
            _ => throw Schema()
        };
        var operation = new EntityOperation(RequestId(entry.GetProperty("request_id")), actor,
            revision.ValueKind == JsonValueKind.Null ? null : Counter(revision), body);
        var access = entry.GetProperty("access");
        var result = new SubmitEntityOperation(operation, access.ValueKind == JsonValueKind.Null ? null : EntityDataJson.Read<EntityAccessSnapshot>(access));
        ValidateCommand(result);
        return result;
    }

    /// <summary>Native результат сообщает только outcome exact эффекта, не подменяет actor state.</summary>
    private static FinishEntityEffect ReadEntityEffectResult(JsonElement entry)
    {
        Fields(entry, "kind", "request_id", "binding_epoch", "actor", "effect_id", "residence_revision", "success");
        var result = new FinishEntityEffect(RequestId(entry.GetProperty("request_id")),
            new(Text(entry.GetProperty("binding_epoch"), 128), ReadActor(entry.GetProperty("actor")),
                Text(entry.GetProperty("effect_id"), 256), Counter(entry.GetProperty("residence_revision")), Boolean(entry.GetProperty("success"))));
        ValidateCommand(result);
        return result;
    }

    /// <summary>Читает одно точное поколение процесса; адреса и REF здесь не интерпретируются.</summary>
    private static ActorKey ReadActor(JsonElement value)
    {
        Fields(value, "actor_id", "generation");
        return new(Text(value.GetProperty("actor_id"), 128), Counter(value.GetProperty("generation")));
    }

    private static void WriteEntityOperation(Utf8JsonWriter writer, SubmitEntityOperation entity)
    {
        writer.WriteString("kind", "entity_operation");
        writer.WriteString("request_id", entity.Operation.Key.Id);
        writer.WritePropertyName("actor"); EntityDataJson.Write(writer, entity.Operation.Actor);
        if (entity.Operation.ExpectedRevision is { } revision) writer.WriteString("expected_revision", Decimal(revision));
        else writer.WriteNull("expected_revision");
        writer.WriteStartObject("action");
        switch (entity.Operation.Action)
        {
            case CreateEntity data: WriteEntityAction(writer, "create", data); break;
            case DeleteEntity data: WriteEntityAction(writer, "delete", data); break;
            case DamageEntity data: WriteEntityAction(writer, "damage", data); break;
            case HealEntity data: WriteEntityAction(writer, "heal", data); break;
            case StunEntity data: WriteEntityAction(writer, "stun", data); break;
            case ShieldEntity data: WriteEntityAction(writer, "shield", data); break;
            case MoveEntity data: WriteEntityAction(writer, "move", data); break;
            case ControlEntity data: WriteEntityAction(writer, "control", data); break;
            case SetEntityPersona data: WriteEntityAction(writer, "persona", data); break;
            case SetEntityFaction data: WriteEntityAction(writer, "faction", data); break;
            case ReadEntity data: WriteEntityAction(writer, "read", data); break;
            default: throw Schema();
        }
        writer.WriteEndObject();
        writer.WritePropertyName("access"); EntityDataJson.Write(writer, entity.Access);
    }

    private static void WriteEntityAction<T>(Utf8JsonWriter writer, string kind, T data)
    {
        writer.WriteString("kind", kind); writer.WritePropertyName("data"); EntityDataJson.Write(writer, data);
    }

    private static void WriteEntityEffectResult(Utf8JsonWriter writer, FinishEntityEffect finish)
    {
        writer.WriteString("kind", "entity_effect_result"); writer.WriteString("request_id", finish.Key.Id);
        writer.WriteString("binding_epoch", finish.Result.BindingEpoch); writer.WritePropertyName("actor"); EntityDataJson.Write(writer, finish.Result.Actor);
        writer.WriteString("effect_id", finish.Result.EffectId); writer.WriteString("residence_revision", Decimal(finish.Result.ResidenceRevision));
        writer.WriteBoolean("success", finish.Result.Success);
    }
}
