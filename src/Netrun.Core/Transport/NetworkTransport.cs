using System.Security.Cryptography;
using Netrun.Core.Communication;
using Netrun.Core.Routing;
using Netrun.Core.Topology;

namespace Netrun.Core.Transport;

/// <summary>Single-owner bounded transport; только выдаёт invocation, а авторизацию и эффекты исполняет DM.</summary>
public sealed partial class NetworkTransport
{
    /// <summary>Снимок coalescing-контекста включает оба поколения сервисов, source address и отзываемый tag.</summary>
    private sealed record MergeKey(ServiceKey Source, string SourceAddress, ServiceKey Destination, string? Tag,
        string Operation, string Key, TrafficClass Traffic, int? Frequency);
    /// <summary>Внутренний request владеет только immutable facts/payload и одним индексируемым таймером.</summary>
    private sealed class PendingRequest
    {
        /// <summary>Последний объединённый body с исходным ключом и deadline.</summary>
        internal required NetworkRequest Request;
        /// <summary>Первоначальная content identity ключа не меняется при объединении других ключей.</summary>
        internal required byte[] Fingerprint;
        /// <summary>Захваченные поколения endpoints и операции защищают заменённые компоненты.</summary>
        internal required ServiceFact Source;
        /// <summary>Первоначальное объявление получателя; tag и owner проверяются вновь перед invocation.</summary>
        internal required ServiceFact Destination;
        /// <summary>Захваченный контракт исполнения, включая ordering и response semantics.</summary>
        internal required OperationContract Contract;
        /// <summary>Первоначальный due в децисекундах не переносится очередным coalescing.</summary>
        internal required double Due;
        /// <summary>Устойчивый порядок admission среди одинаковых due.</summary>
        internal required ulong Serial;
        /// <summary>Текущее состояние принятого запроса.</summary>
        internal RequestState State = RequestState.Queued;
        /// <summary>Индекс в одной из двух heaps; -1 означает отсутствие готового таймера.</summary>
        internal int HeapIndex = -1;
        /// <summary>0 ordinary, 1 background; используется для удаления без stale heap entries.</summary>
        internal int HeapLane;
        /// <summary>Момент следующей доставки или опционального deadline.</summary>
        internal double ScheduledAt;
        /// <summary>Элемент очереди сервиса, если операция требует ordered исполнения.</summary>
        internal LinkedListNode<PendingRequest>? OrderedNode;
        /// <summary>Ключ replaceable index, удаляемый до выдачи invocation.</summary>
        internal MergeKey? Merge;
        /// <summary>Выданный exact invocation ID; никогда не генерируется повторно для retry.</summary>
        internal string? InvocationId;
        /// <summary>True только после успешного выхода события из transport к native/runtime.</summary>
        internal bool InvocationHandedOff;
        /// <summary>Fingerprint принятого результата для duplicate/conflict проверки.</summary>
        internal byte[]? ResultFingerprint;
        /// <summary>Известный ответ нужен только до завершения reverse transit.</summary>
        internal NetworkValue? ResultPayload;
        /// <summary>Причина отложенного terminal при MarkUnavailable.</summary>
        internal RequestState? Failure;
    }
    /// <summary>История хранит fingerprints и статусы, не удерживая пользовательский payload.</summary>
    private sealed record History(byte[] Fingerprint, RequestState State, string Code, RequestKey? CoalescedInto,
        string? InvocationId = null, byte[]? ResultFingerprint = null);

    /// <summary>Живые физические и сервисные факты; не сканируются целиком для одного пакета.</summary>
    private readonly INetworkWorldView _world;
    /// <summary>Публикация графа; null закрывает маршрутизацию dirty/unready topology.</summary>
    private readonly Func<GraphSnapshot?> _graph;
    /// <summary>Подменяемый маршрутный движок без знания IPC.</summary>
    private readonly IRouteProvider _routes;
    /// <summary>Optional explicit radio capability; null сохраняет исходный ordinary-only transport.</summary>
    private readonly ICommunicationRouteProvider? _communications;
    /// <summary>Поколение владельца invocation.</summary>
    private readonly string _bindingEpoch;
    /// <summary>Лимиты активных запросов, выходов, history и producer watermarks.</summary>
    private readonly TransportOptions _options;
    /// <summary>Все принятые незавершённые запросы.</summary>
    private readonly Dictionary<RequestKey, PendingRequest> _active = [];
    /// <summary>Точный invocation lookup без сканирования active.</summary>
    private readonly Dictionary<string, PendingRequest> _invocations = new(StringComparer.Ordinal);
    /// <summary>Replaceable state только до invocation.</summary>
    private readonly Dictionary<MergeKey, PendingRequest> _replaceable = [];
    /// <summary>Ordered admission отдельно для каждого экземпляра destination service.</summary>
    private readonly Dictionary<ServiceKey, LinkedList<PendingRequest>> _ordered = [];
    /// <summary>Закрытые serials producer; ноль резервирует blocked identity, не закрывая положительные serials.</summary>
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    /// <summary>Непринятый head не позволяет pipelined serial обойти retry и закрыть его watermark.</summary>
    private readonly ProducerAdmissionFence _admission;
    /// <summary>Ограниченное окно завершений без повторного выполнения забытых ключей.</summary>
    private readonly Dictionary<RequestKey, History> _history = [];
    /// <summary>FIFO вытеснения terminal records за O(1).</summary>
    private readonly Queue<RequestKey> _historyOrder = [];
    /// <summary>История invocation result связана с тем же ограниченным окном.</summary>
    private readonly Dictionary<string, History> _results = new(StringComparer.Ordinal);
    /// <summary>Bounded выходные события; чтение означает передачу ответственности native/runtime.</summary>
    private readonly Queue<TransportEvent> _events = [];
    /// <summary>Indexed heaps не удерживают stale таймеры после отмены.</summary>
    private readonly IndexedQueue[] _queues = [new(), new()];
    /// <summary>Монотонный serial admission для стабильных равенств due.</summary>
    private ulong _serial;
    /// <summary>Ordinary work streak для гарантированного обслуживания background.</summary>
    private int _ordinaryStreak;
    /// <summary>После потери владельца новые invocation не выдаются.</summary>
    private bool _unavailable;
    /// <summary>Защита от повторного входа через ошибочный route provider; не синхронизация потоков.</summary>
    private bool _busy;

    /// <summary>Число удерживаемых незавершённых запросов.</summary>
    public int ActiveCount => _active.Count;
    /// <summary>Число ещё не забранных событий.</summary>
    public int EventCount => _events.Count;
    /// <summary>Количество terminal fingerprints в текущем окне.</summary>
    public int HistoryCount => _history.Count;
    /// <summary>Количество зарегистрированных producers за binding, включая blocked identity с watermark ноль.</summary>
    public int ProducerCount => _watermarks.Count;
    /// <summary>Последняя принятая граница игрового времени в децисекундах.</summary>
    public int SimulationTimeDs { get; private set; }

    /// <summary>Фиксирует зависимости и лимиты; не создаёт threads, sockets или игровых объектов.</summary>
    public NetworkTransport(INetworkWorldView world, Func<GraphSnapshot?> graph, IRouteProvider routes,
        string bindingEpoch, TransportOptions? options = null, ICommunicationRouteProvider? communications = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(routes);
        if (!NodeFactRules.IsValidText(bindingEpoch, 128)) throw new ArgumentException("Invalid binding.", nameof(bindingEpoch));
        _options = options ?? new TransportOptions();
        if (_options.MaxActive < 1 || _options.MaxEvents < 1 || _options.HistoryCapacity < 1 || _options.MaxProducers < 1 ||
            !double.IsFinite(_options.TickLagDs) || _options.TickLagDs <= 0 || _options.OrdinaryWeight is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        _world = world;
        _graph = graph;
        _routes = routes;
        _communications = communications;
        _bindingEpoch = bindingEpoch;
        _admission = new(_watermarks, _options.MaxProducers);
    }

    /// <summary>Возвращает текущее/недавнее состояние без выдачи mutable request или payload.</summary>
    public RequestState? GetRequestState(RequestKey key) => _active.TryGetValue(key, out var active) ? active.State :
        _history.TryGetValue(key, out var history) ? history.State : null;

    /// <summary>Принимает запрос или semantic replay; даже нулевая задержка не вызывает inline invocation.</summary>
    public TransportAdmission Submit(NetworkRequest request, int timeDs)
    {
        Enter();
        try { return _admission.Complete(SubmitCore(request, timeDs)); }
        finally { _busy = false; }
    }

    /// <summary>Продвигает не более workBudget due-элементов; заполненный выход не потребляет следующий элемент.</summary>
    public int Advance(int timeDs, int workBudget)
    {
        Enter();
        try
        {
            if (!SetTime(timeDs) || workBudget < 0) throw new ArgumentOutOfRangeException(nameof(timeDs));
            var work = 0;
            while (work < workBudget && _events.Count < _options.MaxEvents && NextDue() is { } item)
            {
                RemoveTimer(item);
                try { Process(item); }
                catch { if (_active.ContainsKey(item.Request.Key)) Schedule(item); throw; }
                work++;
            }
            return work;
        }
        finally { _busy = false; }
    }

    /// <summary>Передаёт caller один уже созданный выход; не вызывает callbacks или native handlers.</summary>
    public bool TryDequeueEvent(out TransportEvent? transportEvent)
    {
        Enter();
        try
        {
            // Dirty topology не блокирует все уже готовые terminal за ожидающим invocation.
            var scan = (long)_events.Count + 1;
            while (scan-- > 0 && _events.TryDequeue(out var candidate))
            {
                if (candidate is not ServiceInvocation invocation) { transportEvent = candidate; return true; }
                if (!_active.TryGetValue(invocation.Key, out var item) || item.InvocationId != invocation.InvocationId ||
                    item.State != RequestState.AwaitingInvocation || item.InvocationHandedOff) continue;
                if (item.Request.DeadlineDs is { } deadline && deadline <= SimulationTimeDs)
                { Finish(item, RequestState.TimedOut, "deadline_expired"); continue; }
                var reason = Revalidate(item);
                if (reason is null) _ = Route(item.Request, item.Source, item.Destination, out reason);
                if (reason == "topology_pending") { _events.Enqueue(candidate); continue; }
                if (reason is not null) { Finish(item, RequestState.Failed, reason); continue; }
                item.InvocationHandedOff = true;
                transportEvent = candidate;
                return true;
            }
            transportEvent = null;
            return false;
        }
        finally { _busy = false; }
    }

    /// <summary>Принимает exact результат один раз; would_block оставляет invocation/result полностью непринятым.</summary>
    public TransportActionResult CompleteInvocation(InvocationResult result, int timeDs)
    {
        Enter();
        try { return CompleteCore(result, timeDs); }
        finally { _busy = false; }
    }

    /// <summary>Отменяет только до invocation; после handoff возвращает too_late без попытки откатить native мир.</summary>
    public TransportActionResult Cancel(RequestKey key, int timeDs)
    {
        Enter();
        try
        {
            if (!SetTime(timeDs)) return new(false, "time_regression");
            if (!TransportFingerprint.ValidKey(key)) return new(false, "invalid_key");
            _admission.Release(key);
            if (!_active.TryGetValue(key, out var item)) return new(false, "request_unknown");
            if (item.State != RequestState.Queued) return new(false, "too_late");
            if (_events.Count == _options.MaxEvents) return new(false, "would_block", true);
            Finish(item, RequestState.Cancelled, "cancelled");
            return new(true, "cancelled");
        }
        finally { _busy = false; }
    }

    /// <summary>Закрывает admission, отзывает ещё не прочитанные invocation и классифицирует все active без unsafe retry.</summary>
    public TransportActionResult MarkUnavailable(int timeDs)
    {
        Enter();
        try
        {
            if (!SetTime(timeDs)) return new(false, "time_regression");
            _unavailable = true;
            _admission.Clear();
            var retained = _events.Where(item => item is not ServiceInvocation).ToArray();
            _events.Clear();
            foreach (var item in retained) _events.Enqueue(item);
            foreach (var item in _active.Values)
            {
                item.Failure ??= item.State == RequestState.AwaitingInvocation && item.InvocationHandedOff ? RequestState.OutcomeUnknown : RequestState.Failed;
                Schedule(item);
            }
            // Не вызывать публичный Advance повторным входом; output pressure оставляет остаток в indexed timers.
            while (_events.Count < _options.MaxEvents && NextDue() is { } item)
            {
                RemoveTimer(item);
                Process(item);
            }
            return _active.Count == 0 ? new(true, "unavailable") : new(false, "would_block", true);
        }
        finally { _busy = false; }
    }

    /// <summary>Проверяет key/content/registry/route до резервирования producer serial и активного слота.</summary>
    private TransportAdmission SubmitCore(NetworkRequest request, int timeDs)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (!SetTime(timeDs)) return new(AdmissionStatus.Rejected, "time_regression", request.Key);
        if (!TransportFingerprint.ValidRequest(request)) return new(AdmissionStatus.Rejected, "invalid_request", request.Key);
        var fingerprint = TransportFingerprint.Request(request);
        if (_active.TryGetValue(request.Key, out var active))
            return Same(active.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, "duplicate", request.Key, active.State) : new(AdmissionStatus.Rejected, "request_conflict", request.Key);
        if (_history.TryGetValue(request.Key, out var history))
            return Same(history.Fingerprint, fingerprint) ? new(AdmissionStatus.Duplicate, history.Code, request.Key, history.State, history.CoalescedInto) : new(AdmissionStatus.Rejected, "request_conflict", request.Key);
        if (_watermarks.TryGetValue(request.Key.ProducerId, out var watermark) && request.Key.Sequence <= watermark)
            return new(AdmissionStatus.Rejected, "request_expired", request.Key);
        if (_admission.Blocks(request.Key)) return new(AdmissionStatus.WouldBlock, "producer_backpressure", request.Key);
        if (_unavailable) return new(AdmissionStatus.Rejected, "unavailable", request.Key);
        if (!_watermarks.ContainsKey(request.Key.ProducerId) && _watermarks.Count == _options.MaxProducers)
            return new(AdmissionStatus.WouldBlock, "producer_capacity", request.Key);
        if (request.DeadlineDs is { } deadline && deadline <= SimulationTimeDs)
            return new(AdmissionStatus.Rejected, "deadline_expired", request.Key);
        var reason = Resolve(request, out var source, out var destination, out var contract);
        if (reason is not null) return new(AdmissionStatus.Rejected, reason, request.Key);
        if (request.CoalescingKey is not null && contract!.Safety == OperationSafety.Mutating)
            return new(AdmissionStatus.Rejected, "coalescing_unsafe", request.Key);
        var merge = request.CoalescingKey is { } key ? new MergeKey(request.Source,
            _world.Nodes[source!.Owner.NodeId].State.Address, request.Destination, destination!.Tag,
            request.OperationId, key, request.Traffic, request.Frequency) : null;
        if (merge is not null && _replaceable.TryGetValue(merge, out var previous) &&
            (previous.Contract != contract || previous.Source.Owner != source!.Owner || previous.Destination.Owner != destination!.Owner))
            RemoveMerge(previous);
        if (merge is not null && _replaceable.TryGetValue(merge, out previous))
        {
            if (_events.Count == _options.MaxEvents) return new(AdmissionStatus.WouldBlock, "event_capacity", request.Key);
            if (previous.Request.StateRevision is null || request.StateRevision is null || request.StateRevision >= previous.Request.StateRevision)
                previous.Request = previous.Request with
                {
                    Payload = request.Payload,
                    StateRevision = request.StateRevision,
                    AuthorizationContext = request.AuthorizationContext,
                    NativeContext = request.NativeContext
                };
            _watermarks[request.Key.ProducerId] = request.Key.Sequence;
            Remember(request.Key, new(fingerprint, RequestState.Superseded, "coalesced", previous.Request.Key));
            _events.Enqueue(new RequestCompleted(_bindingEpoch, request.Key, RequestState.Superseded, "coalesced"));
            return new(AdmissionStatus.Coalesced, "coalesced", request.Key, RequestState.Superseded, previous.Request.Key);
        }
        if (_active.Count == _options.MaxActive) return new(AdmissionStatus.WouldBlock, "active_capacity", request.Key);
        var route = Route(request, source!, destination!, out reason);
        if (route is null) return new(AdmissionStatus.Rejected, reason!, request.Key);
        if (_serial == ulong.MaxValue) return new(AdmissionStatus.Rejected, "serial_exhausted", request.Key);
        var item = new PendingRequest
        {
            Request = request,
            Fingerprint = fingerprint,
            Source = source!,
            Destination = destination!,
            Contract = contract!,
            Due = SimulationTimeDs + Delay(route.LatencyDs),
            Serial = ++_serial,
            Merge = merge
        };
        _active.Add(request.Key, item);
        _watermarks[request.Key.ProducerId] = request.Key.Sequence;
        if (merge is not null) _replaceable.Add(merge, item);
        if (item.Contract.Ordered)
        {
            if (!_ordered.TryGetValue(request.Destination, out var lane)) _ordered.Add(request.Destination, lane = []);
            item.OrderedNode = lane.AddLast(item);
        }
        Schedule(item);
        return new(AdmissionStatus.Accepted, "accepted", request.Key, RequestState.Queued);
    }

    /// <summary>Применяет известный native outcome; request/reply проходит отдельную reverse задержку.</summary>
    private TransportActionResult CompleteCore(InvocationResult result, int timeDs)
    {
        if (!SetTime(timeDs)) return new(false, "time_regression");
        if (result is null || result.BindingEpoch != _bindingEpoch || !NodeFactRules.IsValidText(result.InvocationId, 256) ||
            result.Payload is not null && !result.Payload.Validate() || result.Code is not null && !TransportFingerprint.ValidToken(result.Code, 64))
            return new(false, "invalid_invocation_result");
        var fingerprint = TransportFingerprint.Result(result);
        if (!_invocations.TryGetValue(result.InvocationId, out var item))
            return _results.TryGetValue(result.InvocationId, out var old) && old.ResultFingerprint is { } prior && Same(prior, fingerprint)
                ? new(true, "duplicate") : new(false, "invocation_unknown");
        if (item.ResultFingerprint is { } accepted)
            return Same(accepted, fingerprint) ? new(true, "duplicate") : new(false, "invocation_conflict");
        if (_unavailable) return new(false, "unavailable");
        if (!item.InvocationHandedOff) return new(false, "invocation_not_delivered");
        if (_events.Count == _options.MaxEvents) return new(false, "would_block", true);
        if (item.Request.DeadlineDs is { } deadline && deadline <= SimulationTimeDs)
        {
            Finish(item, RequestState.OutcomeUnknown, "invocation_deadline");
            return new(false, "invocation_expired");
        }
        RouteResult? responseRoute = null;
        string? responseReason = null;
        if (result.Success && item.Contract.Delivery == DeliverySemantics.RequestReply)
            responseRoute = Route(item.Request, item.Destination, item.Source, out responseReason);
        item.ResultFingerprint = fingerprint;
        item.ResultPayload = result.Payload;
        if (!result.Success)
        {
            // Native handler мог изменить мир до исключения; неизвестный эффект нельзя превращать в известный отказ.
            Finish(item, result.Code == "outcome_unknown" ? RequestState.OutcomeUnknown : RequestState.Failed,
                result.Code ?? "invocation_rejected", result.Payload);
            return new(true, "accepted");
        }
        if (item.Contract.Delivery == DeliverySemantics.DeliveryOnly)
            Finish(item, RequestState.Completed, "completed", result.Payload);
        else
        {
            if (responseRoute is null) Finish(item, RequestState.Failed, "response_" + responseReason);
            else
            {
                item.State = RequestState.ResponseInTransit;
                item.Due = SimulationTimeDs + Delay(responseRoute.LatencyDs);
                Schedule(item);
            }
        }
        return new(true, "accepted");
    }

    /// <summary>Обрабатывает один due: deadline, terminal failure, invocation или обратную доставку.</summary>
    private void Process(PendingRequest item)
    {
        if (item.Failure is { } failure) { Finish(item, failure, "unavailable"); return; }
        if (item.Request.DeadlineDs is { } deadline && deadline <= SimulationTimeDs)
        {
            Finish(item, item.State == RequestState.AwaitingInvocation && item.InvocationHandedOff ? RequestState.OutcomeUnknown : RequestState.TimedOut,
                item.State == RequestState.AwaitingInvocation && item.InvocationHandedOff ? "invocation_deadline" : "deadline_expired");
            return;
        }
        var reason = Revalidate(item);
        if (reason is not null) { Finish(item, RequestState.Failed, reason); return; }
        var response = item.State == RequestState.ResponseInTransit;
        var route = response ? Route(item.Request, item.Destination, item.Source, out reason) : Route(item.Request, item.Source, item.Destination, out reason);
        if (route is null) { Finish(item, RequestState.Failed, response ? "response_" + reason : reason!); return; }
        if (response) { Finish(item, RequestState.Completed, "completed", item.ResultPayload); return; }
        item.State = RequestState.AwaitingInvocation;
        item.InvocationId = _bindingEpoch + "/" + item.Request.Key.Id;
        _invocations.Add(item.InvocationId, item);
        RemoveMerge(item);
        _events.Enqueue(new ServiceInvocation(_bindingEpoch, item.Request.Key, item.InvocationId, item.Request.Source,
            item.Request.Destination, item.Source.Owner, item.Destination.Owner, item.Destination.Tag, item.Request.OperationId,
            item.Request.Payload, item.Request.DeadlineDs, item.Request.AuthorizationContext, item.Request.NativeContext,
            Traffic: item.Request.Traffic));
        Schedule(item);
    }

    /// <summary>Проверяет два сервиса и их owners по индексам, не сканируя весь мир или реестр.</summary>
    private string? Resolve(NetworkRequest request, out ServiceFact? source, out ServiceFact? destination, out OperationContract? operation)
    {
        source = null; destination = null; operation = null;
        if (!_world.Services.TryGetValue(request.Source.ServiceId, out source) || source.Generation != request.Source.Generation ||
            !_world.Services.TryGetValue(request.Destination.ServiceId, out destination) || destination.Generation != request.Destination.Generation)
            return "service_stale";
        if (!source.Online || !destination.Online) return "service_offline";
        if (!OwnerOnline(source.Owner) || !OwnerOnline(destination.Owner)) return "node_stale_or_offline";
        operation = destination.Operations.FirstOrDefault(candidate => candidate.OperationId == request.OperationId);
        return operation is null ? "operation_unavailable" : null;
    }

    /// <summary>Проверяет точное поколение и последний факт доступности физического owner.</summary>
    private bool OwnerOnline(NodeKey key) => _world.Nodes.TryGetValue(key.NodeId, out var node) && node.Generation == key.Generation && node.State.Online;

    /// <summary>Повторная exact проверка captured context применяется и к due, и непосредственно к выходному handoff.</summary>
    private string? Revalidate(PendingRequest item)
    {
        var reason = Resolve(item.Request, out var source, out var destination, out var contract);
        return reason ?? (source!.Owner != item.Source.Owner || destination!.Owner != item.Destination.Owner ||
            destination.Tag != item.Destination.Tag || source.Family != item.Source.Family || destination.Family != item.Destination.Family ||
            contract != item.Contract ? "service_revoked" : null);
    }

    /// <summary>Считает путь только на текущей публикации; ошибки ограничений поиска становятся безопасным отказом.</summary>
    private RouteResult? Route(NetworkRequest request, ServiceFact source, ServiceFact destination, out string? reason)
    {
        reason = null;
        var graph = _graph();
        if (graph is null) { reason = "topology_pending"; return null; }
        try
        {
            var route = _routes.FindRoute(graph, source.Owner, destination.Owner);
            if (route.Status == RouteStatus.Found && double.IsFinite(route.LatencyDs) && route.LatencyDs >= 0) return route;
            if (route.Status == RouteStatus.Unreachable && request.Traffic == TrafficClass.Communications && _communications is not null)
            {
                route = _communications.FindRoute(graph, new ServiceKey(source.ServiceId, source.Generation),
                    new ServiceKey(destination.ServiceId, destination.Generation), request.Frequency, _world);
                if (route.Status == RouteStatus.Found && double.IsFinite(route.LatencyDs) && route.LatencyDs >= 0) return route;
                reason = "communication_route_unavailable";
                return null;
            }
            reason = "route_unavailable";
            return null;
        }
        catch (RoutingException) { reason = "routing_error"; return null; }
        catch (CommunicationException) { reason = "communication_routing_error"; return null; }
    }

    /// <summary>Точная исходная машинная формула в ds; sub-tick остаётся асинхронной нулевой задержкой.</summary>
    private double Delay(double latency) { var delay = Math.Clamp(latency * 0.02, 0, 2.5); return delay < _options.TickLagDs ? 0 : delay; }

    /// <summary>Публикует terminal до освобождения active; caller заранее зарезервировал один выходной слот.</summary>
    private void Finish(PendingRequest item, RequestState state, string code, NetworkValue? payload = null)
    {
        _events.Enqueue(new RequestCompleted(_bindingEpoch, item.Request.Key, state, code, payload));
        RemoveTimer(item);
        RemoveMerge(item);
        _active.Remove(item.Request.Key);
        if (item.InvocationId is { } invocation) _invocations.Remove(invocation);
        Remember(item.Request.Key, new(item.Fingerprint, state, code, null, item.InvocationId, item.ResultFingerprint));
        if (item.OrderedNode is { } node)
        {
            var lane = node.List!;
            lane.Remove(node);
            if (lane.First is { } next) Schedule(next.Value);
            else _ordered.Remove(item.Request.Destination);
        }
    }

    /// <summary>Вытесняет history вместе с invocation fingerprints, сохраняя producer watermark навсегда в binding.</summary>
    private void Remember(RequestKey key, History record)
    {
        _history.Add(key, record);
        _historyOrder.Enqueue(key);
        if (record.InvocationId is { } invocation) _results[invocation] = record;
        if (_history.Count <= _options.HistoryCapacity) return;
        var oldKey = _historyOrder.Dequeue();
        var old = _history[oldKey];
        _history.Remove(oldKey);
        if (old.InvocationId is { } oldInvocation) _results.Remove(oldInvocation);
    }

    /// <summary>Открепляет replaceable slot ровно у его владельца.</summary>
    private void RemoveMerge(PendingRequest item)
    {
        if (item.Merge is { } key && _replaceable.TryGetValue(key, out var owner) && ReferenceEquals(owner, item)) _replaceable.Remove(key);
        item.Merge = null;
    }

    /// <summary>Отказ регрессии времени не подменяет монотонную границу предыдущих принятых команд.</summary>
    private bool SetTime(int timeDs) { if (timeDs < SimulationTimeDs) return false; SimulationTimeDs = timeDs; return true; }

    /// <summary>Fingerprints имеют фиксированную длину; содержимое сравнивается без раннего выхода.</summary>
    private static bool Same(byte[] first, byte[] second) => CryptographicOperations.FixedTimeEquals(first, second);

    /// <summary>Запрещает повторный вход в mutation через произвольный trusted route provider.</summary>
    private void Enter() { if (_busy) throw new InvalidOperationException("Reentrant transport operation."); _busy = true; }
}
