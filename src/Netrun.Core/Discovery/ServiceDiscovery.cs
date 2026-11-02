using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Communication;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Discovery;

/// <summary>Последовательный bounded async поиск: fixed candidate keys, live generation checks и справедливые route slices.</summary>
public sealed class ServiceDiscovery : IDisposable
{
    /// <summary>Активный запрос хранит только собственные bounded keys, а не snapshot всего мира.</summary>
    private sealed class Query
    {
        /// <summary>Неизменяемая семантика запроса для replay и поздних фильтров.</summary>
        internal required DiscoveryRequest Request;
        /// <summary>Deterministic SHA-256 без directory/graph revision.</summary>
        internal required byte[] Fingerprint;
        /// <summary>Source identity при admission; owner/family/tag rebinding отзывает query.</summary>
        internal required SourceIdentity Source;
        /// <summary>Изначальная выборка; сервисы, созданные позднее, не подмешиваются.</summary>
        internal required ServiceCandidateSnapshot Snapshot;
        /// <summary>Следующий ещё не проверенный key.</summary>
        internal int Cursor;
        /// <summary>Число найденных результатов до явно заданного MaxResults.</summary>
        internal int Found;
        /// <summary>До PageSize найденных keys в ещё не опубликованной странице.</summary>
        internal readonly List<ServiceKey> Page = [];
        /// <summary>O(1) удаление из round-robin без накапливающихся stale queue entries.</summary>
        internal LinkedListNode<Query>? Schedule;
    }

    /// <summary>Минимальная захваченная identity source, не удерживающая большие списки service metadata.</summary>
    private sealed record SourceIdentity(NodeKey Owner, string Family, string? Tag);
    /// <summary>Output хранит только одну страницу и контекст live-проверки; Query/Snapshot сюда не попадает.</summary>
    private sealed record PendingPage(DiscoveryRequest Request, SourceIdentity Source, DiscoveryPage Page);
    /// <summary>Terminal replay history не удерживает candidate payload или world revisions.</summary>
    private sealed record History(byte[] Fingerprint, RequestState State, string Code);

    /// <summary>Актуальные опубликованные facts.</summary>
    private readonly INetworkWorldView _world;
    /// <summary>Отдельно обновляемый family/tag индекс владельца runtime.</summary>
    private readonly ServiceDirectory _directory;
    /// <summary>Null означает pending topology; такой запрос паркуется без negative cache или route spin.</summary>
    private readonly Func<GraphSnapshot?> _graph;
    /// <summary>Обычная сеть имеет приоритет перед privileged channel.</summary>
    private readonly IRouteProvider _routes;
    /// <summary>Опциональная allowlisted native communication capability.</summary>
    private readonly ICommunicationRouteProvider? _communications;
    /// <summary>Binding всех выходных страниц.</summary>
    private readonly string _binding;
    /// <summary>Проверенные абсолютные memory/work caps.</summary>
    private readonly DiscoveryOptions _options;
    /// <summary>Точные producer identities активных запросов.</summary>
    private readonly Dictionary<RequestKey, Query> _active = [];
    /// <summary>Незавершённые запросы fair scheduler; удаление не оставляет stale entries.</summary>
    private readonly LinkedList<Query> _schedule = [];
    /// <summary>Per exact source service quota без полного сканирования active queries.</summary>
    private readonly Dictionary<ServiceKey, int> _perSource = [];
    /// <summary>Страницы до передачи внешнему journal; pressure не теряет уже проверенный результат.</summary>
    private readonly LinkedList<PendingPage> _events = [];
    /// <summary>Keys завершённых query остаются учтены, пока их страницы удерживаются output queue.</summary>
    private readonly Dictionary<RequestKey, int> _queuedCandidates = [];
    /// <summary>Bounded terminal identity/status window.</summary>
    private readonly Dictionary<RequestKey, History> _history = [];
    /// <summary>FIFO eviction terminal history; producer watermark никогда не вытесняется.</summary>
    private readonly Queue<RequestKey> _historyOrder = [];
    /// <summary>Максимальный принятый serial каждого producer за binding.</summary>
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    /// <summary>Общее число retained key objects, включая output pages завершённых запросов.</summary>
    private int _retained;
    /// <summary>Защита последовательного владельца от повторного входа через внешний route provider.</summary>
    private bool _busy;
    /// <summary>Освобождённый binding не принимает новые запросы.</summary>
    private bool _disposed;

    /// <summary>Число активных query slots.</summary>
    public int ActiveCount => _active.Count;
    /// <summary>Число страниц, ожидающих потребителя.</summary>
    public int EventCount => _events.Count;
    /// <summary>Сумма pinned candidate keys и ещё удерживаемых keys завершённых страниц.</summary>
    public int RetainedCandidateCount => _retained;
    /// <summary>Число bounded terminal replay records.</summary>
    public int HistoryCount => _history.Count;
    /// <summary>Число не вытесняемых producer barriers за binding.</summary>
    public int ProducerCount => _watermarks.Count;

    /// <summary>Получает typed dependencies; вызовы выполняются одним владельцем без I/O, tasks и фоновых потоков.</summary>
    public ServiceDiscovery(INetworkWorldView world, ServiceDirectory directory, Func<GraphSnapshot?> graph,
        IRouteProvider routes, string bindingEpoch, DiscoveryOptions? options = null,
        ICommunicationRouteProvider? communications = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(routes);
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        _options = options ?? new();
        if (_options.MaxQueries is < 1 or > 256 || _options.MaxQueriesPerSource is < 1 or > 64 ||
            _options.MaxCandidates is < 1 or > 4096 || _options.MaxRetainedCandidates is < 1 or > 50_000 ||
            _options.MaxEvents is < 1 or > 256 || _options.PageSize is < 1 or > 64 ||
            _options.ChecksPerSlice is < 1 or > 8 || _options.HistoryCapacity is < 1 or > 4096 ||
            _options.MaxProducers is < 1 or > 128)
            throw new ArgumentException("Invalid discovery options.", nameof(options));
        _world = world;
        _directory = directory;
        _graph = graph;
        _routes = routes;
        _binding = bindingEpoch;
        _communications = communications;
    }

    /// <summary>Резервирует candidate/query slots без inline routing и без расходования serial при would_block.</summary>
    public TransportAdmission Submit(DiscoveryRequest request)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!IsValidRequest(request)) return new(AdmissionStatus.Rejected, "invalid_request", request.Key);
            var fingerprint = Fingerprint(request);
            if (_active.TryGetValue(request.Key, out var active))
                return Same(active.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, "duplicate", request.Key, RequestState.Queued) :
                    new(AdmissionStatus.Rejected, "request_conflict", request.Key);
            if (_history.TryGetValue(request.Key, out var history))
                return Same(history.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, history.Code, request.Key, history.State) :
                    new(AdmissionStatus.Rejected, "request_conflict", request.Key);
            if (_watermarks.TryGetValue(request.Key.ProducerId, out var barrier) && request.Key.Sequence <= barrier)
                return new(AdmissionStatus.Rejected, "request_expired", request.Key);
            if (!_watermarks.ContainsKey(request.Key.ProducerId) && _watermarks.Count == _options.MaxProducers)
                return new(AdmissionStatus.WouldBlock, "producer_capacity", request.Key);
            if (!Source(request.Source, out var source)) return new(AdmissionStatus.Rejected, "source_unavailable", request.Key);
            _perSource.TryGetValue(request.Source, out var sourceCount);
            if (_active.Count == _options.MaxQueries) return new(AdmissionStatus.WouldBlock, "query_capacity", request.Key);
            if (sourceCount == _options.MaxQueriesPerSource) return new(AdmissionStatus.WouldBlock, "source_capacity", request.Key);
            if (_directory.CountCandidates(request.DestinationFamily, request.RecipientTag, _options.MaxCandidates) > _options.MaxRetainedCandidates - _retained)
                return new(AdmissionStatus.WouldBlock, "candidate_capacity", request.Key);
            var snapshot = _directory.Capture(request.DestinationFamily, request.RecipientTag, _options.MaxCandidates);
            var query = new Query { Request = request, Fingerprint = fingerprint, Source = source!, Snapshot = snapshot };
            query.Schedule = _schedule.AddLast(query);
            _active.Add(request.Key, query);
            _perSource[request.Source] = sourceCount + 1;
            _retained += snapshot.Keys.Count;
            _watermarks[request.Key.ProducerId] = request.Key.Sequence;
            return new(AdmissionStatus.Accepted, "accepted", request.Key, RequestState.Queued);
        }
        finally { _busy = false; }
    }

    /// <summary>Расходует максимум workBudget candidate checks; query уступает очередь не позднее ChecksPerSlice.</summary>
    public int Advance(int workBudget)
    {
        Enter();
        try
        {
            if (workBudget <= 0 || _events.Count == _options.MaxEvents || _schedule.Count == 0) return 0;
            var graph = _graph();
            if (graph is null) return CheckParkedSources(workBudget);
            var work = 0;
            while (work < workBudget && _events.Count < _options.MaxEvents && _schedule.First is { } first)
            {
                var query = first.Value;
                _schedule.Remove(first);
                _schedule.AddLast(first);
                if (!LiveSource(query.Request, query.Source))
                {
                    RemovePages(query.Request.Key);
                    Finish(query, "source_unavailable", RequestState.Failed);
                    work++;
                    continue;
                }
                var slice = Math.Min(_options.ChecksPerSlice, workBudget - work);
                for (var check = 0; check < slice && _active.ContainsKey(query.Request.Key); check++)
                {
                    if (_events.Count == _options.MaxEvents) break;
                    if (query.Cursor >= query.Snapshot.Keys.Count || query.Found >= query.Request.MaxResults)
                    {
                        Finish(query, CompletionCode(query), RequestState.Completed);
                        work++;
                        break;
                    }
                    var candidate = query.Snapshot.Keys[query.Cursor];
                    bool found;
                    try { found = Eligible(query.Request, candidate, out var service) && Reachable(query.Request, query.Source.Owner, service!, graph); }
                    catch (RoutingException) { Finish(query, "routing_unavailable", RequestState.Failed); work++; break; }
                    catch (CommunicationException) { Finish(query, "routing_unavailable", RequestState.Failed); work++; break; }
                    query.Cursor++;
                    work++;
                    if (found) { query.Page.Add(candidate); query.Found++; }
                    if (query.Cursor == query.Snapshot.Keys.Count || query.Found == query.Request.MaxResults)
                        Finish(query, CompletionCode(query), RequestState.Completed);
                    else if (query.Page.Count == _options.PageSize) Publish(query, false, "page");
                }
            }
            return work;
        }
        finally { _busy = false; }
    }

    /// <summary>Перед handoff повторяет live identity checks; pending graph удерживает candidate pages, но не пустые terminal отказы.</summary>
    public bool TryDequeueEvent(out DiscoveryPage? page)
    {
        Enter();
        try
        {
            var remaining = _events.Count;
            while (remaining-- > 0 && _events.First is { } first)
            {
                var pending = first.Value;
                if (!LiveSource(pending.Request, pending.Source) && pending.Page.Code is "page" or "completed" or "truncated")
                {
                    RemovePages(pending.Request.Key);
                    if (_active.TryGetValue(pending.Request.Key, out var query))
                        Retire(query, "source_unavailable", RequestState.Failed);
                    else if (_history.TryGetValue(pending.Request.Key, out var old))
                        _history[pending.Request.Key] = old with { State = RequestState.Failed, Code = "source_unavailable" };
                    page = new(_binding, pending.Request.Key, [], true, "source_unavailable");
                    return true;
                }
                if (pending.Page.Candidates.Count > 0 && _graph() is null)
                {
                    _events.Remove(first);
                    _events.AddLast(first);
                    continue;
                }
                RemovePage(first);
                var candidates = pending.Page.Candidates.Where(key => Eligible(pending.Request, key, out _)).ToArray();
                if (candidates.Length == 0 && !pending.Page.Complete) continue;
                page = new(_binding, pending.Request.Key, candidates, pending.Page.Complete, pending.Page.Code);
                return true;
            }
            page = null;
            return false;
        }
        finally { _busy = false; }
    }

    /// <summary>Отменяет ещё активный read-only query; output pressure оставляет состояние полностью неизменным.</summary>
    public TransportActionResult Cancel(RequestKey key)
    {
        Enter();
        try
        {
            if (!TransportFingerprint.ValidKey(key)) return new(false, "invalid_key");
            if (!_active.TryGetValue(key, out var query)) return new(false, "request_unknown");
            if (_events.Count == _options.MaxEvents && !_events.Any(entry => entry.Request.Key == key))
                return new(false, "would_block", true);
            RemovePages(key);
            query.Page.Clear();
            Finish(query, "cancelled", RequestState.Cancelled);
            return new(true, "cancelled");
        }
        finally { _busy = false; }
    }

    /// <summary>Освобождает все retained snapshots, pages и replay metadata этого binding.</summary>
    public void Dispose()
    {
        if (_busy) throw new InvalidOperationException("Discovery is already executing.");
        if (_disposed) return;
        _disposed = true;
        _active.Clear();
        _schedule.Clear();
        _perSource.Clear();
        _events.Clear();
        _queuedCandidates.Clear();
        _history.Clear();
        _historyOrder.Clear();
        _watermarks.Clear();
        _retained = 0;
    }

    /// <summary>Source должен оставаться тем же экземпляром owner/family/tag и быть native-online.</summary>
    private bool Source(ServiceKey key, out SourceIdentity? source)
    {
        source = null;
        if (!_world.Services.TryGetValue(key.ServiceId, out var service) || service.Generation != key.Generation ||
            !service.Online || !Online(service.Owner)) return false;
        source = new(service.Owner, service.Family, service.Tag);
        return true;
    }

    /// <summary>Не допускает переадресацию уже принятого запроса на rebinding source.</summary>
    private bool LiveSource(DiscoveryRequest request, SourceIdentity source) =>
        _world.Services.TryGetValue(request.Source.ServiceId, out var service) && service.Generation == request.Source.Generation &&
        service.Online && service.Owner == source.Owner && service.Family == source.Family && service.Tag == source.Tag && Online(service.Owner);

    /// <summary>Проверяет только объявленные network facts; native accepts/matches/select_targets остаются у caller.</summary>
    private bool Eligible(DiscoveryRequest request, ServiceKey key, out ServiceFact? service)
    {
        if (_world.Services.TryGetValue(key.ServiceId, out service) && service.Generation == key.Generation &&
            service.Family == request.DestinationFamily && (request.RecipientTag is null || service.Tag == request.RecipientTag) &&
            service.Online && Online(service.Owner)) return true;
        service = null;
        return false;
    }

    /// <summary>Факт существования exact physical generation отделён от объявленного online сервиса.</summary>
    private bool Online(NodeKey key) => _world.Nodes.TryGetValue(key.NodeId, out var node) && node.Generation == key.Generation && node.State.Online;

    /// <summary>Сначала обычный путь; privileged fallback вызывается лишь при явном opt-in и проверяет свой family/frequency allowlist.</summary>
    private bool Reachable(DiscoveryRequest request, NodeKey source, ServiceFact destination, GraphSnapshot graph)
    {
        var mode = request.Communications ? RouteKind.Machine : RouteKind.Discovery;
        var reachable = _routes is IRouteReachabilityProvider reachability
            ? reachability.IsReachable(graph, source, destination.Owner, mode)
            : _routes.FindRoute(graph, source, destination.Owner, mode).Status == RouteStatus.Found;
        if (reachable)
            return true;
        return request.Communications && _communications is not null && _communications.FindRoute(graph,
            request.Source, new(destination.ServiceId, destination.Generation), request.Frequency, _world).Status == RouteStatus.Found;
    }

    /// <summary>При pending topology один bounded проход отзывает мёртвые sources без route поиска или повторного spin по parked queries.</summary>
    private int CheckParkedSources(int workBudget)
    {
        var work = 0;
        var remaining = Math.Min(_schedule.Count, workBudget);
        while (remaining-- > 0 && _events.Count < _options.MaxEvents && _schedule.First is { } first)
        {
            _schedule.Remove(first);
            _schedule.AddLast(first);
            var query = first.Value;
            if (LiveSource(query.Request, query.Source)) continue;
            RemovePages(query.Request.Key);
            Finish(query, "source_unavailable", RequestState.Failed);
            work++;
        }
        return work;
    }

    /// <summary>Публикует страницу только в зарезервированный output slot, фильтруя facts, изменившиеся между slices.</summary>
    private void Publish(Query query, bool complete, string code)
    {
        var keys = query.Page.Where(key => Eligible(query.Request, key, out _)).ToArray();
        query.Page.Clear();
        if (keys.Length == 0 && !complete) return;
        var page = new DiscoveryPage(_binding, query.Request.Key, keys, complete, code);
        _events.AddLast(new PendingPage(query.Request, query.Source, page));
        if (keys.Length > 0)
        {
            _queuedCandidates.TryGetValue(query.Request.Key, out var queued);
            _queuedCandidates[query.Request.Key] = queued + keys.Length;
        }
    }

    /// <summary>Заканчивает query с одной terminal страницей; error страницы не содержат ранее собранные candidates.</summary>
    private void Finish(Query query, string code, RequestState state)
    {
        if (state != RequestState.Completed) query.Page.Clear();
        Publish(query, true, code);
        Retire(query, code, state);
    }

    /// <summary>Удаляет все scheduler/source/candidate slots; terminal history хранит только fingerprint и статус.</summary>
    private void Retire(Query query, string code, RequestState state)
    {
        _active.Remove(query.Request.Key);
        if (query.Schedule is { } node) _schedule.Remove(node);
        query.Schedule = null;
        var sourceCount = _perSource[query.Request.Source] - 1;
        if (sourceCount == 0) _perSource.Remove(query.Request.Source);
        else _perSource[query.Request.Source] = sourceCount;
        _retained -= query.Snapshot.Keys.Count;
        if (_queuedCandidates.TryGetValue(query.Request.Key, out var queued)) _retained += queued;
        _history.Add(query.Request.Key, new(query.Fingerprint, state, code));
        _historyOrder.Enqueue(query.Request.Key);
        while (_historyOrder.Count > _options.HistoryCapacity) _history.Remove(_historyOrder.Dequeue());
    }

    /// <summary>Удаляет одну страницу с согласованным учётом keys уже завершённого query.</summary>
    private void RemovePage(LinkedListNode<PendingPage> node)
    {
        var key = node.Value.Request.Key;
        var count = node.Value.Page.Candidates.Count;
        _events.Remove(node);
        if (count == 0) return;
        if (!_active.ContainsKey(key)) _retained -= count;
        var queued = _queuedCandidates[key] - count;
        if (queued == 0) _queuedCandidates.Remove(key);
        else _queuedCandidates[key] = queued;
    }

    /// <summary>Bounded scan output queue отзывает только страницы данного query.</summary>
    private void RemovePages(RequestKey key)
    {
        var node = _events.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.Request.Key == key) RemovePage(node);
            node = next;
        }
    }

    /// <summary>Явно сообщает об ограничении candidate bucket или количестве результатов вместо тихого first-target отсечения.</summary>
    private static string CompletionCode(Query query) => query.Snapshot.Truncated || query.Cursor < query.Snapshot.Keys.Count ? "truncated" : "completed";

    /// <summary>Проверяет bounded wire/domain metadata до любой мутации admission.</summary>
    internal static bool IsValidRequest(DiscoveryRequest? request) => request is not null && TransportFingerprint.ValidKey(request.Key) && request.Source is { Generation: > 0 } &&
        NodeFactRules.IsValidText(request.Source.ServiceId, 128) && NodeFactRules.IsValidText(request.DestinationFamily, 128) &&
        (request.RecipientTag is null || NodeFactRules.IsValidText(request.RecipientTag, 256)) && request.MaxResults is >= 1 and <= 4096 &&
        request.Frequency is not <= 0 && (request.Communications || request.Frequency is null);

    /// <summary>Fingerprint зависит только от полного semantic запроса; churn мира не меняет dedup identity.</summary>
    private static byte[] Fingerprint(DiscoveryRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-DISCOVERY-1");
            writer.Write(request.Key.ProducerId);
            writer.Write(request.Key.Sequence);
            writer.Write(request.Source.ServiceId);
            writer.Write(request.Source.Generation);
            writer.Write(request.DestinationFamily);
            writer.Write(request.RecipientTag is not null);
            if (request.RecipientTag is { } tag) writer.Write(tag);
            writer.Write(request.Communications);
            writer.Write(request.Frequency.HasValue);
            if (request.Frequency is { } frequency) writer.Write(frequency);
            writer.Write(request.MaxResults);
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    /// <summary>Сравнивает фиксированный размер SHA-256 без process-local hash identity.</summary>
    private static bool Same(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>Каждая публичная мутация принадлежит одному последовательному владельцу.</summary>
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy) throw new InvalidOperationException("Discovery is already executing.");
        _busy = true;
    }
}
