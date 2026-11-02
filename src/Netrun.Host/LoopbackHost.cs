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
    /// <summary>Listener всегда связан с IPv4 loopback; backlog ограничен одним ожидающим соединением.</summary>
    private readonly TcpListener _listener;
    /// <summary>Инкарнация процесса Host; одинаковая во всех hello_ack этого запуска.</summary>
    private readonly string _engineEpoch = Guid.NewGuid().ToString("N");
    /// <summary>Процесс не принимает старый binding повторно после disconnect.</summary>
    private readonly HashSet<(string World, string Binding)> _usedBindings = [];
    /// <summary>Ограничение истории bind/reconnect исключает неограниченную память при churn подключений.</summary>
    private const int MaxBindings = 4096;
    /// <summary>Строгое UTF-8 без молчаливой замены испорченных секретов/идентификаторов.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>Не допускает одновременные циклы RunAsync на одном listener.</summary>
    private bool _running;

    /// <summary>Фактический локальный порт; доступен после запуска listener в конструкторе.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Открывает только локальный endpoint; авторизация каждой сессии выполняется отдельно.</summary>
    public LoopbackHost(LoopbackHostOptions options, IHostSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        _options = options;
        _factory = factory;
        _listener = new TcpListener(IPAddress.Loopback, options.Port);
        _listener.Start(1);
    }

    /// <summary>Обслуживает соединения последовательно; отмена завершает ожидание без блокировки DM-потока.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_running)
            throw new InvalidOperationException("Host is already running.");
        _running = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                try { await ServeAsync(client.GetStream(), cancellationToken); }
                catch (IOException) { }
                catch (SocketException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Закрывает listener; владелец также отменяет RunAsync и дожидается завершения активной сессии.</summary>
    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>Проверяет hello до создания доменного состояния, затем связывает framed stream с одним binding.</summary>
    private async Task ServeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_options.HandshakeTimeout);
        Epochs? epochs;
        try
        {
            var body = await FramedStream.ReadAsync(stream, handshake.Token);
            epochs = body is null ? null : ReadHello(body);
        }
        catch (ProtocolException)
        {
            epochs = null;
        }
        if (epochs is null)
        {
            await FramedStream.WriteAsync(stream, Error("authentication_failed"), handshake.Token);
            return;
        }
        if (_usedBindings.Count >= MaxBindings || !_usedBindings.Add((epochs.World, epochs.Binding)))
        {
            await FramedStream.WriteAsync(stream, Error("binding_retired"), handshake.Token);
            return;
        }
        using var session = _factory.Create(epochs);
        try
        {
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
                snapshot_checksum_version = 3
            }), handshake.Token);
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
            using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            failure.CancelAfter(_options.HandshakeTimeout);
            await FramedStream.WriteAsync(stream, Error(error.Code), failure.Token);
        }
        finally
        {
            session.Disconnected();
        }
    }

    /// <summary>Строгий минимальный handshake: неизвестные и дублирующиеся поля запрещены, ошибки не содержат токена.</summary>
    private Epochs? ReadHello(byte[] body)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(body);
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var remaining = new HashSet<string>(["kind", "protocol_major", "protocol_minor", "world_epoch", "binding_epoch", "token"], StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!remaining.Remove(property.Name))
                    return null;
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
