using System.Globalization;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Core.Discovery;
using Netrun.Core.Projection;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Scheduling;
using Netrun.Core.Plugins;
using Netrun.Protocol;

namespace Netrun.Host;

/// <summary>Сериализует ограниченные output records без логирования payload, native references или секретов.</summary>
internal static class RuntimeOutputJson
{
    /// <summary>Один логический ответ на вход; status/ACK независимы от результата request admission.</summary>
    internal static byte[] Result(RuntimeResult result, ulong inputSequence, INetworkWorldView world)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "runtime_result");
            Counter(writer, "input_sequence", inputSequence);
            writer.WriteString("status", Name(result.Apply.Status));
            writer.WriteString("code", result.Apply.Code);
            Counter(writer, "applied_sequence", result.Apply.AppliedSequence);
            writer.WriteNumber("simulation_time_ds", result.Apply.SimulationTimeDs);
            writer.WriteString("sync_state", Name(result.SyncState));
            Counter(writer, "graph_version", result.GraphVersion);
            Counter(writer, "graph_input_sequence", result.GraphInputSequence);
            Counter(writer, "availability_version", result.AvailabilityVersion);
            Counter(writer, "infra_stamp", result.InfrastructureStamp);
            Counter(writer, "av_infra_stamp", result.AvailabilityInfrastructureStamp);
            Counter(writer, "communication_revision", result.CommunicationRevision);
            writer.WriteBoolean("topology_pending", result.TopologyPending);
            writer.WriteString("error_code", result.ErrorCode);
            if (result.CommunicationError is { } communicationError)
                writer.WriteString("communication_error", communicationError);
            writer.WriteNumber("node_count", world.Nodes.Count);
            writer.WriteNumber("sector_count", world.Sectors.Count);
            writer.WriteNumber("service_count", world.Services.Count);
            if (result.Admission is { } admission)
            {
                writer.WriteStartObject("admission");
                writer.WriteString("status", Name(admission.Status));
                writer.WriteString("code", admission.Code);
                writer.WriteString("request_id", admission.Key.Id);
                writer.WriteString("state", admission.State is { } state ? Name(state) : null);
                writer.WriteString("coalesced_into", admission.CoalescedInto?.Id);
                writer.WriteEndObject();
            }
            if (result.Action is { } action)
            {
                writer.WriteStartObject("action");
                writer.WriteBoolean("accepted", action.Accepted);
                writer.WriteString("code", action.Code);
                writer.WriteBoolean("would_block", action.WouldBlock);
                writer.WriteEndObject();
            }
            if (result.Route is { } route)
            {
                writer.WriteStartObject("route");
                writer.WriteString("status", Name(route.Status));
                writer.WriteNumber("latency_ds", route.LatencyDs);
                Counter(writer, "graph_version", route.GraphVersion);
                Counter(writer, "availability_version", route.AvailabilityVersion);
                writer.WriteStartArray("path");
                foreach (var node in route.Path)
                {
                    WriteNode(writer, node);
                    if (writer.BytesCommitted + writer.BytesPending > FramedStream.MaxFrameBytes - 1024)
                        throw new ProtocolException("result_size");
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            if (result.Anchor is { } anchor)
            {
                writer.WriteStartObject("anchor_result");
                writer.WriteString("request_id", anchor.Key.Id);
                writer.WritePropertyName("node");
                if (anchor.Node is { } node) WriteNode(writer, node); else writer.WriteNullValue();
                Counter(writer, "graph_version", anchor.GraphVersion);
                Counter(writer, "graph_input_sequence", anchor.GraphInputSequence);
                writer.WriteString("code", anchor.Code);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Один неизменяемый native invocation либо terminal outcome; event становится доступен только после handoff-проверок.</summary>
    internal static byte[] Event(TransportEvent entry)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("binding_epoch", entry.BindingEpoch);
            writer.WriteString("request_id", entry.Key.Id);
            switch (entry)
            {
                case ServiceInvocation invocation:
                    writer.WriteString("kind", "service_invocation");
                    writer.WriteString("invocation_id", invocation.InvocationId);
                    writer.WritePropertyName("source_service");
                    WriteService(writer, invocation.Source);
                    writer.WritePropertyName("destination_service");
                    WriteService(writer, invocation.Destination);
                    writer.WritePropertyName("source_node");
                    WriteNode(writer, invocation.SourceNode);
                    writer.WritePropertyName("destination_node");
                    WriteNode(writer, invocation.DestinationNode);
                    Counter(writer, "source_node_revision", invocation.SourceNodeRevision);
                    Counter(writer, "destination_node_revision", invocation.DestinationNodeRevision);
                    Counter(writer, "infra_stamp", invocation.InfrastructureStamp);
                    Counter(writer, "av_infra_stamp", invocation.AvailabilityInfrastructureStamp);
                    if (invocation.CommunicationRevision is { } communicationRevision)
                        Counter(writer, "communication_revision", communicationRevision);
                    else
                        writer.WriteNull("communication_revision");
                    writer.WriteString("captured_tag", invocation.CapturedTag);
                    writer.WriteString("operation_id", invocation.OperationId);
                    writer.WritePropertyName("payload");
                    NetworkValueJson.Write(writer, invocation.Payload);
                    if (invocation.DeadlineDs is { } deadline)
                        writer.WriteNumber("deadline_ds", deadline);
                    else
                        writer.WriteNull("deadline_ds");
                    writer.WriteString("authorization_context", invocation.AuthorizationContext);
                    writer.WriteString("native_context", invocation.NativeContext);
                    break;
                case RequestCompleted completed:
                    writer.WriteString("kind", "request_completed");
                    writer.WriteString("state", Name(completed.State));
                    writer.WriteString("code", completed.Code);
                    writer.WritePropertyName("payload");
                    NetworkValueJson.Write(writer, completed.Payload ?? NetworkValue.Null);
                    break;
                case ServiceWatchEvent changed:
                    writer.WriteString("kind", "service_watch_event");
                    writer.WriteString("change", changed.Change);
                    writer.WriteString("family", changed.Family);
                    writer.WriteString("tag", changed.Tag);
                    writer.WritePropertyName("service");
                    if (changed.Service is { } observed) WriteService(writer, observed); else writer.WriteNullValue();
                    writer.WriteBoolean("online", changed.Online);
                    Counter(writer, "directory_revision", changed.DirectoryRevision);
                    break;
                case DiscoveryPage page:
                    writer.WriteString("kind", "discovery_page");
                    writer.WriteStartArray("candidates");
                    foreach (var candidate in page.Candidates) WriteService(writer, candidate);
                    writer.WriteEndArray();
                    writer.WriteBoolean("complete", page.Complete);
                    writer.WriteString("code", page.Code);
                    break;
                case EntityNavigationPage navigation:
                    writer.WriteString("kind", "entity_navigation_page");
                    writer.WriteStartObject("actor");
                    writer.WriteString("actor_id", navigation.Actor.Id);
                    Counter(writer, "generation", navigation.Actor.Generation);
                    writer.WriteEndObject();
                    Counter(writer, "actor_revision", navigation.ActorRevision);
                    writer.WritePropertyName("source");
                    WriteNode(writer, navigation.Source);
                    writer.WritePropertyName("destination");
                    WriteNode(writer, navigation.Destination);
                    Counter(writer, "graph_version", navigation.GraphVersion);
                    Counter(writer, "graph_input_sequence", navigation.GraphInputSequence);
                    Counter(writer, "availability_version", navigation.AvailabilityVersion);
                    Counter(writer, "page_index", navigation.PageIndex);
                    writer.WriteStartArray("nodes");
                    foreach (var node in navigation.Nodes) WriteNode(writer, node);
                    writer.WriteEndArray();
                    writer.WriteBoolean("complete", navigation.Complete);
                    writer.WriteString("code", navigation.Code);
                    break;
                case EntityChanged changed:
                    writer.WriteString("kind", "entity_changed");
                    writer.WritePropertyName("actor"); EntityDataJson.Write(writer, changed.Actor);
                    writer.WritePropertyName("view"); EntityDataJson.Write(writer, changed.View);
                    writer.WriteString("code", changed.Code);
                    break;
                case EntityNativeEffect effect:
                    writer.WriteString("kind", "entity_native_effect");
                    writer.WritePropertyName("actor"); EntityDataJson.Write(writer, effect.Actor);
                    writer.WriteString("effect_id", effect.EffectId);
                    Counter(writer, "residence_revision", effect.ResidenceRevision);
                    writer.WritePropertyName("source"); WriteNode(writer, effect.Source);
                    writer.WritePropertyName("destination"); WriteNode(writer, effect.Destination);
                    break;
                case CoreTaskCompleted completed:
                    writer.WriteString("kind", "core_task_completed");
                    Counter(writer, "task_sequence", completed.TaskSequence);
                    writer.WriteBoolean("success", completed.Success);
                    writer.WriteString("code", completed.Code);
                    break;
                case BotCommandCompleted bot:
                    writer.WriteString("kind", "bot_completed");
                    writer.WritePropertyName("bot"); RuntimeJson.WriteBotKey(writer, bot.Bot);
                    writer.WriteBoolean("accepted", bot.Accepted); writer.WriteString("code", bot.Code);
                    writer.WritePropertyName("view"); RuntimeJson.WriteBotView(writer, bot.View);
                    break;
                case BotNativeDecision bot:
                    if (bot.Effect.Action is not (BotWarning or BotCancelAction))
                        throw new ProtocolException("bot_native_action_invalid");
                    writer.WriteString("kind", "bot_native_decision");
                    writer.WritePropertyName("effect"); RuntimeJson.WriteBotEffect(writer, bot.Effect);
                    break;
                case ModuleCompleted module:
                    writer.WriteString("kind", "module_result"); writer.WriteString("module_id", module.ModuleId);
                    writer.WriteBoolean("success", module.Success); writer.WriteString("code", module.Code);
                    writer.WritePropertyName("result"); NetworkValueJson.Write(writer, module.Result);
                    break;
                case ModuleProjectionChanged projection:
                    writer.WriteString("kind", "module_projection"); writer.WriteString("module_id", projection.ModuleId);
                    writer.WriteString("topic", projection.Topic); writer.WritePropertyName("data"); NetworkValueJson.Write(writer, projection.Data);
                    break;
                case StorageOperationCompleted storage:
                    writer.WriteString("kind", "storage_completed"); writer.WritePropertyName("result");
                    EntityDataJson.Write(writer, storage.Result);
                    break;
                case StorageChangedProjection storage:
                    writer.WriteString("kind", "storage_changed"); writer.WritePropertyName("change");
                    EntityDataJson.Write(writer, storage.Change);
                    break;
                case CombatIntentCompleted combat:
                    writer.WriteString("kind", "combat_completed");
                    writer.WritePropertyName("actor"); EntityDataJson.Write(writer, combat.Actor);
                    writer.WriteBoolean("accepted", combat.Accepted); writer.WriteString("code", combat.Code);
                    writer.WritePropertyName("encounter"); EntityDataJson.Write(writer, combat.Encounter);
                    writer.WritePropertyName("state"); EntityDataJson.Write(writer, combat.State);
                    writer.WritePropertyName("installed_program_ids"); EntityDataJson.Write(writer, combat.InstalledProgramIds);
                    break;
                case CombatStateChanged combat:
                    writer.WriteString("kind", "combat_changed");
                    writer.WritePropertyName("actor"); EntityDataJson.Write(writer, combat.Actor);
                    writer.WritePropertyName("encounter"); EntityDataJson.Write(writer, combat.Encounter);
                    writer.WritePropertyName("state"); EntityDataJson.Write(writer, combat.State);
                    writer.WriteString("code", combat.Code);
                    break;
                case CombatNativeEffect combat:
                    writer.WriteString("kind", "combat_native_effect");
                    writer.WritePropertyName("actor"); EntityDataJson.Write(writer, combat.Actor);
                    writer.WriteString("effect_id", combat.EffectId); Counter(writer, "entity_revision", combat.EntityRevision);
                    Counter(writer, "residence_revision", combat.ResidenceRevision);
                    writer.WritePropertyName("node"); WriteNode(writer, combat.Node);
                    writer.WritePropertyName("effect"); EntityDataJson.Write(writer, combat.Effect);
                    break;
                case ProjectionPage projection:
                    if (projection.Rows.Count > 32) throw new ProtocolException("projection_size");
                    writer.WriteString("kind", "projection_page");
                    writer.WriteString("projection_kind", Name(projection.Kind));
                    Counter(writer, "graph_version", projection.GraphVersion);
                    Counter(writer, "graph_input_sequence", projection.GraphInputSequence);
                    Counter(writer, "availability_version", projection.AvailabilityVersion);
                    Counter(writer, "infra_stamp", projection.InfrastructureStamp);
                    Counter(writer, "av_infra_stamp", projection.AvailabilityInfrastructureStamp);
                    Counter(writer, "source_node_revision", projection.SourceNodeRevision);
                    writer.WritePropertyName("source");
                    if (projection.Source is { } source) WriteNode(writer, source); else writer.WriteNullValue();
                    Counter(writer, "page_index", projection.PageIndex);
                    writer.WriteStartArray("rows");
                    foreach (var row in projection.Rows) WriteProjectionRow(writer, row);
                    writer.WriteEndArray();
                    writer.WriteBoolean("complete", projection.Complete);
                    writer.WriteString("code", projection.Code);
                    break;
                case GraphSynchronizationPage graph:
                    writer.WriteString("kind", "graph_sync_page");
                    Counter(writer, "page_index", graph.PageIndex);
                    writer.WriteString("phase", Name(graph.Phase));
                    Counter(writer, "from_graph_version", graph.FromGraphVersion);
                    Counter(writer, "graph_version", graph.GraphVersion);
                    Counter(writer, "graph_input_sequence", graph.GraphInputSequence);
                    Counter(writer, "infra_stamp", graph.InfrastructureStamp);
                    Counter(writer, "av_infra_stamp", graph.AvailabilityInfrastructureStamp);
                    writer.WriteStartArray("operations");
                    foreach (var operation in graph.Operations) WriteGraphOperation(writer, operation);
                    writer.WriteEndArray();
                    writer.WriteBoolean("batch_complete", graph.BatchComplete);
                    writer.WriteString("code", graph.Code);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported transport output.");
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Типизированный NodeKey не превращается в исполняемый адрес DM.</summary>
    private static void WriteNode(Utf8JsonWriter writer, NodeKey key)
    {
        writer.WriteStartObject();
        writer.WriteString("node_id", key.NodeId);
        Counter(writer, "generation", key.Generation);
        writer.WriteEndObject();
    }

    /// <summary>Service identity включает поколение адаптера, не только service family.</summary>
    private static void WriteService(Utf8JsonWriter writer, ServiceKey key)
    {
        writer.WriteStartObject();
        writer.WriteString("service_id", key.ServiceId);
        Counter(writer, "generation", key.Generation);
        writer.WriteEndObject();
    }

    /// <summary>Allowlisted projection ADT не сериализует произвольные CLR fields, whole facts или native references.</summary>
    private static void WriteProjectionRow(Utf8JsonWriter writer, ProjectionRow row)
    {
        writer.WriteStartObject();
        switch (row)
        {
            case GraphNodeRow node:
                writer.WriteString("kind", "node");
                writer.WritePropertyName("node");
                WriteNode(writer, node.Key);
                writer.WriteString("address", node.Address);
                writer.WritePropertyName("anchor");
                if (node.Anchor is { } anchor) WriteNode(writer, anchor); else writer.WriteNullValue();
                writer.WriteString("lan_id", node.LanId);
                writer.WriteBoolean("is_relay", node.IsRelay);
                break;
            case GraphEdgeRow edge:
                writer.WriteString("kind", "edge");
                writer.WritePropertyName("from");
                WriteNode(writer, edge.Edge.From);
                writer.WritePropertyName("to");
                WriteNode(writer, edge.Edge.To);
                writer.WriteString("link_kind", Name(edge.Edge.Kind));
                writer.WriteNumber("distance", edge.Edge.Distance);
                writer.WriteNumber("latency_ds", edge.Edge.LatencyDs);
                break;
            case RouteRow route:
                writer.WriteString("kind", "route");
                writer.WritePropertyName("node");
                WriteNode(writer, route.Key);
                writer.WritePropertyName("previous");
                if (route.Previous is { } previous) WriteNode(writer, previous); else writer.WriteNullValue();
                writer.WriteNumber("latency_ds", route.LatencyDs);
                writer.WriteBoolean("quantum", route.Quantum);
                Counter(writer, "node_revision", route.NodeRevision);
                break;
            default: throw new InvalidOperationException("Unsupported projection row.");
        }
        writer.WriteEndObject();
    }

    /// <summary>Bounded graph diff использует stable identity; никакие whole facts или исполняемые native ссылки не передаются.</summary>
    private static void WriteGraphOperation(Utf8JsonWriter writer, GraphSynchronizationOperation operation)
    {
        writer.WriteStartObject();
        switch (operation)
        {
            case GraphSynchronizationNodeUpsert upsert:
                writer.WriteString("kind", "node_upsert");
                writer.WritePropertyName("node");
                WriteNode(writer, upsert.Node.Key);
                writer.WriteString("address", upsert.Node.Address);
                writer.WritePropertyName("anchor");
                if (upsert.Node.Anchor is { } anchor) WriteNode(writer, anchor); else writer.WriteNullValue();
                writer.WriteString("lan_id", upsert.Node.LanId);
                writer.WriteBoolean("is_relay", upsert.Node.IsRelay);
                Counter(writer, "node_revision", upsert.NodeRevision);
                writer.WriteBoolean("is_endpoint", upsert.IsEndpoint);
                break;
            case GraphSynchronizationNodeRemove remove:
                writer.WriteString("kind", "node_remove");
                writer.WritePropertyName("node");
                WriteNode(writer, remove.Node);
                break;
            case GraphSynchronizationEdgeUpsert upsert:
                writer.WriteString("kind", "edge_upsert");
                writer.WriteString("edge_id", upsert.EdgeId);
                writer.WritePropertyName("from");
                WriteNode(writer, upsert.Edge.From);
                writer.WritePropertyName("to");
                WriteNode(writer, upsert.Edge.To);
                writer.WriteString("link_kind", Name(upsert.Edge.Kind));
                writer.WriteNumber("distance", upsert.Edge.Distance);
                writer.WriteNumber("latency_ds", upsert.Edge.LatencyDs);
                break;
            case GraphSynchronizationEdgeRemove remove:
                writer.WriteString("kind", "edge_remove");
                writer.WriteString("edge_id", remove.EdgeId);
                break;
            default: throw new InvalidOperationException("Unsupported graph synchronization operation.");
        }
        writer.WriteEndObject();
    }

    /// <summary>Все счётчики wire передаются decimal string независимо от разрядности JSON-получателя.</summary>
    internal static void Counter(Utf8JsonWriter writer, string name, ulong value) =>
        writer.WriteString(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Преобразует известные enum имена в snake_case без пользовательских значений.</summary>
    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
