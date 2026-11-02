using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Netrun.Core;
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

    /// <summary>Недопустимые, дублирующиеся или неверно подписанные hello не создают состояние мира.</summary>
    [Theory]
    [InlineData("bad_token")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("invalid_utf8")]
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

    /// <summary>Секрет не попадает в строковое представление конфигурации или исключение о неправильной форме.</summary>
    [Fact]
    public void HostConfigurationDiagnosticsDoNotRevealToken()
    {
        Assert.DoesNotContain(Token, new LoopbackHostOptions(0, Token).ToString());
        Assert.Throws<ArgumentException>(() => new LoopbackHostOptions(0, "short"));
    }
}
