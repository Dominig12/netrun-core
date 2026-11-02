using System.Globalization;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Отказ excessive typed payload принадлежит одному сообщению, а не всему binding или чужим native outcomes.</summary>
public sealed class InvalidPayloadTests
{
    /// <summary>Изолированная synthetic сессия с настоящими mirror, router и transport.</summary>
    private static readonly Epochs Epochs = new("payload-world", "payload-engine", "payload-binding");

    /// <summary>Minimal full snapshot с physical APC, endpoint и declared native operation.</summary>
    private static RuntimeInput[] Snapshot()
    {
        WorldChange[] facts =
        [
            new SectorUpsert(new("sector", 1, 1, new([1]))),
            new NodeUpsert(new("source-node", 1, 1, new("A", "apc", new(1, 1, 1), "sector", true, new("home", "home")))),
            new NodeUpsert(new("target-node", 1, 1, new("B", "server", new(2, 1, 1), "sector", true, new("home", "home")))),
            new ServiceUpsert(new("source", 1, 1, new("source-node", 1), "test.source", null, true, new([new("read", OperationSafety.ReadOnly)], 64))),
            new ServiceUpsert(new("target", 1, 1, new("target-node", 1), "test.target", null, true, new([new("read", OperationSafety.ReadOnly)], 64)))
        ];
        var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(RuntimeJson.Encode(new(Epochs, 2, 0,
            new ApplyWorld([new SnapshotChunk("snapshot", 1, facts)])))));
        var chunk = Assert.IsType<SnapshotChunk>(Assert.Single(Assert.IsType<ApplyWorld>(packet.Input.Command).Entries));
        return
        [
            new(Epochs, 1, 0, new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)])), packet.Input,
            new(Epochs, 3, 0, new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)facts.Length, SnapshotChecksum.Compute([chunk], 3))])),
            new(Epochs, 4, 0, new ApplyWorld([new CatchUpComplete("snapshot", 0)]))
        ];
    }

    /// <summary>Создаёт valid typed metadata, затем вставляет только raw JSON value payload как реальный foreign-language peer.</summary>
    private static byte[] Frame(string payload, ulong sequence = 1, ulong requestSequence = 1, ulong ack = 0)
    {
        var valid = RuntimeJson.Encode(new(Epochs, sequence, 0, new SubmitRequest(new(new("producer", requestSequence),
            new("source", 1), new("target", 1), "read", NetworkValue.Null, AuthorizationContext: "auth-context", NativeContext: "native-context"))), ack);
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"payload\":null", "\"payload\":" + payload, StringComparison.Ordinal));
    }

    /// <summary>Независимые domain limits остаются намного меньше transport frame cap; значения создаются только внутри теста.</summary>
    private static IEnumerable<string> InvalidPayloads()
    {
        yield return "[" + string.Join(',', Enumerable.Repeat("null", 1025)) + "]";
        yield return "{" + string.Join(',', Enumerable.Range(0, 1025).Select(i => "\"k" + i + "\":null")) + "}";
        var branch = "[" + string.Join(',', Enumerable.Repeat("null", 1024)) + "]";
        var last = "[" + string.Join(',', Enumerable.Repeat("null", 1020)) + "]";
        yield return "[" + branch + "," + branch + "," + branch + "," + last + "]";
        yield return new string('[', 32) + "null" + new string(']', 32);
        yield return new string('[', 64) + "null" + new string(']', 64);
        yield return new string('[', 16384) + "null" + new string(']', 16384);
        yield return "\"" + new string('a', 65537) + "\"";
        yield return "[\"" + new string('Я', 43691) + "\"]";
        yield return "{\"a\":1,\"\\u0061\":2}";
        yield return "1e1000";
        yield return "\"\\uD800\"";
    }

    /// <summary>Выбирает единственное событие нужного вида из настоящего serialized output.</summary>
    private static JsonElement Entry(JsonDocument document, string kind) => Assert.Single(document.RootElement.GetProperty("results").EnumerateArray(),
        value => value.GetProperty("kind").GetString() == kind);

    /// <summary>Malformed payload не материализуется в Runtime и не появляется в безопасном command ToString.</summary>
    [Fact]
    public void ValidJsonPayloadLimitErrorsNormalizeToSmallRejectedCommand()
    {
        foreach (var payload in InvalidPayloads())
        {
            var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Frame(payload)));
            Assert.Equal(new RejectRequest(new("producer", 1)), Assert.IsType<RejectRequest>(packet.Input.Command));
        }
        var marker = "never-retain-raw-payload";
        var rejected = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Frame("\"" + marker + new string('a', 65537) + "\""))).Input.Command;
        Assert.DoesNotContain(marker, rejected.ToString(), StringComparison.Ordinal);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(new(Epochs, 1, 0, rejected)));
        // Root array + string quotes: 2 + 2 + 6 * 43690 == 262144, UTF-8 body остаётся значительно меньше 1 MiB.
        var boundary = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Frame("[\"" + new string('Я', 43690) + "\"]")));
        Assert.True(Assert.IsType<SubmitRequest>(boundary.Input.Command).Request.Payload.Validate());
    }

    /// <summary>Все metadata, включая поля после payload, проверяются раньше нормализации; malformed syntax/epochs остаются protocol error.</summary>
    [Fact]
    public void PayloadRejectionCannotHideInvalidRequestMetadataOrEnvelope()
    {
        var body = Encoding.UTF8.GetString(Frame("[" + string.Join(',', Enumerable.Repeat("null", 1025)) + "]"));
        foreach (var invalid in new[]
        {
            body.Replace("\"deadline_ds\":null", "\"deadline_ds\":\"5\"", StringComparison.Ordinal),
            body.Replace("\"state_revision\":null", "\"state_revision\":\"0\"", StringComparison.Ordinal),
            body.Replace("\"coalescing_key\":null", "\"coalescing_key\":{}", StringComparison.Ordinal),
            body.Replace("\"authorization_context\":\"auth-context\"", "\"authorization_context\":{}", StringComparison.Ordinal),
            body.Replace("\"native_context\":\"native-context\"", "\"native_context\":\"\\uD800\"", StringComparison.Ordinal),
            body.Replace("\"native_context\":\"native-context\"", "\"native_context\":null,\"frequency\":1459", StringComparison.Ordinal),
            body.Replace("\"request_id\":\"producer:1\"", "\"request_id\":\"producer:01\"", StringComparison.Ordinal),
            body.Replace("\"generation\":\"1\"", "\"generation\":\"0\"", StringComparison.Ordinal),
            body.Replace("\"world_epoch\":\"payload-world\"", "\"world_epoch\":0", StringComparison.Ordinal),
            body.Replace("\"native_context\":\"native-context\"", "\"native_context\":null,\"unexpected\":true", StringComparison.Ordinal),
            body[..^1]
        }) Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(invalid)));
        Assert.Equal("invalid_json", Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Frame("[null,"))).Code);
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(new byte[FramedStream.MaxFrameBytes + 1]));
    }

    /// <summary>Нормализованный отказ потребляет outer sequence, не занимает transport slot и одинаково replay-ится без хранения raw tree.</summary>
    [Fact]
    public void RejectedPayloadReplayUsesNormalizedEffectIdentityAndAllowsCorrectedNewFrame()
    {
        using var runtime = new NetworkRuntime(Epochs);
        foreach (var input in Snapshot()) runtime.Apply(input);
        var first = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Frame(new string('[', 64) + "null" + new string(']', 64), 5))).Input;
        var rejected = runtime.Apply(first);
        Assert.Equal(ApplyStatus.Applied, rejected.Apply.Status);
        Assert.Equal(AdmissionStatus.Rejected, rejected.Admission!.Status);
        Assert.Equal("invalid_payload", rejected.Admission.Code);
        Assert.Equal(0, runtime.ActiveRequestCount);
        Assert.False(runtime.TryDequeueEvent(out _));
        var equivalent = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Frame("\"" + new string('a', 65537) + "\"", 5))).Input;
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(equivalent).Apply.Status);
        var corrected = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(Frame("null", 6))).Input;
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(corrected).Admission!.Status);
        Assert.Equal(1, runtime.ActiveRequestCount);
        Assert.Equal(1, runtime.Pump(1));
        Assert.True(runtime.TryDequeueEvent(out var invocation));
        Assert.IsType<ServiceInvocation>(invocation);
    }

    /// <summary>Один binding выдерживает все excessive payloads; старый native invocation завершается, а следующий valid request выполняется.</summary>
    [Fact]
    public async Task PayloadFailuresDoNotCloseHostBindingOrLoseOtherPendingOutcome()
    {
        using var session = new RuntimeHostSessionFactory().Create(Epochs);
        ulong ack = 0;
        ulong sequence = 0;
        foreach (var input in Snapshot())
        {
            using var output = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.Encode(input, ack), CancellationToken.None));
            ack = ulong.Parse(output.RootElement.GetProperty("output_sequence").GetString()!, CultureInfo.InvariantCulture);
            sequence = input.Sequence;
        }
        using var admitted = JsonDocument.Parse(await session.ProcessAsync(Frame("{\"value\":1}", ++sequence, 1, ack), CancellationToken.None));
        var invocationId = Entry(admitted, "service_invocation").GetProperty("invocation_id").GetString()!;
        ack = ulong.Parse(admitted.RootElement.GetProperty("output_sequence").GetString()!, CultureInfo.InvariantCulture);
        ulong request = 1;
        foreach (var payload in InvalidPayloads())
        {
            using var output = JsonDocument.Parse(await session.ProcessAsync(Frame(payload, ++sequence, ++request, ack), CancellationToken.None));
            var status = Entry(output, "runtime_result");
            Assert.Equal("applied", status.GetProperty("status").GetString());
            Assert.Equal(sequence.ToString(CultureInfo.InvariantCulture), status.GetProperty("applied_sequence").GetString());
            Assert.Equal("ready", status.GetProperty("sync_state").GetString());
            Assert.Equal("rejected", status.GetProperty("admission").GetProperty("status").GetString());
            Assert.Equal("invalid_payload", status.GetProperty("admission").GetProperty("code").GetString());
            Assert.Single(output.RootElement.GetProperty("results").EnumerateArray());
            ack = ulong.Parse(output.RootElement.GetProperty("output_sequence").GetString()!, CultureInfo.InvariantCulture);
        }
        using var completed = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.Encode(new(Epochs, ++sequence, 0,
            new FinishInvocation(new(Epochs.Binding, invocationId, true))), ack), CancellationToken.None));
        Assert.Equal("completed", Entry(completed, "request_completed").GetProperty("state").GetString());
        ack = ulong.Parse(completed.RootElement.GetProperty("output_sequence").GetString()!, CultureInfo.InvariantCulture);
        using var next = JsonDocument.Parse(await session.ProcessAsync(Frame("{\"value\":2}", ++sequence, ++request, ack), CancellationToken.None));
        Assert.Equal("accepted", Entry(next, "runtime_result").GetProperty("admission").GetProperty("status").GetString());
        Assert.Equal("producer:" + request.ToString(CultureInfo.InvariantCulture), Entry(next, "service_invocation").GetProperty("request_id").GetString());
    }
}
