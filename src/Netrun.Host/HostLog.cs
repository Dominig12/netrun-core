using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Netrun.Host;

/// <summary>Уровень структурированной диагностики; None отключает журналирование.</summary>
public enum HostLogLevel
{
    /// <summary>Корреляция отдельных команд и выдаваемых событий.</summary>
    Debug,
    /// <summary>Состояния процесса, соединения и синхронизации.</summary>
    Information,
    /// <summary>Отклонённые команды, ошибки протокола и недоказанные результаты.</summary>
    Warning,
    /// <summary>Отказ процесса или устройства журналирования.</summary>
    Error,
    /// <summary>Не создавать записи.</summary>
    None
}

/// <summary>Ограничения отдельной очереди диагностики; они не ограничивают игровой транспорт.</summary>
/// <param name="MinimumLevel">Минимальный записываемый уровень.</param>
/// <param name="Capacity">Число ожидающих immutable записей; при заполнении новая запись теряется с учётом счётчика.</param>
/// <param name="FilePath">Дополнительный JSONL-файл в append-режиме; null оставляет только переданный stderr.</param>
/// <param name="FlushTimeout">Предел ожидания завершения worker при остановке; null выбирает две секунды.</param>
/// <param name="TelemetryInterval">Интервал агрегированных снимков; null отключает постоянную телеметрию.</param>
/// <param name="MaximumFileBytes">Мягкая граница активного файла: ротация перед следующей целой записью.</param>
/// <param name="RetainedFiles">Число предыдущих файлов .1 ... .N помимо активного.</param>
public sealed record HostLogOptions(HostLogLevel MinimumLevel = HostLogLevel.Information, int Capacity = 4096,
    string? FilePath = null, TimeSpan? FlushTimeout = null, TimeSpan? TelemetryInterval = null,
    long MaximumFileBytes = 16 * 1024 * 1024, int RetainedFiles = 4)
{
    /// <summary>Читает только явно перечисленные настройки; значения секретов не входят в сообщения об ошибке.</summary>
    public static HostLogOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var level = read("NETRUN_LOG_LEVEL")?.ToLowerInvariant() switch
        {
            null or "info" or "information" => HostLogLevel.Information,
            "debug" => HostLogLevel.Debug,
            "warning" => HostLogLevel.Warning,
            "error" => HostLogLevel.Error,
            "none" => HostLogLevel.None,
            _ => throw new ArgumentException("Invalid log level.")
        };
        long Number(string name, long fallback, long minimum, long maximum)
        {
            var text = read(name);
            if (text is null) return fallback;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
                throw new ArgumentException("Invalid log setting.");
            return value;
        }
        var file = read("NETRUN_LOG_FILE");
        var interval = Number("NETRUN_LOG_TELEMETRY_INTERVAL_MS", file is null ? 0 : 2000, 0, 3600000);
        return new(level, (int)Number("NETRUN_LOG_CAPACITY", 4096, 1, 1000000), file,
            TelemetryInterval: interval == 0 ? null : TimeSpan.FromMilliseconds(interval),
            MaximumFileBytes: Number("NETRUN_LOG_MAX_FILE_BYTES", 16 * 1024 * 1024, 1, 1024L * 1024 * 1024 * 1024),
            RetainedFiles: (int)Number("NETRUN_LOG_RETAINED_FILES", 4, 1, 32));
    }
}

/// <summary>Постоянный экспорт агрегированных scalar counters; поля не содержат объектов мира или payload.</summary>
/// <param name="SyncState">Состояние синхронизации.</param>
/// <param name="AppliedSequence">Причинная граница входа.</param>
/// <param name="GraphVersion">Опубликованная версия графа.</param>
/// <param name="TopologyPending">Ожидается публикация новой топологии.</param>
/// <param name="Nodes">Число узлов.</param>
/// <param name="Services">Число сервисов.</param>
/// <param name="ActiveRequests">Незавершённые запросы.</param>
/// <param name="ActiveDiscovery">Незавершённые поиски.</param>
/// <param name="ActiveProjections">Незавершённые UI-проекции.</param>
/// <param name="PendingEvents">События runtime до упаковки.</param>
/// <param name="InputUnacked">Ожидающие cumulative ACK входы.</param>
/// <param name="OutputUnacked">Ожидающие cumulative ACK выходы.</param>
/// <param name="JournalFrames">Кадры в output replay journal.</param>
/// <param name="JournalBytes">Байты output replay journal.</param>
/// <param name="InputQueueFrames">Кадры ingress.</param>
/// <param name="InputQueueBytes">Байты ingress.</param>
/// <param name="OutputQueueFrames">Кадры egress.</param>
/// <param name="OutputQueueBytes">Байты egress.</param>
/// <param name="ProcessingUs">Время последней обработки owner без ожидания TCP.</param>
/// <param name="MaxProcessingUs">Максимальное время обработки owner.</param>
/// <param name="ManagedBytes">Текущее наблюдение managed memory без принудительного GC.</param>
public sealed record HostLogMetrics(string SyncState, ulong AppliedSequence, ulong GraphVersion, bool TopologyPending,
    int Nodes, int Services, int ActiveRequests, int ActiveDiscovery, int ActiveProjections, int PendingEvents,
    int InputUnacked, ulong OutputUnacked, int JournalFrames, long JournalBytes, long InputQueueFrames,
    long InputQueueBytes, long OutputQueueFrames, long OutputQueueBytes, double ProcessingUs, double MaxProcessingUs,
    long ManagedBytes);

/// <summary>Разрешённые scalar-поля; произвольные объекты, exception messages и payload отсутствуют в контракте.</summary>
/// <param name="Level">Важность записи.</param>
/// <param name="Event">Стабильное название события.</param>
/// <param name="ConnectionId">Идентификатор соединения, созданный самим Host.</param>
/// <param name="Epochs">Проверенные идентификаторы текущего binding.</param>
/// <param name="InputSequence">Исходная последовательность команды.</param>
/// <param name="OutputSequence">Последовательность выдаваемого результата.</param>
/// <param name="RequestId">Канонический producer/serial.</param>
/// <param name="InvocationId">Идентификатор native invocation без его содержимого.</param>
/// <param name="Command">Название типизированной команды.</param>
/// <param name="Code">Краткий код результата без текста ошибки.</param>
/// <param name="State">Краткое имя состояния.</param>
/// <param name="Metrics">Опциональный агрегированный снимок, без raw JSON.</param>
public sealed record HostLogEvent(HostLogLevel Level, string Event, string? ConnectionId = null, Core.Epochs? Epochs = null,
    ulong? InputSequence = null, ulong? OutputSequence = null, string? RequestId = null,
    string? InvocationId = null, string? Command = null, string? Code = null, string? State = null, HostLogMetrics? Metrics = null);

/// <summary>Неблокирующий JSONL-журнал с отдельным I/O worker и ограниченной памятью.</summary>
public sealed class HostLog : IAsyncDisposable
{
    /// <summary>Очередь не запускает продолжения writer синхронно на игровом owner.</summary>
    private readonly Channel<QueuedEntry> _queue;
    /// <summary>stderr принадлежит вызывающему процессу и никогда не закрывается журналом.</summary>
    private TextWriter? _errors;
    /// <summary>Файл принадлежит worker; его отказ не отключает stderr.</summary>
    private StreamWriter? _file;
    /// <summary>Размер учитывает UTF-8 на диске; ротация выполняется только worker.</summary>
    private long _fileBytes;
    /// <summary>Проверенная конфигурация уровней и остановки.</summary>
    private readonly HostLogOptions _options;
    /// <summary>Отмена прекращает кооперативное ожидание sink после ограниченного flush.</summary>
    private readonly CancellationTokenSource _stop = new();
    /// <summary>Единственный worker выполняет сериализацию и последовательное I/O.</summary>
    private readonly Task _worker;
    /// <summary>Счётчики не удерживают содержимое потерянных записей.</summary>
    private long _dropped, _sinkFailures;
    /// <summary>Остановка идемпотентна; TryWrite после остановки не принимает записи.</summary>
    private int _disposed;
    /// <summary>Время снимается на producer, а не после возможной задержки устройства вывода.</summary>
    private sealed record QueuedEntry(DateTimeOffset Timestamp, HostLogEvent Entry);
    /// <summary>JSONL использует одинаковые имена и пропускает отсутствующие поля.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Создаёт файл до запуска Host; никакое file I/O не выполняется из TryWrite.</summary>
    public HostLog(TextWriter errors, HostLogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        _options = options ?? new();
        if (!Enum.IsDefined(_options.MinimumLevel) || _options.Capacity < 1 ||
            (_options.FlushTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))) ||
            (_options.TelemetryInterval is { } interval && interval <= TimeSpan.Zero) ||
            _options.MaximumFileBytes < 1 || _options.RetainedFiles is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options));
        _errors = errors;
        if (_options.FilePath is { } path)
        {
            _file = OpenFile(path);
            _fileBytes = _file.BaseStream.Length;
        }
        _queue = Channel.CreateBounded<QueuedEntry>(new BoundedChannelOptions(_options.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ConsumeAsync);
    }

    /// <summary>Общее число переполнений и записей без работающего sink; не сбрасывается после чтения.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);
    /// <summary>Число отключённых sink после I/O ошибки.</summary>
    public long SinkFailureCount => Interlocked.Read(ref _sinkFailures);
    /// <summary>Частота постоянных наблюдений; не зависит от согласования DM monitor.</summary>
    internal TimeSpan? TelemetryInterval => _options.TelemetryInterval;
    /// <summary>Позволяет горячему пути не создавать дорогие diagnostic ID при отключённом уровне.</summary>
    public bool IsEnabled(HostLogLevel level) => level >= _options.MinimumLevel && level < HostLogLevel.None &&
        Volatile.Read(ref _disposed) == 0;

    /// <summary>Только TryWrite; никогда не ожидает stderr, файл, место в очереди или завершение worker.</summary>
    public bool TryWrite(HostLogEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsEnabled(entry.Level)) return false;
        // Ограничиваем строки до помещения в очередь, чтобы она не удерживала произвольный большой input.
        var bounded = entry with
        {
            Event = Safe(entry.Event, 80)!,
            ConnectionId = Safe(entry.ConnectionId),
            Epochs = entry.Epochs is { } epochs ? new(Safe(epochs.World)!, Safe(epochs.Engine)!, Safe(epochs.Binding)!) : null,
            RequestId = Safe(entry.RequestId),
            InvocationId = Safe(entry.InvocationId),
            Command = Safe(entry.Command, 80),
            Code = Safe(entry.Code, 80),
            State = Safe(entry.State, 80),
            Metrics = entry.Metrics is { } metrics ? metrics with { SyncState = Safe(metrics.SyncState, 80)! } : null
        };
        if (_queue.Writer.TryWrite(new(DateTimeOffset.UtcNow, bounded))) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Ограниченный flush не удерживает закрытие игрового соединения за медленным sink.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(_options.FlushTimeout ?? TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        // При некооперативном TextWriter worker освобождает собственный файл после возврата I/O.
    }

    /// <summary>Сериализация и оба sink исполняются только на отдельном worker.</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                if (_errors is null && _file is null)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }
                var entry = queued.Entry;
                var line = JsonSerializer.Serialize(new
                {
                    timestamp = queued.Timestamp,
                    level = entry.Level.ToString().ToLowerInvariant(),
                    @event = Safe(entry.Event, 80),
                    process_id = Environment.ProcessId,
                    connection_id = Safe(entry.ConnectionId),
                    world_epoch = Safe(entry.Epochs?.World),
                    engine_epoch = Safe(entry.Epochs?.Engine),
                    binding_epoch = Safe(entry.Epochs?.Binding),
                    input_sequence = entry.InputSequence?.ToString(CultureInfo.InvariantCulture),
                    output_sequence = entry.OutputSequence?.ToString(CultureInfo.InvariantCulture),
                    request_id = Safe(entry.RequestId),
                    invocation_id = Safe(entry.InvocationId),
                    command = Safe(entry.Command, 80),
                    code = Safe(entry.Code, 80),
                    state = Safe(entry.State, 80),
                    metrics = entry.Metrics,
                    dropped_total = DroppedCount,
                    sink_failures = SinkFailureCount
                }, JsonOptions);
                var written = false;
                if (_errors is { } errors)
                {
                    if (await WriteAsync(errors, line)) written = true;
                    else _errors = null;
                }
                if (_file is { } file)
                {
                    try
                    {
                        var bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(file.NewLine);
                        if (_fileBytes > 0 && _fileBytes + bytes > _options.MaximumFileBytes)
                            file = await RotateAsync(file);
                        if (await WriteAsync(file, line)) { written = true; _fileBytes += bytes; }
                        else await CloseFileAsync();
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref _sinkFailures);
                        await CloseFileAsync();
                    }
                }
                if (!written) Interlocked.Increment(ref _dropped);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
            await CloseFileAsync();
            _stop.Dispose();
        }
    }

    /// <summary>Открывает только настроенный файл в append-режиме; дочерние каталоги создаёт deployment.</summary>
    private static StreamWriter OpenFile(string path) => new(new FileStream(path, FileMode.Append, FileAccess.Write,
        FileShare.Read, 4096, FileOptions.Asynchronous), new UTF8Encoding(false));

    /// <summary>Ограничивает постоянную историю активным файлом и известными numbered archives.</summary>
    private async Task<StreamWriter> RotateAsync(StreamWriter file)
    {
        await file.DisposeAsync();
        _file = null;
        var path = _options.FilePath!;
        for (var index = _options.RetainedFiles - 1; index >= 1; index--)
        {
            var source = path + "." + index.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(source)) File.Move(source, path + "." + (index + 1).ToString(CultureInfo.InvariantCulture), true);
        }
        File.Move(path, path + ".1", true);
        _file = OpenFile(path);
        _fileBytes = 0;
        return _file;
    }

    /// <summary>Отказ закрытия файла не выходит из диагностического worker.</summary>
    private async Task CloseFileAsync()
    {
        var file = _file;
        _file = null;
        if (file is null) return;
        try { await file.DisposeAsync(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
        { Interlocked.Increment(ref _sinkFailures); }
    }

    /// <summary>Ошибка устройства вывода не выходит в runtime; записи не содержат exception message.</summary>
    private async Task<bool> WriteAsync(TextWriter sink, string line)
    {
        try
        {
            await sink.WriteLineAsync(line.AsMemory(), _stop.Token);
            await sink.FlushAsync(_stop.Token);
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _sinkFailures);
            return false;
        }
    }

    /// <summary>Отбрасывает непроверенные слишком длинные ID; управляющие символы не проходят в журнал.</summary>
    private static string? Safe(string? value, int maximum = 128) => value is null ? null :
        value.Length <= maximum && value.All(character => character is >= '!' and <= '~') ? value : "invalid_log_field";
}
