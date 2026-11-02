using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Projection;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Полный serialized pipeline с настоящими mirror, topology, router и transport, без вызова DM-эффектов.</summary>
public sealed class RuntimeHostSessionTests
{
    /// <summary>Публичный test-only token для единственного ephemeral TCP сценария.</summary>
    private const string Token = "runtime-test-only-token-at-least-32-characters";
    /// <summary>Direct-session epochs фиксированы; TCP получает engine epoch из фактического hello_ack.</summary>
    private static readonly Epochs Epochs = new("runtime-world", "runtime-engine", "runtime-binding");

    /// <summary>Имитирует causal DM peer: перед следующим запросом подтверждает только уже прочитанный output.</summary>
    private sealed class Peer(Epochs epochs, Func<byte[], Task<byte[]>> exchange)
    {
        /// <summary>Binding и incarnation этого соединения.</summary>
        internal Epochs Epochs { get; } = epochs;
        /// <summary>Последний отправленный logical input sequence.</summary>
        internal ulong Sequence { get; private set; }
        /// <summary>Последний прочитанный output sequence, подтверждаемый следующим кадром.</summary>
        internal ulong Ack { get; private set; }

        /// <summary>Посылает одну логическую команду и сохраняет возвращённый output ACK.</summary>
        internal Task<byte[]> Send(RuntimeCommand command, int timeDs = 0) => SendRaw(
            RuntimeJson.Encode(new RuntimeInput(Epochs, Sequence + 1, timeDs, command), Ack));

        /// <summary>Передаёт exact encoded chunk; повторное кодирование не меняет checksum v3 незаметно для теста.</summary>
        internal async Task<byte[]> SendRaw(byte[] body)
        {
            var packet = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(body));
            var response = await exchange(body);
            using var json = JsonDocument.Parse(response);
            Sequence = packet.Input.Sequence;
            Ack = Counter(json.RootElement, "output_sequence");
            return response;
        }

        /// <summary>Poll читает события, не назначая input sequence или simulation time.</summary>
        internal async Task<byte[]> Poll(int maxEvents = 64)
        {
            var response = await exchange(RuntimeJson.EncodePoll(Epochs, Ack, maxEvents));
            using var json = JsonDocument.Parse(response);
            Ack = Counter(json.RootElement, "output_sequence");
            return response;
        }

        /// <summary>Завершает полный bootstrap; checksum v3 вычисляется из decoder-derived raw digest фактического chunk.</summary>
        internal async Task<byte[]> Synchronize(int checksumVersion = 2, int targetX = 2, int extraNodes = 0)
        {
            WorldChange[] facts =
            [
                new SectorUpsert(new("s", 1, 1, new([1]))),
                new NodeUpsert(new("source-node", 1, 1, new("A", "apc", new(1, 1, 1), "s", true, new("home", "home")))),
                new NodeUpsert(new("target-node", 1, 1, new("B", "server", new(targetX, 1, 1), "s", true, new("home", "home")))),
                new ServiceUpsert(new("source", 1, 1, new("source-node", 1), "test.source", "A", true,
                    new([new("read", OperationSafety.ReadOnly)], 64))),
                new ServiceUpsert(new("target", 1, 1, new("target-node", 1), "test.target", "B", true,
                    new([new("read", OperationSafety.ReadOnly)], 64))),
                .. Enumerable.Range(0, extraNodes).Select(index => new NodeUpsert(new NodeFact(
                    "extra-" + index.ToString("D3", CultureInfo.InvariantCulture), 1, 1,
                    new("C-" + index.ToString("D3", CultureInfo.InvariantCulture), "server", new(index + 3, 1, 1),
                        "s", true, new("home", "home")))))
            ];
            await Send(new ApplyWorld([new SnapshotBegin("snapshot", 0, checksumVersion)]));
            var chunk = new SnapshotChunk("snapshot", 1, facts);
            var encoded = RuntimeJson.Encode(new(Epochs, Sequence + 1, 0, new ApplyWorld([chunk])), Ack);
            var decoded = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(encoded));
            var rawChunk = Assert.IsType<SnapshotChunk>(Assert.Single(Assert.IsType<ApplyWorld>(decoded.Input.Command).Entries));
            var digest = SnapshotChecksum.Compute([rawChunk], checksumVersion);
            await SendRaw(encoded);
            await Send(new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)facts.Length, digest)]));
            return await Send(new ApplyWorld([new CatchUpComplete("snapshot", 0)]));
        }
    }

    /// <summary>Создаёт настоящую runtime session через production factory.</summary>
    private static IHostSession Session(RuntimeOptions? options = null) => new RuntimeHostSessionFactory(options).Create(Epochs);

    /// <summary>Адаптирует синхронный ValueTask session к test-peer без mock обработки кадров.</summary>
    private static Peer DirectPeer(IHostSession session) => new(Epochs, body => session.ProcessAsync(body, CancellationToken.None).AsTask());

    /// <summary>Возвращает ровно один output entry указанного вида, независимо от соседнего runtime_result.</summary>
    private static JsonElement Entry(JsonDocument output, string kind) => Assert.Single(output.RootElement.GetProperty("results")
        .EnumerateArray(), value => value.GetProperty("kind").GetString() == kind);

    /// <summary>Читает trusted-host decimal counter как строку, не округляя через JSON number.</summary>
    private static ulong Counter(JsonElement value, string field) => ulong.Parse(value.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);

    /// <summary>Payload имеет реальную форму scrubber filters: массив объектов внутри объекта.</summary>
    private static NetworkValue Payload() => NetworkValue.FromObject(new Dictionary<string, NetworkValue>
    {
        ["filter_types"] = NetworkValue.FromArray([
            NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["gas_id"] = NetworkValue.FromText("co2"), ["enabled"] = NetworkValue.FromBoolean(true) }),
            NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["gas_id"] = NetworkValue.FromText("plasma"), ["enabled"] = NetworkValue.FromBoolean(false) })]),
        ["null"] = NetworkValue.Null,
        ["empty_array"] = NetworkValue.FromArray([]),
        ["empty_object"] = NetworkValue.FromObject([])
    });

    /// <summary>Типизированное обращение к реальному объявленному тестовому сервису.</summary>
    private static NetworkRequest Request() => new(new("peer", 1), new("source", 1), new("target", 1), "read", Payload());

    /// <summary>Full snapshot обоих форматов публикует пригодный граф и выполняет отдельный native handoff/result lifecycle.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SnapshotReadyGraphAndInvocationUseRealRuntimeFactory(int checksumVersion)
    {
        using var session = Session();
        var peer = DirectPeer(session);
        using var ready = JsonDocument.Parse(await peer.Synchronize(checksumVersion));
        var state = Entry(ready, "runtime_result");
        Assert.Equal("ready", state.GetProperty("sync_state").GetString());
        Assert.False(state.GetProperty("topology_pending").GetBoolean());
        Assert.True(Counter(state, "graph_version") > 0);
        Assert.Equal(2, state.GetProperty("node_count").GetInt32());
        Assert.Equal(2, state.GetProperty("service_count").GetInt32());

        using var admitted = JsonDocument.Parse(await peer.Send(new SubmitRequest(Request())));
        Assert.Equal("accepted", Entry(admitted, "runtime_result").GetProperty("admission").GetProperty("status").GetString());
        var invocation = Entry(admitted, "service_invocation");
        Assert.Equal("peer:1", invocation.GetProperty("request_id").GetString());
        Assert.Equal("target-node", invocation.GetProperty("destination_node").GetProperty("node_id").GetString());
        Assert.Equal(1UL, Counter(invocation, "source_node_revision"));
        Assert.Equal(1UL, Counter(invocation, "destination_node_revision"));
        Assert.Equal(Counter(Entry(admitted, "runtime_result"), "infra_stamp"), Counter(invocation, "infra_stamp"));
        Assert.Equal(Counter(Entry(admitted, "runtime_result"), "av_infra_stamp"), Counter(invocation, "av_infra_stamp"));
        Assert.Equal(JsonValueKind.Null, invocation.GetProperty("communication_revision").ValueKind);
        Assert.True(Payload().DeepEquals(NetworkValueJson.Decode(invocation.GetProperty("payload"))));
        var invocationId = invocation.GetProperty("invocation_id").GetString()!;
        var response = NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["answer"] = NetworkValue.FromText("ok") });
        using var finished = JsonDocument.Parse(await peer.Send(new FinishInvocation(new(Epochs.Binding, invocationId, true, response))));
        var terminal = Entry(finished, "request_completed");
        Assert.Equal("completed", terminal.GetProperty("state").GetString());
        Assert.True(response.DeepEquals(NetworkValueJson.Decode(terminal.GetProperty("payload"))));
    }

    /// <summary>Host публикует readiness после своего Pump, иначе idle native peer не сможет начать работу.</summary>
    [Fact]
    public async Task DeferredPublicationReportsReadyWithoutAnotherNativeClockInput()
    {
        using var session = Session(new RuntimeOptions(DeferTopologyBuild: true));
        var peer = DirectPeer(session);
        using var ready = JsonDocument.Parse(await peer.Synchronize());
        var state = Entry(ready, "runtime_result");
        Assert.False(state.GetProperty("topology_pending").GetBoolean());
        Assert.True(Counter(state, "graph_version") > 0);
        Assert.True(Counter(state, "graph_input_sequence") > 0);
        using var admitted = JsonDocument.Parse(await peer.Send(new SubmitRequest(Request())));
        Assert.Equal("accepted", Entry(admitted, "runtime_result").GetProperty("admission").GetProperty("status").GetString());
        _ = Entry(admitted, "service_invocation");
    }

    /// <summary>Повтор input не создаёт второе effect event; unacknowledged output повторяется byte-for-byte.</summary>
    [Fact]
    public async Task InputDedupAndOutputJournalReplayDoNotReissueInvocation()
    {
        using var session = Session();
        var peer = DirectPeer(session);
        await peer.Synchronize();
        var acknowledged = peer.Ack;
        var input = RuntimeJson.Encode(new(Epochs, peer.Sequence + 1, 0, new SubmitRequest(Request())), acknowledged);
        var first = await session.ProcessAsync(input, CancellationToken.None);
        using var original = JsonDocument.Parse(first);
        var invocationId = Entry(original, "service_invocation").GetProperty("invocation_id").GetString();
        var duplicate = await session.ProcessAsync(input, CancellationToken.None);
        using var duplicateJson = JsonDocument.Parse(duplicate);
        Assert.Equal("duplicate", Entry(duplicateJson, "runtime_result").GetProperty("status").GetString());
        Assert.DoesNotContain(duplicateJson.RootElement.GetProperty("results").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "service_invocation");
        var replay = await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, acknowledged), CancellationToken.None);
        Assert.Equal(first, replay);
        using var replayJson = JsonDocument.Parse(replay);
        Assert.Equal(invocationId, Entry(replayJson, "service_invocation").GetProperty("invocation_id").GetString());
        Assert.Equal(first, await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, acknowledged), CancellationToken.None));
    }

    /// <summary>Неправильный binding и будущий ACK не изменяют подтверждённый output journal.</summary>
    [Fact]
    public async Task BindingAndFutureAckErrorsLeaveExistingOutputReplayIntact()
    {
        using var session = Session();
        var begin = RuntimeJson.Encode(new RuntimeInput(Epochs, 1, 0, new ApplyWorld([new SnapshotBegin("snapshot", 0, 2)])));
        var first = await session.ProcessAsync(begin, CancellationToken.None);
        var epochError = await Assert.ThrowsAsync<ProtocolException>(async () => await session.ProcessAsync(
            RuntimeJson.EncodePoll(Epochs with { Binding = "wrong" }, 100), CancellationToken.None));
        Assert.Equal("epoch_mismatch", epochError.Code);
        var ackError = await Assert.ThrowsAsync<ProtocolException>(async () => await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, 2), CancellationToken.None));
        Assert.Equal("output_ack_future", ackError.Code);
        Assert.Equal(first, await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, 0), CancellationToken.None));
    }

    /// <summary>Полный output journal отклоняет вход до engine; ACK освобождает место без потери непринятого sequence.</summary>
    [Fact]
    public async Task OutputPressureDoesNotConsumeTheNextLogicalInput()
    {
        using var session = Session();
        var peer = DirectPeer(session);
        await peer.Synchronize();
        var acknowledged = peer.Ack;
        var firstSequence = peer.Sequence + 1;
        ulong lastOutput = 0;
        for (ulong offset = 0; offset < 64; offset++)
        {
            using var output = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.Encode(
                new(Epochs, firstSequence + offset, 0, new ConfigureBackbone(true)), acknowledged), CancellationToken.None));
            lastOutput = Counter(output.RootElement, "output_sequence");
        }
        var next = new RuntimeInput(Epochs, firstSequence + 64, 0, new ConfigureBackbone(false));
        var pressure = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await session.ProcessAsync(RuntimeJson.Encode(next, acknowledged), CancellationToken.None));
        Assert.Equal("output_pressure", pressure.Code);
        using var drained = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, lastOutput), CancellationToken.None));
        Assert.Equal(firstSequence + 63, Counter(drained.RootElement, "applied_sequence"));
        using var retried = JsonDocument.Parse(await session.ProcessAsync(RuntimeJson.Encode(next,
            Counter(drained.RootElement, "output_sequence")), CancellationToken.None));
        Assert.Equal(next.Sequence, Counter(retried.RootElement, "applied_sequence"));
    }

    /// <summary>Большая source table дробится на bounded replies; ни ACK-only poll, ни replay не теряют её строки.</summary>
    [Fact]
    public async Task LargeProjectionDrainsInBoundedRepliesWithoutLosingOrReplayingRows()
    {
        using var session = Session(new(Projection: new(PageRows: 1)));
        var peer = DirectPeer(session);
        await peer.Synchronize(3, extraNodes: 64);
        var priorAck = peer.Ack;
        var first = await peer.Send(new QueryProjection(new(new("ui", 1), ProjectionKind.Routes, new("source-node", 1))));
        Assert.Equal(first, await session.ProcessAsync(RuntimeJson.EncodePoll(Epochs, priorAck, 128), CancellationToken.None));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong nextPage = 1;
        var complete = false;

        void ReadRows(byte[] body, bool diagnostic)
        {
            using var document = JsonDocument.Parse(body);
            var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(diagnostic ? 1 : 0, results.Count(entry => entry.GetProperty("kind").GetString() == "runtime_result"));
            Assert.InRange(results.Length, diagnostic ? 1 : 0, diagnostic ? 9 : 8);
            foreach (var page in results.Where(entry => entry.GetProperty("kind").GetString() == "projection_page"))
            {
                Assert.Equal("ui:1", page.GetProperty("request_id").GetString());
                Assert.Equal(nextPage++, Counter(page, "page_index"));
                foreach (var row in page.GetProperty("rows").EnumerateArray())
                    Assert.True(seen.Add(row.GetProperty("node").GetProperty("node_id").GetString()!), "A projection row is delivered once after ACK");
                Assert.False(complete, "The terminal page must be last");
                complete = page.GetProperty("complete").GetBoolean();
            }
        }

        ReadRows(first, true);
        using (var ackOnly = JsonDocument.Parse(await peer.Poll(0)))
            Assert.Empty(ackOnly.RootElement.GetProperty("results").EnumerateArray());
        for (var pass = 0; pass < 32 && !complete; pass++)
            ReadRows(await peer.Poll(128), false);
        Assert.True(complete, "The query drains without changing simulation time or resubmitting it");
        Assert.Equal(66, seen.Count);
        using var drained = JsonDocument.Parse(await peer.Poll(128));
        Assert.Empty(drained.RootElement.GetProperty("results").EnumerateArray());
    }

    /// <summary>Физическое ожидание и poll не двигают виртуальный transit; только новый logical time делает invocation due.</summary>
    [Fact]
    public async Task PollCannotAdvanceSimulationOrExecuteFutureDelivery()
    {
        using var session = Session();
        var peer = DirectPeer(session);
        await peer.Synchronize(targetX: 1501);
        using var admitted = JsonDocument.Parse(await peer.Send(new SubmitRequest(Request())));
        Assert.DoesNotContain(admitted.RootElement.GetProperty("results").EnumerateArray(), entry => entry.GetProperty("kind").GetString() == "service_invocation");
        var applied = Counter(admitted.RootElement, "applied_sequence");
        for (var i = 0; i < 3; i++)
        {
            using var poll = JsonDocument.Parse(await peer.Poll());
            Assert.Equal(applied, Counter(poll.RootElement, "applied_sequence"));
            Assert.Empty(poll.RootElement.GetProperty("results").EnumerateArray());
        }
        using var advanced = JsonDocument.Parse(await peer.Send(new QueryRoute(new("source-node", 1), new("target-node", 1)), 2));
        Assert.Equal("found", Entry(advanced, "runtime_result").GetProperty("route").GetProperty("status").GetString());
        _ = Entry(advanced, "service_invocation");
    }

    /// <summary>Один реальный TCP сценарий использует authentication и production session factory, а не echo handler.</summary>
    [Fact]
    public async Task AuthenticatedLoopbackHostCompletesRealSnapshotAndRequestFlow()
    {
        await using var host = new LoopbackHost(new(0, Token), new RuntimeHostSessionFactory());
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, host.Port);
            var stream = client.GetStream();
            await FramedStream.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "hello",
                protocol_major = 1,
                protocol_minor = 3,
                world_epoch = "tcp-world",
                binding_epoch = "tcp-binding",
                token = Token
            }));
            using var hello = JsonDocument.Parse(await ReadFrame(stream));
            Assert.Equal("hello_ack", hello.RootElement.GetProperty("kind").GetString());
            var epochs = new Epochs("tcp-world", hello.RootElement.GetProperty("engine_epoch").GetString()!, "tcp-binding");
            var peer = new Peer(epochs, async body => { await FramedStream.WriteAsync(stream, body); return await ReadFrame(stream); });
            using var ready = JsonDocument.Parse(await peer.Synchronize(3));
            Assert.Equal("ready", Entry(ready, "runtime_result").GetProperty("sync_state").GetString());
            using var submitted = JsonDocument.Parse(await peer.Send(new SubmitRequest(Request())));
            var invocation = Entry(submitted, "service_invocation");
            Assert.True(Payload().DeepEquals(NetworkValueJson.Decode(invocation.GetProperty("payload"))));
            using var finished = JsonDocument.Parse(await peer.Send(new FinishInvocation(new(epochs.Binding,
                invocation.GetProperty("invocation_id").GetString()!, true, NetworkValue.Null))));
            Assert.Equal("completed", Entry(finished, "request_completed").GetProperty("state").GetString());
        }
        finally { stop.Cancel(); await run; }
    }

    /// <summary>Физический test timeout не позволяет зависшей регрессии TCP блокировать CI.</summary>
    private static async Task<byte[]> ReadFrame(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = await FramedStream.ReadAsync(stream, timeout.Token);
        Assert.NotNull(body);
        return body;
    }
}
