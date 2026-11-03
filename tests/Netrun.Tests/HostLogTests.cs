using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет диагностику через реальные TCP-сессии, медленные sink и файлы ротации.</summary>
public sealed class HostLogTests
{
    /// <summary>Открытый секрет только для изолированного ephemeral listener.</summary>
    private const string Token = "host-log-test-only-token-at-least-32-characters";

    /// <summary>Моделирует устройство, которое не отвечает даже на отмену, без блокировки test thread.</summary>
    private sealed class BlockedWriter : StringWriter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            await base.WriteLineAsync(buffer, CancellationToken.None);
        }
    }

    /// <summary>Ошибка устройства намеренно содержит секрет, который не должен попасть в fallback-файл.</summary>
    private sealed class FailedWriter : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("do-not-export-exception-contents");
    }

    /// <summary>Реальный hello; отсутствие monitoring не должно отключать постоянные снимки.</summary>
    private static byte[] Hello(bool duplex = false) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        kind = "hello",
        protocol_major = 1,
        protocol_minor = 3,
        world_epoch = "log-world",
        binding_epoch = "log-binding",
        token = Token,
        duplex
    });

    /// <summary>Deadline относится только к fixture и не задаёт игровых таймаутов.</summary>
    private static async Task<JsonDocument> Read(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return JsonDocument.Parse((await FramedStream.ReadAsync(stream, timeout.Token))!);
    }

    /// <summary>Возвращает независимые JSON nodes после полного flush.</summary>
    private static JsonElement[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();

    /// <summary>Полная очередь и зависший stderr не задерживают handshake или ответ runtime.</summary>
    [Fact]
    public async Task BlockedSinkDoesNotBlockRuntimeAndShutdownHasDeadline()
    {
        var sink = new BlockedWriter();
        var log = new HostLog(sink, new(HostLogLevel.Debug, Capacity: 1, FlushTimeout: TimeSpan.FromMilliseconds(50)));
        log.TryWrite(new(HostLogLevel.Information, "seed"));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var host = new LoopbackHost(new(0, Token), new RuntimeHostSessionFactory(), log);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port);
            await FramedStream.WriteAsync(peer.GetStream(), Hello());
            using var hello = await Read(peer.GetStream());
            var epochs = new Epochs("log-world", hello.RootElement.GetProperty("engine_epoch").GetString()!, "log-binding");
            await FramedStream.WriteAsync(peer.GetStream(), RuntimeJson.Encode(new(epochs, 1, 0,
                new ApplyWorld([new SnapshotBegin("snapshot", 0, 3)]))));
            using var reply = await Read(peer.GetStream());
            Assert.Equal("output", reply.RootElement.GetProperty("kind").GetString());
            Assert.True(log.DroppedCount > 0);
        }
        finally
        {
            stop.Cancel();
            await run;
            var started = Stopwatch.GetTimestamp();
            await log.DisposeAsync();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3));
            sink.Release.TrySetResult();
        }
    }

    /// <summary>Отказ команды связан с исходным input/request/binding, но не с payload или opaque контекстом.</summary>
    [Fact]
    public async Task RejectedCommandAndProtocolFaultAreCorrelatedWithoutSecrets()
    {
        var sink = new StringWriter();
        var log = new HostLog(sink, new(HostLogLevel.Debug));
        await using var host = new LoopbackHost(new(0, Token), new RuntimeHostSessionFactory(), log);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port);
            var stream = peer.GetStream();
            await FramedStream.WriteAsync(stream, Hello());
            using var hello = await Read(stream);
            var epochs = new Epochs("log-world", hello.RootElement.GetProperty("engine_epoch").GetString()!, "log-binding");
            var request = new NetworkRequest(new("producer", 7), new("source", 1), new("destination", 1), "read",
                NetworkValue.FromText("do-not-export-payload"), AuthorizationContext: "do-not-export-authorization",
                NativeContext: "do-not-export-native");
            await FramedStream.WriteAsync(stream, RuntimeJson.Encode(new(epochs, 1, 0, new SubmitRequest(request))));
            using var rejected = await Read(stream);
            Assert.Equal("output", rejected.RootElement.GetProperty("kind").GetString());
            await FramedStream.WriteAsync(stream, Encoding.UTF8.GetBytes("{\"token\":\"do-not-export-bad-json\"}"));
            using var failed = await Read(stream);
            Assert.Equal("error", failed.RootElement.GetProperty("kind").GetString());
        }
        finally { stop.Cancel(); await run; await log.DisposeAsync(); }
        var text = sink.ToString();
        var lines = Lines(text);
        var command = Assert.Single(lines, line => line.GetProperty("event").GetString() == "command_rejected");
        Assert.Equal("1", command.GetProperty("input_sequence").GetString());
        Assert.Equal("producer:7", command.GetProperty("request_id").GetString());
        Assert.Equal("log-binding", command.GetProperty("binding_epoch").GetString());
        var fault = Assert.Single(lines, line => line.GetProperty("event").GetString() == "protocol_error");
        Assert.Equal(command.GetProperty("connection_id").GetString(), fault.GetProperty("connection_id").GetString());
        Assert.Contains(lines, line => line.GetProperty("event").GetString() == "session_disconnected");
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("do-not-export", text);
        Assert.DoesNotContain("payload", text);
    }

    /// <summary>Постоянные снимки работают без live monitor и не создают protocol outputs.</summary>
    [Fact]
    public async Task PersistentTelemetryDoesNotRequireNegotiatedMonitor()
    {
        var sink = new StringWriter();
        var log = new HostLog(sink, new(TelemetryInterval: TimeSpan.FromMilliseconds(10)));
        await using var host = new LoopbackHost(new(0, Token), new RuntimeHostSessionFactory(), log);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port);
            await FramedStream.WriteAsync(peer.GetStream(), Hello(duplex: true));
            using var hello = await Read(peer.GetStream());
            Assert.False(hello.RootElement.GetProperty("monitoring").GetBoolean());
            await Task.Delay(150);
            Assert.Equal(0, peer.Available);
        }
        finally { stop.Cancel(); await run; await log.DisposeAsync(); }
        var lines = Lines(sink.ToString());
        var metrics = lines.Where(line => line.GetProperty("event").GetString() == "runtime_metrics").ToArray();
        Assert.NotEmpty(metrics);
        Assert.All(metrics, line =>
        {
            var snapshot = line.GetProperty("metrics");
            Assert.Equal(0UL, snapshot.GetProperty("applied_sequence").GetUInt64());
            Assert.Equal(0, snapshot.GetProperty("journal_frames").GetInt32());
            Assert.Equal(0, snapshot.GetProperty("output_unacked").GetInt32());
            Assert.Equal("log-binding", line.GetProperty("binding_epoch").GetString());
        });
    }

    /// <summary>Файл продолжает принимать JSONL при отказе stderr и не содержит текста I/O exception.</summary>
    [Fact]
    public async Task FileExportSurvivesFailedStderr()
    {
        var path = Path.GetTempFileName();
        try
        {
            var log = new HostLog(new FailedWriter(), new(FilePath: path));
            Assert.True(log.TryWrite(new(HostLogLevel.Information, "first")));
            Assert.True(log.TryWrite(new(HostLogLevel.Warning, "second", InputSequence: ulong.MaxValue)));
            await log.DisposeAsync();
            var text = await File.ReadAllTextAsync(path);
            var lines = Lines(text);
            Assert.Equal(2, lines.Length);
            Assert.Equal(1, lines[1].GetProperty("sink_failures").GetInt32());
            Assert.Equal(ulong.MaxValue.ToString(CultureInfo.InvariantCulture), lines[1].GetProperty("input_sequence").GetString());
            Assert.Equal(1, log.SinkFailureCount);
            Assert.Equal(0, log.DroppedCount);
            Assert.DoesNotContain("do-not-export", text);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Ротация ограничивает число архивов и сохраняет целые parseable записи.</summary>
    [Fact]
    public async Task RotationRetainsOnlyConfiguredArchives()
    {
        var path = Path.GetTempFileName();
        try
        {
            var log = new HostLog(TextWriter.Null, new(FilePath: path, MaximumFileBytes: 1, RetainedFiles: 2));
            for (var index = 0; index < 6; index++)
                Assert.True(log.TryWrite(new(HostLogLevel.Information, "record", InputSequence: (ulong)index)));
            await log.DisposeAsync();
            Assert.Equal("5", Assert.Single(Lines(await File.ReadAllTextAsync(path))).GetProperty("input_sequence").GetString());
            Assert.Equal("4", Assert.Single(Lines(await File.ReadAllTextAsync(path + ".1"))).GetProperty("input_sequence").GetString());
            Assert.Equal("3", Assert.Single(Lines(await File.ReadAllTextAsync(path + ".2"))).GetProperty("input_sequence").GetString());
            Assert.False(File.Exists(path + ".3"));
            Assert.Equal(0, log.SinkFailureCount);
        }
        finally { foreach (var suffix in new[] { "", ".1", ".2", ".3" }) File.Delete(path + suffix); }
    }

    /// <summary>Опциональный файл включает историю с частотой 2 с; явный ноль отключает её.</summary>
    [Fact]
    public void EnvironmentSettingsAreExplicitAndValidated()
    {
        var environment = new Dictionary<string, string> { ["NETRUN_LOG_FILE"] = "host.jsonl" };
        string? ReadSetting(string key) => environment.GetValueOrDefault(key);
        var defaults = HostLogOptions.FromEnvironment(ReadSetting);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.TelemetryInterval);
        Assert.Equal(4096, defaults.Capacity);
        environment["NETRUN_LOG_TELEMETRY_INTERVAL_MS"] = "0";
        Assert.Null(HostLogOptions.FromEnvironment(ReadSetting).TelemetryInterval);
        environment["NETRUN_LOG_CAPACITY"] = "-1";
        Assert.Throws<ArgumentException>(() => HostLogOptions.FromEnvironment(ReadSetting));
        environment["NETRUN_LOG_CAPACITY"] = "1";
        environment["NETRUN_LOG_LEVEL"] = Token;
        Assert.DoesNotContain(Token, Assert.Throws<ArgumentException>(() => HostLogOptions.FromEnvironment(ReadSetting)).Message);
    }
}
