using Netrun.Core.Transport;

namespace Netrun.Core.Discovery;

/// <summary>Long-lived, binding-scoped observation of service advertisements, not a route or authorization grant.</summary>
public sealed record ServiceWatchRequest(RequestKey Key, ServiceKey Source, string Family, string? Tag = null);

/// <summary>Native-readable observation. Service is null for snapshot_end and terminal control events.</summary>
public sealed record ServiceWatchEvent(string BindingEpoch, RequestKey Key, string Change, string Family,
    string? Tag, ServiceKey? Service, bool Online, ulong DirectoryRevision) : TransportEvent(BindingEpoch, Key);

/// <summary>Independent limits for persistent watches, retained notifications and initial candidate capture.</summary>
public sealed record ServiceWatchOptions(int MaxSubscriptions = 128, int MaxEvents = 4096,
    int MaxInitialCandidates = 4096, int HistoryCapacity = 4096, int MaxProducers = 128);

/// <summary>
/// Single-owner indexed service watch. Facts only generate data; callbacks never execute inside Core.
/// A bounded overflow terminates the affected watch with resync_required instead of losing changes.
/// </summary>
public sealed class ServiceWatchBroker : IDisposable
{
    private sealed class Watch
    {
        internal required ServiceWatchRequest Request;
        internal required NodeKey Owner;
        internal required string SourceFamily;
        internal required string? SourceTag;
        internal bool Overflowed;
    }

    private readonly record struct Notification(RequestKey WatchId, ServiceWatchEvent Event);
    private readonly INetworkWorldView _world;
    private readonly ServiceDirectory _directory;
    private readonly string _binding;
    private readonly ServiceWatchOptions _options;
    private readonly Dictionary<RequestKey, Watch> _active = [];
    private readonly Dictionary<string, HashSet<RequestKey>> _byFamily = new(StringComparer.Ordinal);
    private readonly Queue<Notification> _events = [];
    private readonly Queue<RequestKey> _overflow = [];
    private readonly Dictionary<RequestKey, ServiceWatchRequest> _history = [];
    private readonly Queue<RequestKey> _historyOrder = [];
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    private bool _disposed;

    public int ActiveCount => _active.Count;
    public int EventCount => _events.Count + _overflow.Count;

    public ServiceWatchBroker(INetworkWorldView world, ServiceDirectory directory, string bindingEpoch,
        ServiceWatchOptions? options = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        if (!NodeFactRules.IsValidText(bindingEpoch, 128))
            throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        _binding = bindingEpoch;
        _options = options ?? new();
        if (_options.MaxSubscriptions < 1 || _options.MaxEvents < 1 || _options.MaxInitialCandidates < 1 ||
            _options.HistoryCapacity < 1 || _options.MaxProducers < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public static bool IsValidRequest(ServiceWatchRequest? request) =>
        request is not null && TransportFingerprint.ValidKey(request.Key) &&
        request.Source is { Generation: > 0 } && NodeFactRules.IsValidText(request.Source.ServiceId, 128) &&
        NodeFactRules.IsValidText(request.Family, 128) &&
        (request.Tag is null || NodeFactRules.IsValidText(request.Tag, 256));

    public TransportAdmission Subscribe(ServiceWatchRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidRequest(request)) return new(AdmissionStatus.Rejected, "invalid_watch", request.Key);
        if (_active.TryGetValue(request.Key, out var current))
            return current.Request == request
                ? new(AdmissionStatus.Duplicate, "duplicate", request.Key, RequestState.Queued)
                : new(AdmissionStatus.Rejected, "watch_conflict", request.Key);
        if (_history.TryGetValue(request.Key, out var completed))
            return completed == request
                ? new(AdmissionStatus.Duplicate, "cancelled", request.Key, RequestState.Cancelled)
                : new(AdmissionStatus.Rejected, "watch_conflict", request.Key);
        if (_watermarks.TryGetValue(request.Key.ProducerId, out var prior) && request.Key.Sequence <= prior)
            return new(AdmissionStatus.Rejected, "watch_expired", request.Key);
        if (_active.Count >= _options.MaxSubscriptions)
            return new(AdmissionStatus.WouldBlock, "watch_capacity", request.Key);
        if (!_watermarks.ContainsKey(request.Key.ProducerId) && _watermarks.Count >= _options.MaxProducers)
            return new(AdmissionStatus.WouldBlock, "producer_capacity", request.Key);
        if (!Source(request.Source, out var source))
            return new(AdmissionStatus.Rejected, "source_unavailable", request.Key);
        var snapshot = _directory.Capture(request.Family, request.Tag, _options.MaxInitialCandidates);
        if (snapshot.Truncated)
            return new(AdmissionStatus.WouldBlock, "watch_snapshot_capacity", request.Key);
        if (snapshot.Keys.Count + 1 > _options.MaxEvents - _events.Count)
            return new(AdmissionStatus.WouldBlock, "watch_event_capacity", request.Key);

        var watch = new Watch
        {
            Request = request,
            Owner = source!.Owner,
            SourceFamily = source.Family,
            SourceTag = source.Tag
        };
        _active.Add(request.Key, watch);
        if (!_byFamily.TryGetValue(request.Family, out var watchers))
            _byFamily.Add(request.Family, watchers = []);
        watchers.Add(request.Key);
        _watermarks[request.Key.ProducerId] = request.Key.Sequence;
        foreach (var key in snapshot.Keys)
        {
            if (_world.Services.TryGetValue(key.ServiceId, out var service) && service.Generation == key.Generation)
                Queue(watch, "added", service);
        }
        Queue(watch, "snapshot_end", null);
        return new(AdmissionStatus.Accepted, "accepted", request.Key, RequestState.Queued);
    }

    public TransportActionResult Unsubscribe(RequestKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TransportFingerprint.ValidKey(key)) return new(false, "invalid_key");
        if (_active.Remove(key, out var watch))
        {
            if (_byFamily.TryGetValue(watch.Request.Family, out var watchers))
            {
                watchers.Remove(key);
                if (watchers.Count == 0) _byFamily.Remove(watch.Request.Family);
            }
            _history[key] = watch.Request;
            _historyOrder.Enqueue(key);
            if (_historyOrder.Count > _options.HistoryCapacity)
                _history.Remove(_historyOrder.Dequeue());
            return new(true, "cancelled");
        }
        return new(_history.ContainsKey(key), _history.ContainsKey(key) ? "duplicate" : "watch_unknown");
    }

    /// <summary>Called only after accepted atomic mirror changes and live directory indexing.</summary>
    public void Observe(ServiceFact? before, ServiceFact? after)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (before == after) return;
        if (before is not null && (after is null || before.Generation != after.Generation ||
            before.Family != after.Family || before.Tag != after.Tag))
            Notify(before, "removed");
        if (after is not null)
            Notify(after, before is null || before.Generation != after.Generation ||
                before.Family != after.Family || before.Tag != after.Tag ? "added" : "updated");
    }

    public bool TryDequeueEvent(out ServiceWatchEvent? result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_overflow.TryDequeue(out var key))
        {
            if (!_active.TryGetValue(key, out var watch) || !watch.Overflowed) continue;
            result = Event(watch, "resync_required", null);
            Unsubscribe(key);
            return true;
        }
        while (_events.TryDequeue(out var notification))
        {
            if (!_active.TryGetValue(notification.WatchId, out var watch) || watch.Overflowed)
                continue;
            if (!SourceCurrent(watch))
            {
                result = Event(watch, "source_unavailable", null);
                Unsubscribe(watch.Request.Key);
                return true;
            }
            // A superseded or removed advertisement cannot be replayed as a live service.
            if (notification.Event.Change is "added" or "updated" && notification.Event.Service is { } serviceKey &&
                (!_world.Services.TryGetValue(serviceKey.ServiceId, out var live) ||
                live.Generation != serviceKey.Generation || live.Family != notification.Event.Family ||
                live.Tag != notification.Event.Tag))
                continue;
            result = notification.Event;
            return true;
        }
        result = null;
        return false;
    }

    private bool Source(ServiceKey key, out ServiceFact? source)
    {
        source = null;
        if (!_world.Services.TryGetValue(key.ServiceId, out var fact) || fact.Generation != key.Generation ||
            !fact.Online || !_world.Nodes.TryGetValue(fact.Owner.NodeId, out var node) ||
            node.Generation != fact.Owner.Generation || !node.State.Online)
            return false;
        source = fact;
        return true;
    }

    private bool SourceCurrent(Watch watch) =>
        Source(watch.Request.Source, out var source) && source!.Owner == watch.Owner &&
        source.Family == watch.SourceFamily && source.Tag == watch.SourceTag;

    private ServiceWatchEvent Event(Watch watch, string change, ServiceFact? service) =>
        new(_binding, watch.Request.Key, change, watch.Request.Family, service?.Tag ?? watch.Request.Tag,
            service is null ? null : new(service.ServiceId, service.Generation), service?.Online ?? false, _directory.Revision);

    private void Notify(ServiceFact service, string change)
    {
        if (!_byFamily.TryGetValue(service.Family, out var targets)) return;
        foreach (var key in targets)
        {
            var watch = _active[key];
            if (watch.Request.Tag is null || watch.Request.Tag == service.Tag)
                Queue(watch, change, service);
        }
    }

    private void Queue(Watch watch, string change, ServiceFact? service)
    {
        if (watch.Overflowed) return;
        if (_events.Count >= _options.MaxEvents)
        {
            watch.Overflowed = true;
            _overflow.Enqueue(watch.Request.Key);
            return;
        }
        _events.Enqueue(new(watch.Request.Key, Event(watch, change, service)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active.Clear();
        _byFamily.Clear();
        _events.Clear();
        _overflow.Clear();
        _history.Clear();
        _historyOrder.Clear();
        _watermarks.Clear();
    }
}
