using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Routing;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Core.Discovery;
using Netrun.Core.Projection;
using Netrun.Core.Entities;
using Netrun.Core.Plugins;

namespace Netrun.Protocol;

/// <summary>Вход локального runtime: причинная команда либо poll без продвижения игрового времени.</summary>
/// <param name="Epochs">Идентичность мира, процесса и binding.</param>
/// <param name="AckOutputSequence">Cumulative ACK уже применённых DM выходов, ноль допустим.</param>
public abstract record RuntimeMessage(Epochs Epochs, ulong AckOutputSequence);

/// <summary>Один sequenced logical frame, который полностью обработает NetworkRuntime.</summary>
/// <param name="Input">Причинный ввод с sequence и simulation time.</param>
/// <param name="AckOutputSequence">Cumulative ACK обработки выходов DM.</param>
public sealed record RuntimePacket(RuntimeInput Input, ulong AckOutputSequence) : RuntimeMessage(Input.Epochs, AckOutputSequence);

/// <summary>Ограниченная транспортная группа независимо sequenced команд одного игрового времени.</summary>
/// <param name="Inputs">Последовательные команды; world facts и диагностические route replies передаются отдельно.</param>
/// <param name="AckOutputSequence">Cumulative ACK обработки выходов DM.</param>
public sealed record RuntimeInputBatch(IReadOnlyList<RuntimeInput> Inputs, ulong AckOutputSequence)
    : RuntimeMessage(Inputs[0].Epochs, AckOutputSequence);

/// <summary>Чтение уже готовых выходов; не является входом игрового clock или фактов.</summary>
/// <param name="Epochs">Идентичность выбранного binding.</param>
/// <param name="AckOutputSequence">Cumulative ACK обработки выходов DM.</param>
/// <param name="MaxEvents">Максимум забираемых событий, 0..128.</param>
public sealed record RuntimePoll(Epochs Epochs, ulong AckOutputSequence, int MaxEvents = 64) : RuntimeMessage(Epochs, AckOutputSequence);

/// <summary>Строгая runtime-схема v1.3; команды не смешиваются с world facts внутри одного атомарного frame.</summary>
public static partial class RuntimeJson
{
    /// <summary>Согласуемый hello_ack предел команд; не увеличивает бюджет Pump или native events.</summary>
    public const int MaxInputCommands = 16;
    /// <summary>
    /// Структурный JSON parser ограничен числом пар скобок внутри заранее ограниченного 1-MiB frame.
    /// Payload глубже 32 остаётся valid JSON и получает message-level отказ вместо закрытия binding.
    /// JsonDocument не материализует NetworkValue; его рекурсивный domain reader по-прежнему останавливается на depth 32.
    /// </summary>
    private const int MaxDepth = FramedStream.MaxFrameBytes / 2;
    /// <summary>Некорректный UTF-8 не заменяется на другой идентификатор.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Проверяет размер до JSON-разбора и создаёт typed poll/packet без ссылок на временный документ.</summary>
    public static RuntimeMessage Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length is 0 or > FramedStream.MaxFrameBytes) throw new ProtocolException("frame_size");
        try
        {
            _ = StrictUtf8.GetCharCount(body);
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
            var root = document.RootElement;
            var grouped = false;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var kind))
            {
                grouped = Text(kind, 32) == "input_batch";
                if (!grouped)
                {
                    if (Text(kind, 32) != "poll") throw Schema();
                    Fields(root, "kind", "world_epoch", "engine_epoch", "binding_epoch", "ack_output_sequence", "max_events");
                    return new RuntimePoll(ReadEpochs(root), Counter(root.GetProperty("ack_output_sequence"), true),
                        Integer(root.GetProperty("max_events"), 0, 128));
                }
            }
            if (grouped) Fields(root, "kind", "protocol_major", "protocol_minor", "world_epoch", "engine_epoch", "binding_epoch", "sequence",
                "ack_output_sequence", "simulation_time_ds", "entries");
            else Fields(root, "protocol_major", "protocol_minor", "world_epoch", "engine_epoch", "binding_epoch", "sequence",
                "ack_output_sequence", "simulation_time_ds", "entries");
            if (Integer(root.GetProperty("protocol_major"), 0, int.MaxValue) != 1 ||
                Integer(root.GetProperty("protocol_minor"), 0, int.MaxValue) != 3) throw new ProtocolException("protocol_version");
            var epochs = ReadEpochs(root);
            var sequence = Counter(root.GetProperty("sequence"));
            var ack = Counter(root.GetProperty("ack_output_sequence"), true);
            var time = Integer(root.GetProperty("simulation_time_ds"), 0, int.MaxValue);
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > BatchJson.MaxEntries) throw Schema();
            if (grouped)
            {
                var count = entries.GetArrayLength();
                if (count is < 2 or > MaxInputCommands || sequence < (ulong)count) throw Schema();
                var inputs = new RuntimeInput[count];
                var index = 0;
                // Весь frame проверяется до первого Apply. sequence обозначает конец непрерывной группы.
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("kind", out var entryKind) ||
                        !IsCommand(Text(entryKind, 128))) throw Schema();
                    var item = ReadCommand(entry);
                    if (item is QueryRoute) throw Schema();
                    inputs[index] = new(epochs, sequence - (ulong)count + 1 + (ulong)index, time, item);
                    index++;
                }
                return new RuntimeInputBatch(inputs, ack);
            }
            RuntimeCommand? command = null;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("kind", out var entryKind)) throw Schema();
                if (!IsCommand(Text(entryKind, 128))) continue;
                if (entries.GetArrayLength() != 1) throw Schema();
                command = ReadCommand(entry);
            }
            if (command is null)
            {
                // Не использовать JsonElement.WriteTo: checksum v3 зависит от ТОЧНЫХ bytes changes.
                using var copied = new MemoryStream();
                using (var writer = new Utf8JsonWriter(copied))
                {
                    writer.WriteStartObject();
                    foreach (var property in root.EnumerateObject())
                    {
                        if (property.Name == "ack_output_sequence") continue;
                        writer.WritePropertyName(property.Name);
                        writer.WriteRawValue(property.Value.GetRawText(), skipInputValidation: true);
                    }
                    writer.WriteEndObject();
                }
                var batch = BatchJson.Decode(copied.GetBuffer().AsSpan(0, checked((int)copied.Length)));
                command = new ApplyWorld(batch.Entries);
            }
            return new RuntimePacket(new RuntimeInput(epochs, sequence, time, command), ack);
        }
        catch (ProtocolException) { throw; }
        catch (DecoderFallbackException) { throw new ProtocolException("invalid_utf8"); }
        catch (JsonException) { throw new ProtocolException("invalid_json"); }
        catch (InvalidOperationException) { throw Schema(); }
        catch (ArgumentException) { throw Schema(); }
    }

    /// <summary>Кодирует только последовательные команды; каждая сохраняет собственные dedup и result correlation.</summary>
    public static byte[] EncodeBatch(IReadOnlyList<RuntimeInput> inputs, ulong ackOutputSequence = 0)
    {
        if (inputs is null || inputs.Count is < 2 or > MaxInputCommands) throw Schema();
        var first = inputs[0];
        if (first is null || first.Sequence == 0 || first.SimulationTimeDs < 0 ||
            first.Sequence > ulong.MaxValue - (ulong)(inputs.Count - 1)) throw Schema();
        ValidateEpochs(first.Epochs);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "input_batch");
            writer.WriteNumber("protocol_major", 1);
            writer.WriteNumber("protocol_minor", 3);
            WriteEpochs(writer, first.Epochs);
            writer.WriteString("sequence", Decimal(first.Sequence + (ulong)(inputs.Count - 1)));
            writer.WriteString("ack_output_sequence", Decimal(ackOutputSequence));
            writer.WriteNumber("simulation_time_ds", first.SimulationTimeDs);
            writer.WriteStartArray("entries");
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                if (input is null || input.Sequence != first.Sequence + (ulong)i || input.Epochs != first.Epochs ||
                    input.SimulationTimeDs != first.SimulationTimeDs || input.Command is null or ApplyWorld or QueryRoute) throw Schema();
                ValidateCommand(input.Command);
                WriteCommand(writer, input.Command);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.Length > FramedStream.MaxFrameBytes) throw new ProtocolException("frame_size");
        return buffer.ToArray();
    }

    /// <summary>Кодирует одну typed runtime-команду; world facts делегируются точному BatchJson minimumMinor 3.</summary>
    public static byte[] Encode(RuntimeInput input, ulong ackOutputSequence = 0)
    {
        if (input is null || input.Sequence == 0 || input.SimulationTimeDs < 0 || input.Command is null) throw Schema();
        ValidateEpochs(input.Epochs);
        byte[]? worldBody = null;
        if (input.Command is ApplyWorld world)
            worldBody = BatchJson.Encode(new InputBatch(input.Epochs, input.Sequence, input.SimulationTimeDs, world.Entries), 3);
        else ValidateCommand(input.Command);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol_major", 1);
            writer.WriteNumber("protocol_minor", 3);
            WriteEpochs(writer, input.Epochs);
            writer.WriteString("sequence", Decimal(input.Sequence));
            writer.WriteString("ack_output_sequence", Decimal(ackOutputSequence));
            writer.WriteNumber("simulation_time_ds", input.SimulationTimeDs);
            writer.WritePropertyName("entries");
            if (worldBody is not null)
            {
                using var worldDocument = JsonDocument.Parse(worldBody);
                writer.WriteRawValue(worldDocument.RootElement.GetProperty("entries").GetRawText(), skipInputValidation: true);
            }
            else
            {
                writer.WriteStartArray();
                WriteCommand(writer, input.Command);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        if (buffer.Length > FramedStream.MaxFrameBytes) throw new ProtocolException("frame_size");
        return buffer.ToArray();
    }

    /// <summary>Кодирует bounded poll без sequence и времени; max_events=0 допускает только ACK.</summary>
    public static byte[] EncodePoll(Epochs epochs, ulong ackOutputSequence, int maxEvents = 64)
    {
        ValidateEpochs(epochs);
        if (maxEvents is < 0 or > 128) throw Schema();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "poll");
            WriteEpochs(writer, epochs);
            writer.WriteString("ack_output_sequence", Decimal(ackOutputSequence));
            writer.WriteNumber("max_events", maxEvents);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Выделяет только логические команды; неизвестные world kinds затем отвергает BatchJson.</summary>
    private static bool IsCommand(string kind) => kind is "request_submit" or "request_cancel" or "invocation_result" or "route_query" or "backbone_configure" or
        "service_discover" or "service_discovery_cancel" or "projection_query" or "projection_cancel" or "anchor_query" or
        "graph_sync_start" or "graph_sync_continue" or "graph_sync_cancel" or "service_watch" or "service_unwatch" or
        "entity_navigation_query" or "entity_navigation_cancel" or "entity_operation" or "entity_effect_result" or "module_request" ||
        IsStorageWireKind(kind) || IsCombatWireKind(kind);

    /// <summary>Разбирает allowlisted логическую команду с точным набором обязательных nullable-полей.</summary>
    private static RuntimeCommand ReadCommand(JsonElement entry)
    {
        switch (Text(entry.GetProperty("kind"), 128))
        {
            case "storage_register" or "storage_remove" or "storage_seed" or "storage_operation": return ReadStorageCommand(entry);
            case "combat_intent" or "combat_actor_configure": return ReadCombatCommand(entry);
            case "module_request":
                Fields(entry, "kind", "request_id", "module_id", "operation", "payload");
                var moduleRequest = new ModuleRequest(RequestId(entry.GetProperty("request_id")), Text(entry.GetProperty("module_id"), 64),
                    Text(entry.GetProperty("operation"), 64), NetworkValueJson.Decode(entry.GetProperty("payload")));
                if (!ModuleRequestRules.IsValid(moduleRequest)) throw Schema();
                return new SubmitModuleRequest(moduleRequest);
            case "entity_operation": return ReadEntityOperation(entry);
            case "entity_effect_result": return ReadEntityEffectResult(entry);
            case "entity_navigation_query":
                return ReadEntityNavigation(entry);
            case "entity_navigation_cancel":
                Fields(entry, "kind", "request_id");
                return new CancelEntityNavigation(RequestId(entry.GetProperty("request_id")));
            case "request_submit":
                return ReadSubmit(entry);
            case "request_cancel":
                Fields(entry, "kind", "request_id");
                return new CancelRequest(RequestId(entry.GetProperty("request_id")));
            case "service_discover":
                Fields(entry, "kind", "request_id", "source", "destination_family", "recipient_tag", "communications", "frequency", "max_results");
                var communications = Boolean(entry.GetProperty("communications"));
                var discoveryFrequency = entry.GetProperty("frequency");
                int? requestedFrequency = discoveryFrequency.ValueKind == JsonValueKind.Null ? null : Integer(discoveryFrequency, 1, int.MaxValue);
                if (!communications && requestedFrequency is not null) throw Schema();
                return new DiscoverServices(new DiscoveryRequest(RequestId(entry.GetProperty("request_id")), ReadService(entry.GetProperty("source")),
                    Text(entry.GetProperty("destination_family"), 128), NullableText(entry.GetProperty("recipient_tag"), 256), communications,
                    requestedFrequency, Integer(entry.GetProperty("max_results"), 1, int.MaxValue)));
            case "service_watch":
                Fields(entry, "kind", "request_id", "source", "destination_family", "recipient_tag");
                return new WatchServices(new ServiceWatchRequest(RequestId(entry.GetProperty("request_id")),
                    ReadService(entry.GetProperty("source")), Text(entry.GetProperty("destination_family"), 128),
                    NullableText(entry.GetProperty("recipient_tag"), 256)));
            case "service_unwatch":
                Fields(entry, "kind", "request_id");
                return new UnwatchServices(RequestId(entry.GetProperty("request_id")));
            case "service_discovery_cancel":
                Fields(entry, "kind", "request_id");
                return new CancelDiscovery(RequestId(entry.GetProperty("request_id")));
            case "graph_sync_start":
                Fields(entry, "kind", "request_id");
                return new StartGraphSynchronization(RequestId(entry.GetProperty("request_id")));
            case "graph_sync_continue":
                Fields(entry, "kind", "request_id", "page_index");
                return new ContinueGraphSynchronization(RequestId(entry.GetProperty("request_id")), Counter(entry.GetProperty("page_index")));
            case "graph_sync_cancel":
                Fields(entry, "kind", "request_id");
                return new CancelGraphSynchronization(RequestId(entry.GetProperty("request_id")));
            case "projection_query":
                Fields(entry, "kind", "request_id", "projection_kind", "source", "mode");
                var projectionKind = Text(entry.GetProperty("projection_kind"), 32) switch
                {
                    "graph" => ProjectionKind.Graph,
                    "routes" => ProjectionKind.Routes,
                    _ => throw Schema()
                };
                var projectionMode = Text(entry.GetProperty("mode"), 32) switch
                {
                    "machine" => RouteKind.Machine,
                    "discovery" => RouteKind.Discovery,
                    _ => throw Schema()
                };
                var projectionSource = entry.GetProperty("source");
                var projection = new ProjectionRequest(RequestId(entry.GetProperty("request_id")), projectionKind,
                    projectionSource.ValueKind == JsonValueKind.Null ? null : ReadNode(projectionSource), projectionMode);
                if (!ProjectionBroker.IsValidRequest(projection)) throw Schema();
                return new QueryProjection(projection);
            case "projection_cancel":
                Fields(entry, "kind", "request_id");
                return new CancelProjection(RequestId(entry.GetProperty("request_id")));
            case "anchor_query":
                Fields(entry, "kind", "request_id", "position", "sector_id", "base_area_id");
                return new QueryAnchor(new(RequestId(entry.GetProperty("request_id")), ReadPosition(entry.GetProperty("position")),
                    Text(entry.GetProperty("sector_id"), 128), NullableText(entry.GetProperty("base_area_id"), 128)));
            case "invocation_result":
                Fields(entry, "kind", "binding_epoch", "invocation_id", "success", "payload", "code");
                var code = NullableText(entry.GetProperty("code"), 64);
                if (code is not null && !Token(code, 64)) throw Schema();
                var payload = entry.GetProperty("payload");
                return new FinishInvocation(new InvocationResult(Text(entry.GetProperty("binding_epoch"), 128),
                    Text(entry.GetProperty("invocation_id"), 256), Boolean(entry.GetProperty("success")),
                    payload.ValueKind == JsonValueKind.Null ? null : NetworkValueJson.Decode(payload), code));
            case "route_query":
                Fields(entry, "kind", "source", "destination", "mode");
                var mode = Text(entry.GetProperty("mode"), 32) switch
                {
                    "machine" => RouteKind.Machine,
                    "discovery" => RouteKind.Discovery,
                    _ => throw Schema()
                };
                return new QueryRoute(ReadNode(entry.GetProperty("source")), ReadNode(entry.GetProperty("destination")), mode);
            case "backbone_configure":
                Fields(entry, "kind", "enabled");
                return new ConfigureBackbone(Boolean(entry.GetProperty("enabled")));
            default: throw new ProtocolException("entry_kind");
        }
    }

    /// <summary>
    /// Сначала проверяет ВСЮ request metadata. Только domain-отказ payload нормализуется в RejectRequest;
    /// неизвестные поля, неверные counters/типы и повреждённый общий JSON не маскируются как отказ сообщения.
    /// </summary>
    private static RuntimeCommand ReadSubmit(JsonElement entry)
    {
        FieldsWithOptional(entry, ["kind", "request_id", "source", "destination", "operation_id", "payload", "deadline_ds",
            "coalescing_key", "state_revision", "authorization_context", "native_context"], "traffic_class", "frequency");
        var key = RequestId(entry.GetProperty("request_id"));
        var source = ReadService(entry.GetProperty("source"));
        var destination = ReadService(entry.GetProperty("destination"));
        var operation = Text(entry.GetProperty("operation_id"), 128);
        var deadlineValue = entry.GetProperty("deadline_ds");
        int? deadline = deadlineValue.ValueKind == JsonValueKind.Null ? null : Integer(deadlineValue, 0, int.MaxValue);
        var coalescing = NullableText(entry.GetProperty("coalescing_key"), 128);
        var revisionValue = entry.GetProperty("state_revision");
        ulong? revision = revisionValue.ValueKind == JsonValueKind.Null ? null : Counter(revisionValue);
        var authorization = NullableText(entry.GetProperty("authorization_context"), 128);
        var nativeContext = NullableText(entry.GetProperty("native_context"), 128);
        var traffic = entry.TryGetProperty("traffic_class", out var trafficValue) ? Text(trafficValue, 32) switch
        {
            "ordinary" => TrafficClass.Ordinary,
            "communications" => TrafficClass.Communications,
            _ => throw Schema()
        } : TrafficClass.Ordinary;
        int? frequency = entry.TryGetProperty("frequency", out var frequencyValue) && frequencyValue.ValueKind != JsonValueKind.Null
            ? Integer(frequencyValue, 1, int.MaxValue) : null;
        if (traffic != TrafficClass.Communications && frequency is not null) throw Schema();
        try
        {
            var payload = NetworkValueJson.Decode(entry.GetProperty("payload"));
            return new SubmitRequest(new(key, source, destination, operation, payload, deadline, coalescing,
                revision, authorization, nativeContext, traffic, frequency));
        }
        catch (ProtocolException failure) when (failure.Code is "payload_invalid" or "payload_size")
        {
            return new RejectRequest(key);
        }
    }

    /// <summary>Проверяет локальную модель до записи длинных строк или payload; Runtime остаётся независим от JSON.</summary>
    private static void ValidateCommand(RuntimeCommand command)
    {
        switch (command)
        {
            case RegisterStorage or RemoveStorage or SeedStorageFile or SubmitStorageOperation: ValidateStorageCommand(command); break;
            case SubmitCombatIntent or ConfigureActorCombat: ValidateCombatCommand(command); break;
            case SubmitModuleRequest module:
                if (!ModuleRequestRules.IsValid(module.Request)) throw Schema();
                break;
            case SubmitEntityOperation entity:
                if (!EntityWorld.IsValidOperation(entity.Operation) || entity.Access is not null &&
                    !Netrun.Core.Access.EntityAccessAuthority.IsValidSnapshot(entity.Access)) throw Schema();
                break;
            case FinishEntityEffect finish:
                ValidateRequestKey(finish.Key);
                if (!EntityWorld.IsValidResult(finish.Result)) throw Schema();
                break;
            case QueryEntityNavigation navigation:
                if (!EntityNavigationBroker.IsValidRequest(navigation.Request)) throw Schema();
                break;
            case CancelEntityNavigation cancel: ValidateRequestKey(cancel.Key); break;
            case SubmitRequest { Request: { } request }:
                ValidateRequestKey(request.Key);
                ValidateService(request.Source);
                ValidateService(request.Destination);
                ValidateText(request.OperationId, 128);
                if (request.Payload is null || !request.Payload.Validate() || request.DeadlineDs is < 0 || request.StateRevision == 0) throw Schema();
                ValidateOptionalText(request.CoalescingKey, 128);
                ValidateOptionalText(request.AuthorizationContext, 128);
                ValidateOptionalText(request.NativeContext, 128);
                if (!Enum.IsDefined(request.Traffic) || request.Frequency is <= 0 ||
                    request.Traffic != TrafficClass.Communications && request.Frequency is not null) throw Schema();
                break;
            case CancelRequest cancel: ValidateRequestKey(cancel.Key); break;
            case DiscoverServices { Request: { } discover }:
                ValidateRequestKey(discover.Key);
                ValidateService(discover.Source);
                ValidateText(discover.DestinationFamily, 128);
                ValidateOptionalText(discover.RecipientTag, 256);
                if (discover.MaxResults < 1 || discover.Frequency is <= 0 ||
                    !discover.Communications && discover.Frequency is not null) throw Schema();
                break;
            case WatchServices watch:
                if (!ServiceWatchBroker.IsValidRequest(watch.Request)) throw Schema();
                break;
            case UnwatchServices unwatch: ValidateRequestKey(unwatch.Key); break;
            case CancelDiscovery cancel: ValidateRequestKey(cancel.Key); break;
            case QueryProjection projection:
                if (!ProjectionBroker.IsValidRequest(projection.Request)) throw Schema();
                break;
            case CancelProjection cancel: ValidateRequestKey(cancel.Key); break;
            case StartGraphSynchronization start: ValidateRequestKey(start.Key); break;
            case ContinueGraphSynchronization next:
                ValidateRequestKey(next.Key);
                if (next.PageIndex == 0) throw Schema();
                break;
            case CancelGraphSynchronization cancel: ValidateRequestKey(cancel.Key); break;
            case QueryAnchor anchor:
                if (!AnchorRequest.IsValid(anchor.Request)) throw Schema();
                break;
            case FinishInvocation { Result: { } result }:
                ValidateText(result.BindingEpoch, 128);
                ValidateText(result.InvocationId, 256);
                if (result.Payload is not null && !result.Payload.Validate() || result.Code is not null && !Token(result.Code, 64)) throw Schema();
                break;
            case QueryRoute route:
                ValidateNode(route.Source);
                ValidateNode(route.Destination);
                if (!Enum.IsDefined(route.Mode)) throw Schema();
                break;
            case ConfigureBackbone: break;
            default: throw Schema();
        }
    }

    /// <summary>Пишет одну заранее проверенную команду без скрытого выполнения domain handlers.</summary>
    private static void WriteCommand(Utf8JsonWriter writer, RuntimeCommand command)
    {
        writer.WriteStartObject();
        switch (command)
        {
            case RegisterStorage or RemoveStorage or SeedStorageFile or SubmitStorageOperation: WriteStorageCommand(writer, command); break;
            case SubmitCombatIntent or ConfigureActorCombat: WriteCombatCommand(writer, command); break;
            case SubmitModuleRequest module:
                writer.WriteString("kind", "module_request"); writer.WriteString("request_id", module.Request.Key.Id);
                writer.WriteString("module_id", module.Request.ModuleId); writer.WriteString("operation", module.Request.Operation);
                writer.WritePropertyName("payload"); NetworkValueJson.Write(writer, module.Request.Payload);
                break;
            case SubmitEntityOperation entity: WriteEntityOperation(writer, entity); break;
            case FinishEntityEffect finish: WriteEntityEffectResult(writer, finish); break;
            case QueryEntityNavigation navigation:
                WriteEntityNavigation(writer, navigation.Request);
                break;
            case CancelEntityNavigation cancel:
                writer.WriteString("kind", "entity_navigation_cancel");
                writer.WriteString("request_id", cancel.Key.Id);
                break;
            case SubmitRequest submit:
                var request = submit.Request;
                writer.WriteString("kind", "request_submit");
                writer.WriteString("request_id", request.Key.Id);
                WriteService(writer, "source", request.Source);
                WriteService(writer, "destination", request.Destination);
                writer.WriteString("operation_id", request.OperationId);
                writer.WritePropertyName("payload");
                NetworkValueJson.Write(writer, request.Payload);
                if (request.DeadlineDs is { } deadline) writer.WriteNumber("deadline_ds", deadline); else writer.WriteNull("deadline_ds");
                writer.WriteString("coalescing_key", request.CoalescingKey);
                if (request.StateRevision is { } revision) writer.WriteString("state_revision", Decimal(revision)); else writer.WriteNull("state_revision");
                writer.WriteString("authorization_context", request.AuthorizationContext);
                writer.WriteString("native_context", request.NativeContext);
                if (request.Traffic != TrafficClass.Ordinary || request.Frequency is not null)
                {
                    writer.WriteString("traffic_class", request.Traffic == TrafficClass.Communications ? "communications" : "ordinary");
                    if (request.Frequency is { } frequency) writer.WriteNumber("frequency", frequency); else writer.WriteNull("frequency");
                }
                break;
            case CancelRequest cancel:
                writer.WriteString("kind", "request_cancel");
                writer.WriteString("request_id", cancel.Key.Id);
                break;
            case DiscoverServices discover:
                writer.WriteString("kind", "service_discover");
                writer.WriteString("request_id", discover.Request.Key.Id);
                WriteService(writer, "source", discover.Request.Source);
                writer.WriteString("destination_family", discover.Request.DestinationFamily);
                writer.WriteString("recipient_tag", discover.Request.RecipientTag);
                writer.WriteBoolean("communications", discover.Request.Communications);
                if (discover.Request.Frequency is { } discoveryFrequency) writer.WriteNumber("frequency", discoveryFrequency); else writer.WriteNull("frequency");
                writer.WriteNumber("max_results", discover.Request.MaxResults);
                break;
            case WatchServices watch:
                writer.WriteString("kind", "service_watch");
                writer.WriteString("request_id", watch.Request.Key.Id);
                WriteService(writer, "source", watch.Request.Source);
                writer.WriteString("destination_family", watch.Request.Family);
                writer.WriteString("recipient_tag", watch.Request.Tag);
                break;
            case UnwatchServices unwatch:
                writer.WriteString("kind", "service_unwatch");
                writer.WriteString("request_id", unwatch.Key.Id);
                break;
            case CancelDiscovery cancel:
                writer.WriteString("kind", "service_discovery_cancel");
                writer.WriteString("request_id", cancel.Key.Id);
                break;
            case StartGraphSynchronization start:
                writer.WriteString("kind", "graph_sync_start");
                writer.WriteString("request_id", start.Key.Id);
                break;
            case ContinueGraphSynchronization next:
                writer.WriteString("kind", "graph_sync_continue");
                writer.WriteString("request_id", next.Key.Id);
                writer.WriteString("page_index", Decimal(next.PageIndex));
                break;
            case CancelGraphSynchronization cancel:
                writer.WriteString("kind", "graph_sync_cancel");
                writer.WriteString("request_id", cancel.Key.Id);
                break;
            case QueryProjection projection:
                writer.WriteString("kind", "projection_query");
                writer.WriteString("request_id", projection.Request.Key.Id);
                writer.WriteString("projection_kind", projection.Request.Kind == ProjectionKind.Graph ? "graph" : "routes");
                writer.WritePropertyName("source");
                if (projection.Request.Source is { } projectionSource) WriteNodeValue(writer, projectionSource); else writer.WriteNullValue();
                writer.WriteString("mode", projection.Request.Mode == RouteKind.Machine ? "machine" : "discovery");
                break;
            case CancelProjection cancel:
                writer.WriteString("kind", "projection_cancel");
                writer.WriteString("request_id", cancel.Key.Id);
                break;
            case QueryAnchor anchor:
                writer.WriteString("kind", "anchor_query");
                writer.WriteString("request_id", anchor.Request.Key.Id);
                writer.WriteStartObject("position");
                writer.WriteNumber("x", anchor.Request.Position.X);
                writer.WriteNumber("y", anchor.Request.Position.Y);
                writer.WriteNumber("z", anchor.Request.Position.Z);
                writer.WriteEndObject();
                writer.WriteString("sector_id", anchor.Request.SectorId);
                writer.WriteString("base_area_id", anchor.Request.BaseAreaId);
                break;
            case FinishInvocation finish:
                writer.WriteString("kind", "invocation_result");
                writer.WriteString("binding_epoch", finish.Result.BindingEpoch);
                writer.WriteString("invocation_id", finish.Result.InvocationId);
                writer.WriteBoolean("success", finish.Result.Success);
                writer.WritePropertyName("payload");
                if (finish.Result.Payload is { } response) NetworkValueJson.Write(writer, response); else writer.WriteNullValue();
                writer.WriteString("code", finish.Result.Code);
                break;
            case QueryRoute route:
                writer.WriteString("kind", "route_query");
                WriteNode(writer, "source", route.Source);
                WriteNode(writer, "destination", route.Destination);
                writer.WriteString("mode", route.Mode == RouteKind.Machine ? "machine" : "discovery");
                break;
            case ConfigureBackbone backbone:
                writer.WriteString("kind", "backbone_configure");
                writer.WriteBoolean("enabled", backbone.Enabled);
                break;
            default: throw Schema();
        }
        writer.WriteEndObject();
        if (writer.BytesCommitted + writer.BytesPending > FramedStream.MaxFrameBytes) throw new ProtocolException("frame_size");
    }

    /// <summary>Читает canonical producer:serial; буквенные токены не могут содержать дополнительный colon.</summary>
    private static RequestKey RequestId(JsonElement element)
    {
        var id = Text(element, 85);
        var colon = id.IndexOf(':');
        if (colon <= 0 || id.LastIndexOf(':') != colon || !Token(id[..colon], 64)) throw Schema();
        return new RequestKey(id[..colon], ParseCounter(id[(colon + 1)..], false));
    }

    /// <summary>Два поля service reference не допускают alias сетевым адресом или числовое поколение.</summary>
    private static ServiceKey ReadService(JsonElement value)
    {
        Fields(value, "service_id", "generation");
        return new(Text(value.GetProperty("service_id"), 128), Counter(value.GetProperty("generation")));
    }

    /// <summary>Читает точное physical generation для route query.</summary>
    private static NodeKey ReadNode(JsonElement value)
    {
        Fields(value, "node_id", "generation");
        return new(Text(value.GetProperty("node_id"), 128), Counter(value.GetProperty("generation")));
    }

    /// <summary>Строго ограничивает три physical координаты до чтения current spatial index.</summary>
    private static Position ReadPosition(JsonElement value)
    {
        Fields(value, "x", "y", "z");
        return new(Integer(value.GetProperty("x"), 1, 32767), Integer(value.GetProperty("y"), 1, 32767),
            Integer(value.GetProperty("z"), 1, 4096));
    }

    /// <summary>Пишет service ID отдельно от family и physical node.</summary>
    private static void WriteService(Utf8JsonWriter writer, string property, ServiceKey service)
    {
        writer.WriteStartObject(property);
        writer.WriteString("service_id", service.ServiceId);
        writer.WriteString("generation", Decimal(service.Generation));
        writer.WriteEndObject();
    }

    /// <summary>Пишет physical identity со строковым ulong generation.</summary>
    private static void WriteNode(Utf8JsonWriter writer, string property, NodeKey node)
    {
        writer.WritePropertyName(property);
        WriteNodeValue(writer, node);
    }

    /// <summary>Пишет node value для обязательной nullable projection source без строкового alias.</summary>
    private static void WriteNodeValue(Utf8JsonWriter writer, NodeKey node)
    {
        writer.WriteStartObject();
        writer.WriteString("node_id", node.NodeId);
        writer.WriteString("generation", Decimal(node.Generation));
        writer.WriteEndObject();
    }

    /// <summary>Читает три независимые fenced идентичности после проверки root schema.</summary>
    private static Epochs ReadEpochs(JsonElement root) => new(Text(root.GetProperty("world_epoch"), 128),
        Text(root.GetProperty("engine_epoch"), 128), Text(root.GetProperty("binding_epoch"), 128));

    /// <summary>Пишет обязательные epochs, не помещая сюда секрет аутентификации локального endpoint.</summary>
    private static void WriteEpochs(Utf8JsonWriter writer, Epochs epochs)
    {
        writer.WriteString("world_epoch", epochs.World);
        writer.WriteString("engine_epoch", epochs.Engine);
        writer.WriteString("binding_epoch", epochs.Binding);
    }

    /// <summary>Root IDs валидируются до появления их в выходном буфере.</summary>
    private static void ValidateEpochs(Epochs? epochs)
    {
        if (epochs is null) throw Schema();
        ValidateText(epochs.World, 128); ValidateText(epochs.Engine, 128); ValidateText(epochs.Binding, 128);
    }

    /// <summary>Локально созданные ключи подчиняются тем же правилам canonical request ID.</summary>
    private static void ValidateRequestKey(RequestKey? key)
    {
        if (key is null || key.Sequence == 0 || !Token(key.ProducerId, 64)) throw Schema();
    }

    /// <summary>Валидирует ссылку на экземпляр сервиса, не сверяя ещё world ownership.</summary>
    private static void ValidateService(ServiceKey? key)
    {
        if (key is null || key.Generation == 0) throw Schema();
        ValidateText(key.ServiceId, 128);
    }

    /// <summary>Валидирует physical reference; существование поколения проверяет Core.</summary>
    private static void ValidateNode(NodeKey? key)
    {
        if (key is null || key.Generation == 0) throw Schema();
        ValidateText(key.NodeId, 128);
    }

    /// <summary>Точный набор mandatory fields отвергает duplicates/unknowns в одном проходе.</summary>
    private static void Fields(JsonElement value, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Schema();
        var remaining = new HashSet<string>(required, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) throw Schema();
        if (remaining.Count != 0) throw Schema();
    }

    /// <summary>Optional properties разрешаются только явно у request_submit; unknown и duplicate по-прежнему запрещены.</summary>
    private static void FieldsWithOptional(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Schema();
        var remaining = new HashSet<string>(required, StringComparer.Ordinal);
        var allowed = new HashSet<string>(required.Concat(optional), StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Remove(property.Name)) throw Schema();
            remaining.Remove(property.Name);
        }
        if (remaining.Count != 0) throw Schema();
    }

    /// <summary>Тип и длина ID не приводятся автоматически из JSON numbers/bools.</summary>
    private static string Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) throw Schema();
        var text = value.GetString();
        ValidateText(text, maximum);
        return text!;
    }

    /// <summary>Текст идентификатора отличен от payload: controls/whitespace-only и invalid Unicode запрещены.</summary>
    private static void ValidateText(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) throw Schema();
        try { _ = StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw Schema(); }
    }

    /// <summary>Nullable поля обязаны присутствовать, но могут явно не содержать контекста.</summary>
    private static string? NullableText(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.Null ? null : Text(value, maximum);

    /// <summary>Проверяет optional ID локального input до сериализации.</summary>
    private static void ValidateOptionalText(string? value, int maximum) { if (value is not null) ValidateText(value, maximum); }

    /// <summary>ASCII token используется для producer и safe code, без произвольного диагностического текста.</summary>
    private static bool Token(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    /// <summary>Counter всегда строка; ноль разрешается только явным контрактом ACK.</summary>
    private static ulong Counter(JsonElement value, bool allowZero = false) => ParseCounter(Text(value, 20), allowZero);

    /// <summary>Парсит ASCII decimal без sign, whitespace, leading zero или округления UInt64.</summary>
    private static ulong ParseCounter(string value, bool allowZero)
    {
        if (allowZero && value == "0") return 0;
        if (value.Length is 0 or > 20 || value[0] is < '1' or > '9' || value.Any(c => c is < '0' or > '9') ||
            !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)) throw Schema();
        return result;
    }

    /// <summary>Читает явное Int32 внутри границ, без string coercion.</summary>
    private static int Integer(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum) throw Schema();
        return number;
    }

    /// <summary>Boolean не кодируется числом 0/1.</summary>
    private static bool Boolean(JsonElement value) => value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Schema() };

    /// <summary>Единый десятичный wire-формат не зависит от locale процесса.</summary>
    private static string Decimal(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Ошибка схемы не содержит тела, ключей payload или native exception.</summary>
    private static ProtocolException Schema() => new("schema_invalid");
}
