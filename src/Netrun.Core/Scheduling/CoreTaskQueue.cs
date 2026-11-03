using System.Security.Cryptography;
using Netrun.Core.Transport;

namespace Netrun.Core.Scheduling;

/// <summary>Итог одного cooperative шага: незавершённый head сохраняет своё место FIFO.</summary>
public sealed record CoreTaskStep(int Work, bool Complete, string Code = "completed", bool Success = true);

/// <summary>Terminal outcome не удерживает тело задачи, plugin object, native контекст или исключение.</summary>
public sealed record CoreTaskCompleted(string BindingEpoch, RequestKey Key, ulong TaskSequence,
    bool Success, string Code) : TransportEvent(BindingEpoch, Key);

/// <summary>Admission и output capacity настраиваются независимо; FIFO никогда не вытесняет уже принятую задачу.</summary>
public sealed record CoreTaskQueueOptions(int MaximumTasks = int.MaxValue, int MaximumEvents = int.MaxValue,
    int MaximumProducers = int.MaxValue, int HistoryCapacity = 256);

/// <summary>
/// Общая FIFO очередь NETRUN. Вход возвращается после enqueue; работа выполняется отдельным Pump ядра.
/// Незавершённое вычисление продолжает head. Ожидание native ответа завершает текущую задачу;
/// ответ создаёт новую задачу в хвосте, не удерживая worker или BYOND поток.
/// </summary>
public sealed class CoreTaskQueue<T> : IDisposable where T : class
{
    /// <summary>Единственное принятое тело живёт только до окончания задачи.</summary>
    private sealed record Entry(RequestKey Key, ulong Sequence, byte[] Fingerprint, T Body);
    /// <summary>Replay хранит только fingerprint и immutable admission.</summary>
    private sealed record History(byte[] Fingerprint, TransportAdmission Admission);

    private readonly string _binding;
    private readonly CoreTaskQueueOptions _options;
    private readonly Func<T, int, CoreTaskStep> _execute;
    private readonly Queue<Entry> _queue = [];
    private readonly Queue<CoreTaskCompleted> _events = [];
    private readonly Dictionary<RequestKey, Entry> _active = [];
    private readonly Dictionary<RequestKey, History> _history = [];
    private readonly Queue<RequestKey> _historyOrder = [];
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    private readonly ProducerAdmissionFence _fence;
    private ulong _sequence;
    private bool _busy;
    private bool _disposed;
    private bool _faulted;

    /// <summary>Число принятых незавершённых задач, включая текущий cooperative head.</summary>
    public int Count => _queue.Count;
    /// <summary>Непрочитанные terminal outcomes.</summary>
    public int EventCount => _events.Count;
    /// <summary>Монотонный порядок последней принятой задачи, независимо от producer.</summary>
    public ulong EnqueuedSequence => _sequence;
    /// <summary>Порядок последней начатой задачи; новый head не обгоняет незавершённый.</summary>
    public ulong StartedSequence { get; private set; }
    /// <summary>Порядок последней завершённой задачи.</summary>
    public ulong CompletedSequence { get; private set; }

    /// <summary>Создаёт очередь одного binding без потоков, таймеров, приоритетов и I/O.</summary>
    public CoreTaskQueue(string bindingEpoch, Func<T, int, CoreTaskStep> execute, CoreTaskQueueOptions? options = null)
    {
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        ArgumentNullException.ThrowIfNull(execute);
        _options = options ?? new();
        if (_options.MaximumTasks < 1 || _options.MaximumEvents < 1 || _options.MaximumProducers < 1 ||
            _options.HistoryCapacity is < 1 or > 4096) throw new ArgumentException("Invalid queue options.", nameof(options));
        _binding = bindingEpoch;
        _execute = execute;
        _fence = new(_watermarks, _options.MaximumProducers);
    }

    /// <summary>Принимает immutable тело и SHA-256 полной semantic identity; вычислений задачи здесь нет.</summary>
    public TransportAdmission Enqueue(RequestKey key, T body, ReadOnlySpan<byte> fingerprint)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(body);
            if (!TransportFingerprint.ValidKey(key) || fingerprint.Length != 32) return new(AdmissionStatus.Rejected, "invalid_task", key);
            if (_active.TryGetValue(key, out var active)) return Same(active.Fingerprint, fingerprint)
                ? new(AdmissionStatus.Duplicate, "queued", key, RequestState.Queued)
                : new(AdmissionStatus.Rejected, "request_conflict", key);
            if (_history.TryGetValue(key, out var history)) return Same(history.Fingerprint, fingerprint)
                ? history.Admission with { Status = AdmissionStatus.Duplicate }
                : new(AdmissionStatus.Rejected, "request_conflict", key);
            if (_watermarks.TryGetValue(key.ProducerId, out var watermark) && key.Sequence <= watermark)
                return new(AdmissionStatus.Rejected, "request_expired", key);
            if (_fence.Blocks(key)) return new(AdmissionStatus.WouldBlock, "producer_backpressure", key);
            if (!_watermarks.ContainsKey(key.ProducerId) && _watermarks.Count >= _options.MaximumProducers)
                return new(AdmissionStatus.WouldBlock, "producer_capacity", key);
            if (_queue.Count >= _options.MaximumTasks || _sequence == ulong.MaxValue)
                return _fence.Complete(new(AdmissionStatus.WouldBlock, "task_capacity", key));
            var entry = new Entry(key, ++_sequence, fingerprint.ToArray(), body);
            _queue.Enqueue(entry);
            _active.Add(key, entry);
            _watermarks[key.ProducerId] = key.Sequence;
            return _fence.Complete(new(AdmissionStatus.Accepted, "queued", key, RequestState.Queued));
        }
        finally { _busy = false; }
    }

    /// <summary>Выполняет head строго до completion или исчерпания budget; нулевой прогресс не создаёт busy loop.</summary>
    public int Advance(int workBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workBudget);
        Enter();
        try
        {
            var work = 0;
            while (work < workBudget && _events.Count < _options.MaximumEvents && _queue.TryPeek(out var head))
            {
                StartedSequence = head.Sequence;
                var step = _execute(head.Body, workBudget - work);
                if (step.Work < 0 || step.Work > workBudget - work ||
                    !TransportFingerprint.ValidToken(step.Code, 64)) throw new InvalidOperationException("Invalid core task step.");
                work += step.Work;
                if (step.Complete)
                {
                    _queue.Dequeue();
                    _active.Remove(head.Key);
                    CompletedSequence = head.Sequence;
                    var completed = new CoreTaskCompleted(_binding, head.Key, head.Sequence, step.Success, step.Code);
                    _events.Enqueue(completed);
                    var admission = new TransportAdmission(AdmissionStatus.Accepted, step.Code, head.Key,
                        step.Success ? RequestState.Completed : RequestState.Failed);
                    _history.Add(head.Key, new(head.Fingerprint, admission));
                    _historyOrder.Enqueue(head.Key);
                    if (_historyOrder.Count > _options.HistoryCapacity) _history.Remove(_historyOrder.Dequeue());
                    // Даже готовая без вычислений операция расходует один dispatch unit.
                    if (step.Work == 0) work++;
                }
                else if (step.Work == 0) break;
            }
            return work;
        }
        catch
        {
            // Частично выполненный foreign/module task нельзя автоматически запускать второй раз.
            _faulted = true;
            throw;
        }
        finally { _busy = false; }
    }

    /// <summary>Outcome handoff освобождает output pressure, не изменяя порядок pending tasks.</summary>
    public bool TryDequeueEvent(out CoreTaskCompleted? result)
    {
        Enter();
        try { return _events.TryDequeue(out result); }
        finally { _busy = false; }
    }

    /// <summary>Disconnect освобождает тела; ранее выданные native эффекты в новую очередь не воспроизводятся.</summary>
    public void Dispose()
    {
        if (_busy) throw new InvalidOperationException("Task queue is already executing.");
        _disposed = true;
        _queue.Clear(); _events.Clear(); _active.Clear(); _history.Clear(); _historyOrder.Clear(); _watermarks.Clear(); _fence.Clear();
    }

    private static bool Same(byte[] first, ReadOnlySpan<byte> second) => CryptographicOperations.FixedTimeEquals(first, second);
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new InvalidOperationException("Task queue requires a new binding after a task fault.");
        if (_busy) throw new InvalidOperationException("Task queue is already executing.");
        _busy = true;
    }
}
