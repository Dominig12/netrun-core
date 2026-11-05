using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Routing;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет causality envelope v1.3, logical commands, opaque payload и raw snapshot integrity.</summary>
public sealed class RuntimeProtocolTests
{
    /// <summary>Фиксированные epochs отделяют логическую корреляцию от аутентификации endpoint.</summary>
    private static readonly Epochs Epochs = new("world", "engine", "binding");

    /// <summary>Полная минимальная схема submit с обязательными nullable свойствами.</summary>
    private const string Submit = """
        {"kind":"request_submit","request_id":"producer:1","source":{"service_id":"source","generation":"1"},
        "destination":{"service_id":"target","generation":"2"},"operation_id":"read","payload":null,
        "deadline_ds":null,"coalescing_key":null,"state_revision":null,"authorization_context":null,"native_context":null}
        """;

    /// <summary>Модель неизвестной команды проверяет закрытость wire allowlist.</summary>
    private sealed record UnsupportedCommand : RuntimeCommand;

    /// <summary>Группа сохраняет точные 64-bit sequences, ограничивает burst и не ослабляет одиночную схему.</summary>
    [Fact]
    public void InputBatchKeepsIndependentSequencesAndStrictCommandBoundary()
    {
        var inputs = Enumerable.Range(0, 16).Select(index => new RuntimeInput(Epochs,
            16777217UL + (ulong)index, 15, new ConfigureBackbone(index % 2 == 0))).ToArray();
        var encoded = RuntimeJson.EncodeBatch(inputs, 7);
        var decoded = Assert.IsType<RuntimeInputBatch>(RuntimeJson.Decode(encoded));
        Assert.Equal(7UL, decoded.AckOutputSequence);
        Assert.Equal(inputs, decoded.Inputs);
        var body = Encoding.UTF8.GetString(encoded);
        foreach (var invalid in new[]
        {
            body.Replace("\"sequence\":\"16777232\"", "\"sequence\":\"1\"", StringComparison.Ordinal),
            body.Replace("\"kind\":\"input_batch\",", "", StringComparison.Ordinal),
            body.Replace("\"kind\":\"backbone_configure\",\"enabled\":true", "\"kind\":\"catchup_complete\"", StringComparison.Ordinal),
            body.Replace("\"kind\":\"backbone_configure\",", "", StringComparison.Ordinal),
            body[..^1] + ",\"extra\":true}"
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<ProtocolException>(() => RuntimeJson.EncodeBatch(inputs[..1]));
        // Снятый под-потолок 16: 17 команд теперь валидны; реальная стена grouped-кадра — BatchJson.MaxEntries.
        Assert.IsType<RuntimeInputBatch>(RuntimeJson.Decode(RuntimeJson.EncodeBatch([.. inputs, inputs[^1] with { Sequence = inputs[^1].Sequence + 1 }])));
        var full = Enumerable.Range(0, BatchJson.MaxEntries).Select(index => new RuntimeInput(Epochs,
            16777217UL + (ulong)index, 15, new ConfigureBackbone(index % 2 == 0))).ToArray();
        Assert.IsType<RuntimeInputBatch>(RuntimeJson.Decode(RuntimeJson.EncodeBatch(full)));
        foreach (var second in new[]
        {
            inputs[1] with { Sequence = inputs[0].Sequence },
            inputs[1] with { Epochs = new("other", "engine", "binding") },
            inputs[1] with { SimulationTimeDs = 16 },
            inputs[1] with { Command = new ApplyWorld([]) },
            inputs[1] with { Command = new QueryRoute(new("a", 1), new("b", 1), RouteKind.Machine) }
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.EncodeBatch([inputs[0], second]));
        Assert.Throws<ProtocolException>(() => RuntimeJson.EncodeBatch([
            inputs[0] with { Sequence = ulong.MaxValue }, inputs[1] with { Sequence = 0 }]));
        var finalCounters = new[] { inputs[0] with { Sequence = ulong.MaxValue - 1 }, inputs[1] with { Sequence = ulong.MaxValue } };
        Assert.Equal(finalCounters, Assert.IsType<RuntimeInputBatch>(RuntimeJson.Decode(RuntimeJson.EncodeBatch(finalCounters))).Inputs);
        // Один лишний entry поверх полного кадра (MaxInputCommands) превышает frame entries wall и отвергается при decode.
        var fullBody = Encoding.UTF8.GetString(RuntimeJson.EncodeBatch(full));
        var tooMany = fullBody.Replace("\"entries\":[", "\"entries\":[{\"kind\":\"backbone_configure\",\"enabled\":true},", StringComparison.Ordinal);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(tooMany)));
    }

    /// <summary>Каждая логическая команда имеет независимый typed round-trip, включая оба route режима.</summary>
    public static IEnumerable<object[]> Commands()
    {
        var payload = NetworkValue.FromObject(new Dictionary<string, NetworkValue>
        {
            ["null"] = NetworkValue.Null,
            ["array"] = NetworkValue.FromArray([]),
            ["object"] = NetworkValue.FromObject([]),
            ["text"] = NetworkValue.FromText("line\n\t\0🙂"),
            ["number"] = NetworkValue.FromNumber(2.5),
            ["bool"] = NetworkValue.FromBoolean(false)
        });
        yield return [new SubmitRequest(new(new("producer-1", ulong.MaxValue), new("source", ulong.MaxValue), new("target", 2),
            "read", payload, 999, "state", ulong.MaxValue, "auth-context", "native-context"))];
        yield return [new CancelRequest(new("producer.2", 3))];
        yield return [new FinishInvocation(new("binding", "binding/producer:1", false, payload, "device_rejected"))];
        yield return [new FinishInvocation(new("binding", "binding/producer:2", true))];
        yield return [new QueryRoute(new("node-1", 1), new("node-2", 2), RouteKind.Machine)];
        yield return [new QueryRoute(new("node-1", 1), new("node-2", 2), RouteKind.Discovery)];
        yield return [new ConfigureBackbone(false)];
    }

    /// <summary>IDs/counters сохраняют UInt64.MaxValue строками; command и payload не теряют typed форму.</summary>
    [Theory]
    [MemberData(nameof(Commands))]
    public void LogicalCommandsRoundTripInOneSequencedFrame(RuntimeCommand command)
    {
        var input = new RuntimeInput(Epochs, ulong.MaxValue, 42, command);
        var encoded = RuntimeJson.Encode(input, ulong.MaxValue);
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(encoded));
        Assert.Equal(input, packet.Input);
        Assert.Equal(ulong.MaxValue, packet.AckOutputSequence);
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(3, document.RootElement.GetProperty("protocol_minor").GetInt32());
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("sequence").ValueKind);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("ack_output_sequence").ValueKind);
        Assert.Equal(1, document.RootElement.GetProperty("entries").GetArrayLength());
    }

    /// <summary>World changes проходят прежний строгий codec, но общая оболочка содержит свой output ACK.</summary>
    [Fact]
    public void WorldFrameUsesRuntimeEpochSequenceAndAckWithoutChangingFacts()
    {
        WorldChange[] entries = [new NodeRemove("node-1", 2, 3), new SectorRemove("sector-1", 1, 4)];
        var input = new RuntimeInput(Epochs, 17, 50, new ApplyWorld(entries));
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(input, 9)));
        Assert.Equal(Epochs, packet.Epochs);
        Assert.Equal(17UL, packet.Input.Sequence);
        Assert.Equal(50, packet.Input.SimulationTimeDs);
        Assert.Equal(9UL, packet.AckOutputSequence);
        Assert.Equal(entries, Assert.IsType<ApplyWorld>(packet.Input.Command).Entries);
        var begin = new RuntimeInput(Epochs, 1, 0, new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)]));
        Assert.Equal(3, Assert.IsType<SnapshotBegin>(Assert.Single(Assert.IsType<ApplyWorld>(
            Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(begin))).Input.Command).Entries)).ChecksumVersion);
    }

    /// <summary>Удаление внешнего ACK не нормализует whitespace или Unicode escapes внутри changes для checksum v3.</summary>
    [Fact]
    public void WorldDecodePreservesExactRawChangesBytesForChecksumThree()
    {
        const string changes = "[\n { \"node_revision\" : \"2\", \"generation\":\"1\", \"node_id\" : \"n\\u0031\", \"kind\":\"node_remove\" }\n]";
        var entry = "{\"kind\":\"snapshot_chunk\",\"snapshot_id\":\"snapshot\",\"chunk_index\":\"1\",\"changes\":" + changes + "}";
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Envelope(entry)));
        var chunk = Assert.IsType<SnapshotChunk>(Assert.Single(Assert.IsType<ApplyWorld>(packet.Input.Command).Entries));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(changes))), chunk.WireDigest);
        Assert.Equal(new NodeRemove("n1", 1, 2), Assert.Single(chunk.Changes));
        using var document = JsonDocument.Parse(changes);
        using var normalized = new MemoryStream();
        using (var writer = new Utf8JsonWriter(normalized)) document.RootElement.WriteTo(writer);
        Assert.NotEqual(chunk.WireDigest, Convert.ToHexStringLower(SHA256.HashData(normalized.ToArray())));
    }

    /// <summary>Poll допускает ACK нуля и нулевой event budget без sequence/time.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    public void PollIsSeparateFromLogicalTimeAndSupportsBoundedEventCounts(int maximum)
    {
        var poll = Assert.IsType<RuntimePoll>(RuntimeJson.Decode(RuntimeJson.EncodePoll(Epochs, 0, maximum)));
        Assert.Equal(Epochs, poll.Epochs);
        Assert.Equal(0UL, poll.AckOutputSequence);
        Assert.Equal(maximum, poll.MaxEvents);
        using var document = JsonDocument.Parse(RuntimeJson.EncodePoll(Epochs, ulong.MaxValue, maximum));
        Assert.False(document.RootElement.TryGetProperty("sequence", out _));
        Assert.False(document.RootElement.TryGetProperty("simulation_time_ds", out _));
    }

    /// <summary>Poll не принимает дополнительные clock/command поля, превышенный budget или числовой ACK.</summary>
    [Fact]
    public void PollRejectsUnknownDuplicateAndInvalidFields()
    {
        var valid = Encoding.UTF8.GetString(RuntimeJson.EncodePoll(Epochs, 0));
        foreach (var invalid in new[]
        {
            valid.Replace("\"max_events\":64", "\"max_events\":129", StringComparison.Ordinal),
            valid.Replace("\"max_events\":64", "\"max_events\":-1", StringComparison.Ordinal),
            valid.Replace("\"max_events\":64", "\"max_events\":\"64\"", StringComparison.Ordinal),
            valid.Replace("\"ack_output_sequence\":\"0\"", "\"ack_output_sequence\":0", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"poll\"", "\"kind\":\"poll\",\"kind\":\"poll\"", StringComparison.Ordinal),
            valid[..^1] + ",\"simulation_time_ds\":0}",
            valid.Replace(",\"max_events\":64", "", StringComparison.Ordinal)
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<ProtocolException>(() => RuntimeJson.EncodePoll(Epochs, 0, 129));
    }

    /// <summary>Одно изменение мира или вторая logical command не могут смешаться с logical command.</summary>
    [Fact]
    public void MixedLogicalCommandsAndWorldEntriesAreRejected()
    {
        foreach (var suffix in new[] { "," + Submit, ",{\"kind\":\"node_remove\",\"node_id\":\"n\",\"generation\":\"1\",\"node_revision\":\"1\"}" })
            Assert.Equal("schema_invalid", Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(Submit + suffix))).Code);
        Assert.Equal("entry_kind", Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope("{\"kind\":\"unpublished_command\"}"))).Code);
    }

    /// <summary>Canonical request ID запрещает пустой/Unicode producer, colon injection и неоднозначный serial.</summary>
    [Theory]
    [InlineData(":1")]
    [InlineData("producer:")]
    [InlineData("producer:0")]
    [InlineData("producer:01")]
    [InlineData("producer:+1")]
    [InlineData("producer:1:2")]
    [InlineData("производитель:1")]
    [InlineData("producer:18446744073709551616")]
    public void RequestIdsMustBeCanonicalProducerAndPositiveSerial(string id) =>
        Assert.Equal("schema_invalid", Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(
            Submit.Replace("producer:1", id, StringComparison.Ordinal)))).Code);

    /// <summary>Все envelope counters строковые и точные, а zero разрешён только output ACK.</summary>
    [Fact]
    public void RuntimeCountersAndRequiredEnvelopeFieldsStayStrict()
    {
        var original = Encoding.UTF8.GetString(Envelope(Submit));
        foreach (var invalid in new[]
        {
            original.Replace("\"sequence\":\"1\"", "\"sequence\":1", StringComparison.Ordinal),
            original.Replace("\"sequence\":\"1\"", "\"sequence\":\"0\"", StringComparison.Ordinal),
            original.Replace("\"ack_output_sequence\":\"0\"", "\"ack_output_sequence\":\"00\"", StringComparison.Ordinal),
            original.Replace("\"ack_output_sequence\":\"0\"", "\"ack_output_sequence\":null", StringComparison.Ordinal),
            original.Replace("\"sequence\":\"1\"", "\"sequence\":\"1\",\"sequence\":\"1\"", StringComparison.Ordinal),
            original.Replace("\"simulation_time_ds\":0", "\"simulation_time_ds\":-1", StringComparison.Ordinal),
            original.Replace("\"protocol_minor\":3", "\"protocol_minor\":2", StringComparison.Ordinal),
            original[..^1] + ",\"unknown\":1}"
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(Submit.Replace("\"generation\":\"2\"", "\"generation\":2", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(Submit.Replace("\"state_revision\":null", "\"state_revision\":\"0\"", StringComparison.Ordinal))));
    }

    /// <summary>Submit nullable-поля присутствуют явно; bools, counters и deadline не приводятся из альтернативных типов.</summary>
    [Theory]
    [InlineData("\"deadline_ds\":null", "\"deadline_ds\":\"5\"")]
    [InlineData("\"deadline_ds\":null", "\"deadline_ds\":-1")]
    [InlineData("\"operation_id\":\"read\"", "\"operation_id\":\"read\",\"operation_id\":\"read\"")]
    [InlineData("\"native_context\":null", "\"native_context\":{}")]
    [InlineData("\"authorization_context\":null,", "")]
    public void SubmitFieldsRejectDuplicateMissingAndWrongTypes(string find, string replacement) =>
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(Submit.Replace(find, replacement, StringComparison.Ordinal))));

    /// <summary>Native result codes — безопасные tokens, а route modes и backbone enabled — закрытые typed значения.</summary>
    [Fact]
    public void ResultRouteAndConfigurationSchemasRemainClosed()
    {
        const string result = """{"kind":"invocation_result","binding_epoch":"binding","invocation_id":"id","success":true,"payload":null,"code":null}""";
        Assert.Null(Assert.IsType<FinishInvocation>(Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Envelope(result))).Input.Command).Result.Payload);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(result.Replace("\"success\":true", "\"success\":1", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(result.Replace("\"code\":null", "\"code\":\"native exception text\"", StringComparison.Ordinal))));
        const string route = """{"kind":"route_query","source":{"node_id":"a","generation":"1"},"destination":{"node_id":"b","generation":"2"},"mode":"machine"}""";
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope(route.Replace("machine", "arbitrary", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Envelope("{\"kind\":\"backbone_configure\",\"enabled\":1}")));
    }

    /// <summary>Payload сохраняет null, пустые коллекции и вложенные positional arrays независимо от DM list ambiguities.</summary>
    [Fact]
    public void PayloadShapeRoundTripPreservesAllExplicitJsonTypes()
    {
        const string payload = """{"n":null,"a":[],"o":{},"nested":[null,{"filters":[{"gas_id":"co2","enabled":false}]}],"s":"line\n"}""";
        using var document = JsonDocument.Parse(payload);
        var value = Assert.IsType<NetworkObject>(NetworkValueJson.Decode(document.RootElement));
        Assert.IsType<NetworkNull>(value.Properties["n"]);
        Assert.Empty(Assert.IsType<NetworkArray>(value.Properties["a"]).Items);
        Assert.Empty(Assert.IsType<NetworkObject>(value.Properties["o"]).Properties);
        using var encoded = new MemoryStream();
        using (var writer = new Utf8JsonWriter(encoded)) NetworkValueJson.Write(writer, value);
        using var restored = JsonDocument.Parse(encoded.ToArray());
        Assert.True(value.DeepEquals(NetworkValueJson.Decode(restored.RootElement)));
    }

    /// <summary>Дубликаты object keys, включая escaped aliases, и nonfinite numbers отвергаются без coercion.</summary>
    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    [InlineData("1e1000")]
    [InlineData("\"\\ud800\"")]
    public void PayloadRejectsAmbiguousObjectsNonfiniteNumbersAndInvalidUnicode(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("payload_invalid", Assert.Throws<ProtocolException>(() => NetworkValueJson.Decode(document.RootElement)).Code);
    }

    /// <summary>Payload recursion и collection limits действуют до неограниченной материализации.</summary>
    [Fact]
    public void PayloadDepthValueCountAndContainerLimitsAreEnforced()
    {
        var branch = "[" + string.Join(',', Enumerable.Repeat("null", 1024)) + "]";
        var shortBranch = "[" + string.Join(',', Enumerable.Repeat("null", 1020)) + "]";
        var excessiveValues = "[" + branch + "," + branch + "," + branch + "," + shortBranch + "]";
        var excessiveArray = "[" + string.Join(',', Enumerable.Repeat("null", 1025)) + "]";
        var excessiveObject = "{" + string.Join(',', Enumerable.Range(0, 1025).Select(i => "\"k" + i + "\":null")) + "}";
        foreach (var payload in new[] { excessiveValues, excessiveArray, excessiveObject,
            new string('[', 32) + "null" + new string(']', 32), "\"" + new string('a', 65_537) + "\"" })
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
            Assert.Throws<ProtocolException>(() => NetworkValueJson.Decode(document.RootElement));
        }
        var maximumDepth = new string('[', 31) + "null" + new string(']', 31);
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Envelope(Submit.Replace("\"payload\":null", "\"payload\":" + maximumDepth, StringComparison.Ordinal))));
        Assert.True(Assert.IsType<SubmitRequest>(packet.Input.Command).Request.Payload.Validate());
    }

    /// <summary>Локально созданные некорректные metadata также отвергаются до JSON, не полагаясь только на Decode.</summary>
    [Fact]
    public void EncoderRejectsInvalidLocalCommandsAndMetadata()
    {
        var valid = new NetworkRequest(new("producer", 1), new("source", 1), new("target", 1), "read", NetworkValue.Null);
        RuntimeCommand[] invalid =
        [
            new UnsupportedCommand(), new CancelRequest(new("injected:producer", 1)),
            new SubmitRequest(valid with { Key = new("producer", 0) }),
            new SubmitRequest(valid with { StateRevision = 0 }), new SubmitRequest(valid with { DeadlineDs = -1 }),
            new SubmitRequest(valid with { Source = new("source", 0) }), new SubmitRequest(valid with { AuthorizationContext = "\ud800" }),
            new FinishInvocation(new("binding", "id", true, Code: "full exception text")),
            new QueryRoute(new("n", 1), new("m", 1), (RouteKind)999)
        ];
        foreach (var command in invalid) Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(new(Epochs, 1, 0, command)));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(new(Epochs, 0, 0, new ConfigureBackbone(true))));
    }

    /// <summary>Malformed вход не раскрывает секретный body или raw JSON даже в ToString исключения.</summary>
    [Fact]
    public void RuntimeRejectsOversizedInvalidUtf8AndMalformedBodiesSafely()
    {
        Assert.Equal("frame_size", Assert.Throws<ProtocolException>(() => RuntimeJson.Decode([])).Code);
        Assert.Equal("frame_size", Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(new byte[FramedStream.MaxFrameBytes + 1])).Code);
        var bytes = Envelope(Submit);
        bytes[0] = 0xff;
        Assert.Equal("invalid_utf8", Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(bytes)).Code);
        var error = Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes("{\"secret\":\"never-log-this")));
        Assert.Equal("invalid_json", error.Code);
        Assert.DoesNotContain("never-log-this", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Оболочка хранит точные entry bytes, чтобы тесты не нормализовали raw checksum перед parser.</summary>
    private static byte[] Envelope(string entries) => Encoding.UTF8.GetBytes($$"""
        {"protocol_major":1,"protocol_minor":3,"world_epoch":"world","engine_epoch":"engine","binding_epoch":"binding",
        "sequence":"1","ack_output_sequence":"0","simulation_time_ds":0,"entries":[{{entries}}]}
        """);
}
