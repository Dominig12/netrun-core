using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Access;
using Netrun.Core.Runtime;

namespace Netrun.Protocol;

public static partial class RuntimeJson
{
    /// <summary>Читает ограниченный ACL-снимок native сеанса без выполнения поиска на стороне codec.</summary>
    private static QueryEntityNavigation ReadEntityNavigation(JsonElement entry)
    {
        Fields(entry, "kind", "request_id", "actor", "actor_revision", "source", "destination", "access");
        var actor = entry.GetProperty("actor");
        Fields(actor, "actor_id", "generation");
        var access = EntityDataJson.Read<EntityAccessSnapshot>(entry.GetProperty("access"));
        var request = new EntityNavigationRequest(RequestId(entry.GetProperty("request_id")),
            new ActorKey(Text(actor.GetProperty("actor_id"), 128), Counter(actor.GetProperty("generation"))),
            Counter(entry.GetProperty("actor_revision")), ReadNode(entry.GetProperty("source")),
            ReadNode(entry.GetProperty("destination")), access);
        if (!EntityNavigationBroker.IsValidRequest(request)) throw Schema();
        return new QueryEntityNavigation(request);
    }

    /// <summary>Wire identity покрывает поколение сущности и каждую разрешённую вершину в исходном порядке.</summary>
    private static void WriteEntityNavigation(Utf8JsonWriter writer, EntityNavigationRequest request)
    {
        writer.WriteString("kind", "entity_navigation_query");
        writer.WriteString("request_id", request.Key.Id);
        writer.WriteStartObject("actor");
        writer.WriteString("actor_id", request.Actor.Id);
        writer.WriteString("generation", Decimal(request.Actor.Generation));
        writer.WriteEndObject();
        writer.WriteString("actor_revision", Decimal(request.ActorRevision));
        WriteNode(writer, "source", request.Source);
        WriteNode(writer, "destination", request.Destination);
        writer.WritePropertyName("access");
        EntityDataJson.Write(writer, request.Access);
    }
}
