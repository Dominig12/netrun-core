using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет реальный loopback socket, допуск до доменного состояния и барьеры reconnect.</summary>
public sealed class LoopbackHostTests
{
    /// <summary>Публичный тестовый секрет, не используемый вне изолированных ephemeral listeners.</summary>
    private const string Token = "test-only-token-with-at-least-32-characters";

    /// <summary>Считает создание состояния; неправильный handshake не должен вызывать Create.</summary>
    private sealed class Factory : IHostSessionFactory
    {
        /// <summary>Число допущенных сессий.</summary>
        internal int Created;
        /// <summary>Создаёт простую echo-сессию, отделяя проверку TCP от engine.</summary>
        public IHostSession Create(Epochs epochs)
        {
            Interlocked.Increment(ref Created);
            return new EchoSession();
        }
    }

    /// <summary>Фиктивный обработчик не выполняет эффектов; проверяется framing произвольного JSON-тела.</summary>
    private sealed class EchoSession : IHostSession
    {
        /// <summary>Возвращает независимую копию тела для проверки stream boundaries.</summary>
        public ValueTask<byte[]> ProcessAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
            ValueTask.FromResult(body.ToArray());
        /// <summary>Незавершённых invocation в echo-стенде нет.</summary>
        public void Disconnected() { }
        /// <summary>Echo не владеет внешними ресурсами.</summary>
        public void Dispose() { }
    }

    /// <summary>Только socket-stall fixture: runtime ownership проверяет отдельный настоящий TCP scenario.</summary>
    private sealed class PushFactory : IHostSessionFactory
    {
        internal readonly PushSession Session = new();
        public IHostSession Create(Epochs epochs) => Session;
    }

    /// <summary>Максимальные immutable тела заполняют bounded egress, когда peer перестаёт читать.</summary>
    private sealed class PushSession : IStreamingHostSession
    {
        public StreamingHostLimits Limits { get; } = new(QueueCapacity: 8);
        private readonly byte[] _body = new byte[FramedStream.MaxFrameBytes];
        internal int Produced;
        internal int DisconnectedCount;
        internal int DisposedCount;
        public void EnableStreaming() { }
        public ValueTask<byte[]> ProcessAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<byte[]?> ProcessStreamingAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask<IReadOnlyList<byte[]>> ProcessStreamingBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> bodies, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<byte[]>>([]);
        public byte[]? TryProduceOutput() { Interlocked.Increment(ref Produced); return _body; }
        public void Disconnected() => Interlocked.Increment(ref DisconnectedCount);
        public void Dispose() => Interlocked.Increment(ref DisposedCount);
    }

    /// <summary>Создаёт точную допустимую hello-схему; токен остаётся только внутри локального тестового потока.</summary>
    private static byte[] Hello(string binding, string token = Token) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        kind = "hello",
        protocol_major = 1,
        protocol_minor = 3,
        world_epoch = "test-world",
        binding_epoch = binding,
        token
    });

    /// <summary>Принимает framed JSON с физическим пределом ожидания, чтобы регресс не зависал в CI.</summary>
    private static async Task<JsonDocument> ReadAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = await FramedStream.ReadAsync(stream, timeout.Token);
        Assert.NotNull(body);
        return JsonDocument.Parse(body);
    }

    /// <summary>Доменная обработка становится доступна только после успешной аутентификации.</summary>
    [Fact]
    public async Task AuthenticatedSessionExchangesFramesOnLoopback()
    {
        var factory = new Factory();
        await using var host = new LoopbackHost(new(0, Token), factory);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, host.Port);
            var stream = client.GetStream();
            await FramedStream.WriteAsync(stream, Hello("binding"));
            using var hello = await ReadAsync(stream);
            Assert.Equal("hello_ack", hello.RootElement.GetProperty("kind").GetString());
            Assert.Equal("binding", hello.RootElement.GetProperty("binding_epoch").GetString());
            Assert.False(string.IsNullOrEmpty(hello.RootElement.GetProperty("engine_epoch").GetString()));
            var body = Encoding.UTF8.GetBytes("{\"probe\":\"проверка\"}");
            await FramedStream.WriteAsync(stream, body);
            using var echo = await ReadAsync(stream);
            Assert.Equal("проверка", echo.RootElement.GetProperty("probe").GetString());
            Assert.Equal(1, factory.Created);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    /// <summary>Idle telemetry появляется только после opt-in, не требует poll и не двигает runtime/replay clock.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MonitoringIsNegotiatedAndIdleSamplesNeedNoApplicationAcknowledgement(bool monitoring)
    {
        await using var host = new LoopbackHost(new(0, Token), new RuntimeHostSessionFactory());
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, host.Port);
            var stream = client.GetStream();
            await FramedStream.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "hello",
                protocol_major = 1,
                protocol_minor = 3,
                world_epoch = "monitor-world",
                binding_epoch = "monitor-binding",
                token = Token,
                duplex = true,
                monitoring
            }));
            using var hello = await ReadAsync(stream);
            Assert.Equal(monitoring, hello.RootElement.GetProperty("monitoring").GetBoolean());
            var epochs = new Epochs("monitor-world", hello.RootElement.GetProperty("engine_epoch").GetString()!, "monitor-binding");
            if (monitoring)
            {
                for (var index = 1; index <= 2; index++)
                {
                    using var sample = await ReadAsync(stream);
                    var root = sample.RootElement;
                    Assert.Equal("monitor", root.GetProperty("kind").GetString());
                    Assert.Equal(epochs.World, root.GetProperty("world_epoch").GetString());
                    Assert.Equal(epochs.Engine, root.GetProperty("engine_epoch").GetString());
                    Assert.Equal(epochs.Binding, root.GetProperty("binding_epoch").GetString());
                    Assert.Equal(index.ToString(System.Globalization.CultureInfo.InvariantCulture), root.GetProperty("sample_sequence").GetString());
                    Assert.False(root.TryGetProperty("output_sequence", out _));
                    var metrics = root.GetProperty("host_metrics");
                    Assert.Equal("awaiting_snapshot", metrics.GetProperty("sync_state").GetString());
                    Assert.Equal("0", metrics.GetProperty("applied_sequence").GetString());
                    Assert.Equal(0, metrics.GetProperty("output_journal_frames").GetInt32());
                    Assert.Equal(0, metrics.GetProperty("output_unacked").GetInt32());
                    Assert.Equal(0, metrics.GetProperty("simulation_time_ds").GetInt32());
                    Assert.DoesNotContain(Token, root.GetRawText());
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2250));
                Assert.False(stream.DataAvailable);
            }
            // Две samples без ACK не расходуют output sequence; первый domain ответ по-прежнему имеет sequence 1.
            await FramedStream.WriteAsync(stream, RuntimeJson.Encode(new(epochs, 1, 0,
                new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)]))));
            using var applied = await ReadAsync(stream);
            Assert.Equal("output", applied.RootElement.GetProperty("kind").GetString());
            Assert.Equal("1", applied.RootElement.GetProperty("output_sequence").GetString());
            Assert.Equal("1", applied.RootElement.GetProperty("applied_sequence").GetString());
        }
        finally { stop.Cancel(); await run; }
    }

    /// <summary>Закрытие stalled reader отменяет writer и owner; ни один producer не переживает session.Dispose.</summary>
    [Fact]
    public async Task DuplexSlowConsumerCanDisconnectWithoutLeakingOwnerOrWriter()
    {
        var factory = new PushFactory();
        await using var host = new LoopbackHost(new(0, Token), factory);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        using var client = new TcpClient { ReceiveBufferSize = 1024 };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, host.Port);
            var stream = client.GetStream();
            var hello = Encoding.UTF8.GetString(Hello("stream-stall"));
            await FramedStream.WriteAsync(stream, Encoding.UTF8.GetBytes(hello[..^1] + ",\"duplex\":true}"));
            using var acknowledged = await ReadAsync(stream);
            Assert.True(acknowledged.RootElement.GetProperty("duplex").GetBoolean());
            // Stop consuming while the background producer fills TCP and the bounded output channel.
            await Task.Delay(500);
            Assert.InRange(Volatile.Read(ref factory.Session.Produced), 1, 24);
        }
        finally
        {
            client.Dispose();
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, factory.Session.DisconnectedCount);
        Assert.Equal(1, factory.Session.DisposedCount);
        var produced = factory.Session.Produced;
        await Task.Delay(75);
        Assert.Equal(produced, factory.Session.Produced);
    }

    /// <summary>Недопустимые, дублирующиеся или неверно подписанные hello не создают состояние мира.</summary>
    [Theory]
    [InlineData("bad_token")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("invalid_utf8")]
    [InlineData("bad_duplex")]
    [InlineData("duplicate_duplex")]
    [InlineData("bad_monitoring")]
    [InlineData("duplicate_monitoring")]
    public async Task InvalidHandshakeCannotReachFactory(string scenario)
    {
        var factory = new Factory();
        await using var host = new LoopbackHost(new(0, Token), factory);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, host.Port);
            var stream = client.GetStream();
            var body = scenario switch
            {
                "bad_token" => Hello("binding", new string('!', 32)),
                "duplicate" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Hello("binding")).Replace("\"kind\":\"hello\"", "\"kind\":\"hello\",\"kind\":\"hello\"")),
                "unknown" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Hello("binding")).Replace("\"kind\":\"hello\"", "\"kind\":\"hello\",\"extra\":true")),
                "bad_duplex" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Hello("binding")).Replace("\"kind\":\"hello\"", "\"kind\":\"hello\",\"duplex\":1")),
                "duplicate_duplex" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Hello("binding")).Replace("\"kind\":\"hello\"", "\"kind\":\"hello\",\"duplex\":true,\"duplex\":true")),
                "bad_monitoring" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Hello("binding")).Replace("\"kind\":\"hello\"", "\"kind\":\"hello\",\"monitoring\":1")),
                "duplicate_monitoring" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Hello("binding")).Replace("\"kind\":\"hello\"", "\"kind\":\"hello\",\"monitoring\":true,\"monitoring\":true")),
                _ => new byte[] { 0xff }
            };
            await FramedStream.WriteAsync(stream, body);
            using var error = await ReadAsync(stream);
            Assert.Equal("authentication_failed", error.RootElement.GetProperty("code").GetString());
            Assert.Equal(0, factory.Created);
            Assert.DoesNotContain(Token, error.RootElement.GetRawText());
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    /// <summary>Повторное подключение требует нового binding; потерянные эффекты не переигрываются старой сессией.</summary>
    [Fact]
    public async Task ReconnectRejectsRetiredBindingAndAcceptsFreshOne()
    {
        var factory = new Factory();
        await using var host = new LoopbackHost(new(0, Token), factory);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            foreach (var (binding, expected) in new[] { ("same", "hello_ack"), ("same", "error"), ("new", "hello_ack") })
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, host.Port);
                var stream = client.GetStream();
                await FramedStream.WriteAsync(stream, Hello(binding));
                using var reply = await ReadAsync(stream);
                Assert.Equal(expected, reply.RootElement.GetProperty("kind").GetString());
            }
            Assert.Equal(2, factory.Created);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    /// <summary>History saturation rotates the engine fence rather than permanently rejecting fresh connections.</summary>
    [Fact]
    public async Task SaturatedBindingHistoryRotatesEngineAndRejectsOldSequencedFrames()
    {
        await using var host = new LoopbackHost(new(0, Token, maxRetiredBindings: 2), new RuntimeHostSessionFactory());
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        string? firstEngine = null;
        string? rotatedEngine = null;
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var binding = "binding-" + index;
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, host.Port);
                var stream = client.GetStream();
                await FramedStream.WriteAsync(stream, Hello(binding));
                using var hello = await ReadAsync(stream);
                Assert.Equal("hello_ack", hello.RootElement.GetProperty("kind").GetString());
                var engine = hello.RootElement.GetProperty("engine_epoch").GetString()!;
                firstEngine ??= engine;
                if (index < 2)
                    Assert.Equal(firstEngine, engine);
                else
                {
                    Assert.NotEqual(firstEngine, engine);
                    rotatedEngine ??= engine;
                    Assert.Equal(rotatedEngine, engine);
                    var epochs = new Epochs("test-world", index == 2 ? firstEngine : engine, binding);
                    await FramedStream.WriteAsync(stream, RuntimeJson.Encode(new(epochs, 1, 0,
                        new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)]))));
                    using var result = await ReadAsync(stream);
                    if (index == 2)
                    {
                        Assert.Equal("error", result.RootElement.GetProperty("kind").GetString());
                        Assert.Equal("epoch_mismatch", result.RootElement.GetProperty("code").GetString());
                    }
                    else
                    {
                        Assert.Equal("output", result.RootElement.GetProperty("kind").GetString());
                        Assert.Equal("1", result.RootElement.GetProperty("applied_sequence").GetString());
                    }
                }
            }
        }
        finally { stop.Cancel(); await run; }
    }

    /// <summary>Секрет не попадает в строковое представление конфигурации или исключение о неправильной форме.</summary>
    [Fact]
    public void HostConfigurationDiagnosticsDoNotRevealToken()
    {
        Assert.DoesNotContain(Token, new LoopbackHostOptions(0, Token).ToString());
        Assert.Throws<ArgumentException>(() => new LoopbackHostOptions(0, "short"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackHostOptions(0, Token, maxRetiredBindings: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackHostOptions(0, Token, maxRetiredBindings: 4097));
    }
}
