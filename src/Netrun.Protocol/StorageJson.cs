using System.Text.Json;
using Netrun.Core.Runtime;
using Netrun.Core.Storage;

namespace Netrun.Protocol;

public static partial class RuntimeJson
{
    /// <summary>Только закрытые storage control/user kind; нет wire-доступа к внутреннему expiry intent.</summary>
    private static bool IsStorageWireKind(string kind) => kind is "storage_register" or "storage_remove" or "storage_seed" or "storage_operation";

    /// <summary>Bootstrap facts принимаются только от доверенного native adapter, отдельно от allowlisted UI action.</summary>
    private static RuntimeCommand ReadStorageCommand(JsonElement entry)
    {
        RuntimeCommand command;
        switch (Text(entry.GetProperty("kind"), 32))
        {
            case "storage_register":
                Fields(entry, "kind", "request_id", "definition");
                command = new RegisterStorage(RequestId(entry.GetProperty("request_id")),
                    EntityDataJson.Read<StorageDefinition>(entry.GetProperty("definition")));
                break;
            case "storage_seed":
                Fields(entry, "kind", "request_id", "store", "definition", "preload");
                command = new SeedStorageFile(RequestId(entry.GetProperty("request_id")),
                    EntityDataJson.Read<StoreKey>(entry.GetProperty("store")),
                    EntityDataJson.Read<FileDefinition>(entry.GetProperty("definition")), Boolean(entry.GetProperty("preload")));
                break;
            case "storage_remove":
                Fields(entry, "kind", "request_id", "store");
                command = new RemoveStorage(RequestId(entry.GetProperty("request_id")), EntityDataJson.Read<StoreKey>(entry.GetProperty("store")));
                break;
            case "storage_operation":
                Fields(entry, "kind", "request_id", "actor", "expected_revision", "action");
                var action = entry.GetProperty("action");
                Fields(action, "kind", "data");
                var data = action.GetProperty("data");
                StorageAction body = Text(action.GetProperty("kind"), 32) switch
                {
                    "create" => EntityDataJson.Read<CreateStorageFile>(data),
                    "transfer" => EntityDataJson.Read<TransferStorageFile>(data),
                    "delete" => EntityDataJson.Read<DeleteStorageFile>(data),
                    "edit_text" => EntityDataJson.Read<EditStorageText>(data),
                    "rename" => EntityDataJson.Read<RenameStorageFile>(data),
                    "pack_agent" => EntityDataJson.Read<PackAgentMemory>(data),
                    "restore_agent" => EntityDataJson.Read<RestoreAgentMemory>(data),
                    "offer" => EntityDataJson.Read<OfferStorageFile>(data),
                    "respond_offer" => EntityDataJson.Read<RespondStorageOffer>(data),
                    "read" => EntityDataJson.Read<ReadStorage>(data),
                    _ => throw Schema()
                };
                var revision = entry.GetProperty("expected_revision");
                command = new SubmitStorageOperation(new(RequestId(entry.GetProperty("request_id")),
                    ReadActor(entry.GetProperty("actor")), body,
                    revision.ValueKind == JsonValueKind.Null ? null : Counter(revision)));
                break;
            default: throw Schema();
        }
        ValidateStorageCommand(command);
        return command;
    }

    /// <summary>Проверяет shape до encoding/decode; mutable capacity/ownership проверит только FIFO domain owner.</summary>
    private static void ValidateStorageCommand(RuntimeCommand command)
    {
        switch (command)
        {
            case RegisterStorage register:
                ValidateRequestKey(register.Key);
                if (!StorageWorld.IsValidStoreDefinition(register.Definition)) throw Schema();
                break;
            case SeedStorageFile seed:
                ValidateRequestKey(seed.Key);
                if (StorageWorld.FingerprintOperation(new(seed.Key, new("storage.bootstrap", 1),
                    new CreateStorageFile(seed.Store, seed.Definition))) is null || !StorageWorld.IsValidFileDefinition(seed.Definition)) throw Schema();
                break;
            case SubmitStorageOperation submit:
                if (StorageWorld.FingerprintOperation(submit.Operation) is null) throw Schema();
                break;
            case RemoveStorage remove:
                ValidateRequestKey(remove.Key);
                if (!StorageWorld.IsValidStoreDefinition(new(remove.Store, 0))) throw Schema();
                break;
            default: throw Schema();
        }
    }

    /// <summary>Записывает ровно один allowlisted intent; произвольные типы и private grants не сериализуются как команды.</summary>
    private static void WriteStorageCommand(Utf8JsonWriter writer, RuntimeCommand command)
    {
        switch (command)
        {
            case RegisterStorage register:
                writer.WriteString("kind", "storage_register"); writer.WriteString("request_id", register.Key.Id);
                writer.WritePropertyName("definition"); EntityDataJson.Write(writer, register.Definition);
                break;
            case SeedStorageFile seed:
                writer.WriteString("kind", "storage_seed"); writer.WriteString("request_id", seed.Key.Id);
                writer.WritePropertyName("store"); EntityDataJson.Write(writer, seed.Store);
                writer.WritePropertyName("definition"); EntityDataJson.Write(writer, seed.Definition);
                writer.WriteBoolean("preload", seed.Preload);
                break;
            case RemoveStorage remove:
                writer.WriteString("kind", "storage_remove"); writer.WriteString("request_id", remove.Key.Id);
                writer.WritePropertyName("store"); EntityDataJson.Write(writer, remove.Store);
                break;
            case SubmitStorageOperation submit:
                writer.WriteString("kind", "storage_operation"); writer.WriteString("request_id", submit.Operation.Key.Id);
                writer.WritePropertyName("actor"); EntityDataJson.Write(writer, submit.Operation.Actor);
                if (submit.Operation.ExpectedRevision is { } revision) writer.WriteString("expected_revision", Decimal(revision));
                else writer.WriteNull("expected_revision");
                writer.WriteStartObject("action");
                switch (submit.Operation.Action)
                {
                    case CreateStorageFile data: WriteStorageAction(writer, "create", data); break;
                    case TransferStorageFile data: WriteStorageAction(writer, "transfer", data); break;
                    case DeleteStorageFile data: WriteStorageAction(writer, "delete", data); break;
                    case EditStorageText data: WriteStorageAction(writer, "edit_text", data); break;
                    case RenameStorageFile data: WriteStorageAction(writer, "rename", data); break;
                    case PackAgentMemory data: WriteStorageAction(writer, "pack_agent", data); break;
                    case RestoreAgentMemory data: WriteStorageAction(writer, "restore_agent", data); break;
                    case OfferStorageFile data: WriteStorageAction(writer, "offer", data); break;
                    case RespondStorageOffer data: WriteStorageAction(writer, "respond_offer", data); break;
                    case ReadStorage data: WriteStorageAction(writer, "read", data); break;
                    default: throw Schema();
                }
                writer.WriteEndObject();
                break;
            default: throw Schema();
        }
    }

    private static void WriteStorageAction<T>(Utf8JsonWriter writer, string kind, T data)
    {
        writer.WriteString("kind", kind); writer.WritePropertyName("data"); EntityDataJson.Write(writer, data);
    }
}
