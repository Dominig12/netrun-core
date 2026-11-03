using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Protocol;

namespace Netrun.Host;

/// <summary>Один последовательный локальный мир; не открывает внешний интерфейс и не допускает клиента до handshake.</summary>
public sealed class LoopbackHost : IAsyncDisposable
{
    /// <summary>Конфигурация и защищённая проверка session token.</summary>
    private readonly LoopbackHostOptions _options;
    /// <summary>Создание нового доменного состояния отделено от сетевого цикла.</summary>
    private readonly IHostSessionFactory _factory;
    /// <summary>Диагностика не ожидает I/O и не содержит handshake token.</summary>
    private readonly HostLog? _log;
    /// <summary>Listener всегда связан с IPv4 loopback; backlog ограничен одним ожидающим соединением.</summary>
    private readonly TcpListener _listener;
    /// <summary>Инкарнация протокола Host; меняется между сессиями при заполнении retired binding history.</summary>
    private string _engineEpoch = Guid.NewGuid().ToString("N");
    /// <summary>Одна engine epoch не принимает старый binding повторно после disconnect.</summary>
    private readonly HashSet<(string World, string Binding)> _usedBindings = [];
    /// <summary>Строгое UTF-8 без молчаливой замены испорченных секретов/идентификаторов.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>Не допускает одновременные циклы RunAsync на одном listener.</summary>
    private bool _running;

    /// <summary>Фактический локальный порт; доступен после запуска listener в конструкторе.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Открывает только локальный endpoint; авторизация каждой сессии выполняется отдельно.</summary>
    public LoopbackHost(LoopbackHostOptions options, IHostSessionFactory factory, HostLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        _options = options;
        _factory = factory;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, options.Port);
        _listener.Start(1);
    }

    /// <summary>Обслуживает соединения последовательно; отмена завершает ожидание без блокировки DM-потока.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_running)
            throw new InvalidOperationException("Host is already running.");
        _running = true;
        _log?.TryWrite(new(HostLogLevel.Information, "host_started"));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                var connectionId = Guid.NewGuid().ToString("N");
                var reason = "peer_closed";
                _log?.TryWrite(new(HostLogLevel.Information, "connection_accepted", connectionId));
                try { await ServeAsync(client.GetStream(), connectionId, cancellationToken); }
                catch (IOException) { reason = "io_error"; }
                catch (SocketException) { reason = "socket_error"; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { reason = "io_timeout"; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { reason = "host_stopping"; throw; }
                finally { _log?.TryWrite(new(HostLogLevel.Information, "connection_closed", connectionId, Code: reason)); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            _running = false;
            _log?.TryWrite(new(HostLogLevel.Information, "host_stopped"));
        }
    }

    /// <summary>Закрывает listener; владелец также отменяет RunAsync и дожидается завершения активной сессии.</summary>
    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>Проверяет hello до создания доменного состояния, затем связывает framed stream с одним binding.</summary>
    private async Task ServeAsync(NetworkStream stream, string connectionId, CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_options.HandshakeTimeout);
        Epochs? epochs;
        var duplex = false;
        var monitoring = false;
        try
        {
            var body = await FramedStream.ReadAsync(stream, handshake.Token);
            epochs = body is null ? null : ReadHello(body, out duplex, out monitoring);
        }
        catch (ProtocolException error)
        {
            _log?.TryWrite(new(HostLogLevel.Warning, "protocol_error", connectionId, Code: error.Code));
            epochs = null;
        }
        if (epochs is null)
        {
            _log?.TryWrite(new(HostLogLevel.Warning, "authentication_rejected", connectionId, Code: "authentication_failed"));
            await FramedStream.WriteAsync(stream, Error("authentication_failed"), handshake.Token);
            return;
        }
        if (_usedBindings.Contains((epochs.World, epochs.Binding)))
        {
            _log?.TryWrite(new(HostLogLevel.Warning, "authentication_rejected", connectionId, epochs, Code: "binding_retired"));
            await FramedStream.WriteAsync(stream, Error("binding_retired"), handshake.Token);
            return;
        }
        if (_usedBindings.Count >= _options.MaxRetiredBindings)
        {
            // A permanent capacity refusal would prevent recovery after enough reconnects.
            // Rotate the engine fence before dropping history: an old sequenced frame
            // remains invalid even if its world/binding pair is used in this new epoch.
            _engineEpoch = Guid.NewGuid().ToString("N");
            _usedBindings.Clear();
            epochs = epochs with { Engine = _engineEpoch };
        }
        _usedBindings.Add((epochs.World, epochs.Binding));
        var diagnostics = _log is null ? null : new HostLogSession(_log, connectionId, epochs);
        using var session = _factory.Create(epochs);
        if (session is RuntimeHostSession runtime) runtime.AttachLog(diagnostics);
        try
        {
            diagnostics?.Write(HostLogLevel.Information, "session_started");
            if (duplex && session is not IStreamingHostSession)
                throw new ProtocolException("duplex_unsupported");
            if (duplex) ((IStreamingHostSession)session).EnableStreaming();
            monitoring = monitoring && duplex && session is IMonitoredHostSession;
            await FramedStream.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "hello_ack",
                protocol_major = 1,
                protocol_minor = 3,
                world_epoch = epochs.World,
                engine_epoch = epochs.Engine,
                binding_epoch = epochs.Binding,
                max_frame_bytes = FramedStream.MaxFrameBytes,
                max_entries = BatchJson.MaxEntries,
                max_input_commands = RuntimeJson.MaxInputCommands,
                duplex,
                monitoring,
                max_inflight_frames = duplex ? ((IStreamingHostSession)session).Limits.MaximumInflightFrames : 1,
                max_graph_pages = duplex ? ((IStreamingHostSession)session).Limits.GraphPageWindow : 1,
                snapshot_checksum_version = 3
            }), handshake.Token);
            if (duplex)
            {
                await StreamingConnection.RunAsync(stream, (IStreamingHostSession)session, _options.IdleTimeout, cancellationToken, monitoring);
                return;
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(_options.IdleTimeout);
                var body = await FramedStream.ReadAsync(stream, idle.Token);
                if (body is null)
                    break;
                var response = await session.ProcessAsync(body, idle.Token);
                await FramedStream.WriteAsync(stream, response, idle.Token);
            }
        }
        catch (ProtocolException error)
        {
            diagnostics?.Write(HostLogLevel.Warning, "protocol_error", error.Code);
            using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            failure.CancelAfter(_options.HandshakeTimeout);
            await FramedStream.WriteAsync(stream, Error(error.Code), failure.Token);
        }
        finally
        {
            session.Disconnected();
            diagnostics?.Write(HostLogLevel.Information, "session_disconnected");
        }
    }

    /// <summary>Строгий минимальный handshake: неизвестные и дублирующиеся поля запрещены, ошибки не содержат токена.</summary>
    private Epochs? ReadHello(byte[] body, out bool duplex, out bool monitoring)
    {
        duplex = false;
        monitoring = false;
        try
        {
            _ = StrictUtf8.GetCharCount(body);
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var remaining = new HashSet<string>(["kind", "protocol_major", "protocol_minor", "world_epoch", "binding_epoch", "token"], StringComparer.Ordinal);
            var seenDuplex = false;
            var seenMonitoring = false;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name == "monitoring")
                {
                    if (seenMonitoring || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                    seenMonitoring = true;
                    monitoring = property.Value.GetBoolean();
                    continue;
                }
                if (property.Name == "duplex")
                {
                    if (seenDuplex || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                    seenDuplex = true;
                    duplex = property.Value.GetBoolean();
                    continue;
                }
                if (!remaining.Remove(property.Name))
                    return null;
            }
            if (remaining.Count != 0 || root.GetProperty("kind").GetString() != "hello" ||
                root.GetProperty("protocol_major").GetInt32() != 1 || root.GetProperty("protocol_minor").GetInt32() != 3 ||
                !_options.Authenticate(root.GetProperty("token").GetString()))
                return null;
            var world = root.GetProperty("world_epoch").GetString();
            var binding = root.GetProperty("binding_epoch").GetString();
            if (!ValidEpoch(world) || !ValidEpoch(binding))
                return null;
            return new Epochs(world!, _engineEpoch, binding!);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Малый printable ID без исполняемых адресов и управляющих символов.</summary>
    private static bool ValidEpoch(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => character is >= '!' and <= '~');

    /// <summary>Безопасный ответ об отказе без входного содержимого.</summary>
    private static byte[] Error(string code) => JsonSerializer.SerializeToUtf8Bytes(new { kind = "error", code });
}
