using System.Globalization;

namespace Netrun.Core.Transport;

/// <summary>Ключ producer-session и его монотонный serial; watermark запрещает повтор забытых запросов.</summary>
/// <param name="ProducerId">ASCII letters/digits/._-, 1..64 символа; новый producer после исчерпания требует нового binding.</param>
/// <param name="Sequence">Положительный serial; transport допускает только новые значения выше watermark.</param>
public sealed record RequestKey(string ProducerId, ulong Sequence)
{
    /// <summary>Канонический request ID без неоднозначного объединения двух произвольных строк.</summary>
    public string Id => ProducerId + ":" + Sequence.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Точный экземпляр сервиса; повторное использование service ID не сохраняет поколение.</summary>
/// <param name="ServiceId">Логический ID экземпляра.</param>
/// <param name="Generation">Положительное поколение адаптера.</param>
public sealed record ServiceKey(string ServiceId, ulong Generation);

/// <summary>Явная attested packet capability; название сервиса само по себе не включает radio fallback.</summary>
public enum TrafficClass
{
    /// <summary>Только общий Machine graph.</summary>
    Ordinary,
    /// <summary>Разрешает отдельный native overlay при allowlisted families обоих концов.</summary>
    Communications
}

/// <summary>Типизированный запрос; transport не получает proc names или ссылки на DM-объекты.</summary>
/// <param name="Key">Идентичность producer-запроса.</param>
/// <param name="Source">Точный исходный сервис.</param>
/// <param name="Destination">Точный сервис-получатель.</param>
/// <param name="OperationId">Объявленная получателем allowlisted операция.</param>
/// <param name="Payload">Неизменяемое ограниченное дерево данных.</param>
/// <param name="DeadlineDs">Опциональный абсолютный deadline игрового времени; null сохраняет исходный no-TTL контракт DM.</param>
/// <param name="CoalescingKey">Явный opt-in только безопасного заменяемого состояния; mutating операции не заменяются.</param>
/// <param name="StateRevision">Опциональная domain revision; старый producer не заменит уже ожидающую новую версию.</param>
/// <param name="AuthorizationContext">Opaque ID native-контекста прав; авторизацию повторно выполняет DM.</param>
/// <param name="NativeContext">Opaque ID DM-owned объектов, которые нельзя сериализовать как исполняемые адреса.</param>
/// <param name="Traffic">Явный класс native packet; ordinary не получает radio capability.</param>
/// <param name="Frequency">Положительная частота communications либо null для control; ordinary требует null.</param>
public sealed record NetworkRequest(RequestKey Key, ServiceKey Source, ServiceKey Destination, string OperationId,
    NetworkValue Payload, int? DeadlineDs = null, string? CoalescingKey = null, ulong? StateRevision = null,
    string? AuthorizationContext = null, string? NativeContext = null,
    TrafficClass Traffic = TrafficClass.Ordinary, int? Frequency = null);

/// <summary>Полный lifecycle принятого запроса; terminal результат не подразумевает безопасный retry.</summary>
public enum RequestState
{
    /// <summary>Ожидается срок доставки и очередь сервиса.</summary>
    Queued,
    /// <summary>Invocation уже передан выходному каналу; возможны native-эффекты.</summary>
    AwaitingInvocation,
    /// <summary>DM-результат принят, ответ моделируется отдельным обратным маршрутом.</summary>
    ResponseInTransit,
    /// <summary>Известный успешный terminal результат.</summary>
    Completed,
    /// <summary>Известный отказ без автоматического повтора.</summary>
    Failed,
    /// <summary>Отмена до выдачи invocation; native-эффект не был запрошен.</summary>
    Cancelled,
    /// <summary>Опциональный deadline истёк до invocation или при доставке известного ответа.</summary>
    TimedOut,
    /// <summary>Invocation мог исполниться; результат не доказан и не повторяется автоматически.</summary>
    OutcomeUnknown,
    /// <summary>Заменяемое состояние объединено с исходным запросом без собственного invocation.</summary>
    Superseded
}

/// <summary>Admission отделён от фактической доставки и native-исполнения.</summary>
public enum AdmissionStatus
{
    /// <summary>Новый запрос принят в ограниченную очередь.</summary>
    Accepted,
    /// <summary>Содержательно идентичный ключ уже известен.</summary>
    Duplicate,
    /// <summary>Состояние объединено с прежним запросом; результат указывает оригинальный ключ.</summary>
    Coalesced,
    /// <summary>Контракт, идентичность, маршрут или watermark не допускают admission.</summary>
    Rejected,
    /// <summary>Нет bounded-ёмкости; caller может повторить тот же ещё не принятый ключ.</summary>
    WouldBlock
}

/// <summary>Безопасный результат admission, не раскрывающий payload.</summary>
/// <param name="Status">Категория admission.</param>
/// <param name="Code">Стабильный код причины.</param>
/// <param name="Key">Ключ рассматриваемого запроса.</param>
/// <param name="State">Известное текущее/terminal состояние, если ключ принят.</param>
/// <param name="CoalescedInto">Исходный ключ при объединении состояния.</param>
public sealed record TransportAdmission(AdmissionStatus Status, string Code, RequestKey Key,
    RequestState? State = null, RequestKey? CoalescedInto = null);

/// <summary>Результат управляющей операции; would_block не потребляет ещё не принятый DM-результат.</summary>
/// <param name="Accepted">Операция обработана.</param>
/// <param name="Code">Безопасная причина или статус.</param>
/// <param name="WouldBlock">Caller должен освободить выходную очередь и повторить операцию.</param>
public sealed record TransportActionResult(bool Accepted, string Code, bool WouldBlock = false);

/// <summary>Выходное событие принадлежит одному binding и одному producer-запросу.</summary>
/// <param name="BindingEpoch">Поколение владельца, проверяемое native-адаптером.</param>
/// <param name="Key">Стабильная корреляция запроса.</param>
public abstract record TransportEvent(string BindingEpoch, RequestKey Key);

/// <summary>Единственное разрешение запросить DM handler; сам transport не выполняет эффект.</summary>
/// <param name="BindingEpoch">Поколение владельца.</param>
/// <param name="Key">Исходный ключ запроса, сохраняющийся при coalescing.</param>
/// <param name="InvocationId">Стабильный ID invocation для native dedup.</param>
/// <param name="Source">Захваченный исходный сервис.</param>
/// <param name="Destination">Захваченный экземпляр получателя.</param>
/// <param name="SourceNode">Захваченное поколение исходного физического узла.</param>
/// <param name="DestinationNode">Захваченное поколение физического узла получателя.</param>
/// <param name="CapturedTag">Tag получателя при admission; смена отзывает обращение.</param>
/// <param name="OperationId">Разрешённый ID операции.</param>
/// <param name="Payload">Последнее безопасно объединённое immutable состояние.</param>
/// <param name="DeadlineDs">Опциональный deadline, который также проверяет DM.</param>
/// <param name="AuthorizationContext">Opaque native-контекст прав.</param>
/// <param name="NativeContext">Opaque native-контекст объектов.</param>
/// <param name="SourceNodeRevision">Raw fact revision на границе runtime handoff; zero допустим для standalone transport.</param>
/// <param name="DestinationNodeRevision">Raw fact revision адресата на той же последовательной границе.</param>
/// <param name="InfrastructureStamp">Captured structural transit proof; сериализованный backlog не получает новую authority автоматически.</param>
/// <param name="AvailabilityInfrastructureStamp">Captured forwarder/backbone availability proof.</param>
/// <param name="Traffic">Класс запроса позволяет runtime добавить отдельный proof native radio policy.</param>
/// <param name="CommunicationRevision">Captured port catalog revision только для communications; null для ordinary.</param>
public sealed record ServiceInvocation(string BindingEpoch, RequestKey Key, string InvocationId,
    ServiceKey Source, ServiceKey Destination, NodeKey SourceNode, NodeKey DestinationNode, string? CapturedTag,
    string OperationId, NetworkValue Payload, int? DeadlineDs, string? AuthorizationContext, string? NativeContext,
    ulong SourceNodeRevision = 0, ulong DestinationNodeRevision = 0,
    ulong InfrastructureStamp = 0, ulong AvailabilityInfrastructureStamp = 0,
    TrafficClass Traffic = TrafficClass.Ordinary, ulong? CommunicationRevision = null)
    : TransportEvent(BindingEpoch, Key);

/// <summary>Один terminal outcome каждого принятого запроса, включая coalesced aliases.</summary>
/// <param name="BindingEpoch">Поколение владельца.</param>
/// <param name="Key">Ключ завершённого запроса.</param>
/// <param name="State">Terminal состояние.</param>
/// <param name="Code">Безопасный код результата.</param>
/// <param name="Payload">Опциональное содержимое известного DM-ответа.</param>
public sealed record RequestCompleted(string BindingEpoch, RequestKey Key, RequestState State, string Code,
    NetworkValue? Payload = null) : TransportEvent(BindingEpoch, Key);

/// <summary>Результат exact invocation, возвращённый доверенным native-адаптером после его собственных проверок.</summary>
/// <param name="BindingEpoch">Binding, которому принадлежал invocation.</param>
/// <param name="InvocationId">Стабильный ID выданного invocation.</param>
/// <param name="Success">Native handler подтвердил успех.</param>
/// <param name="Payload">Опциональный immutable ответ.</param>
/// <param name="Code">Краткий allowlisted диагностический код, без native exception/payload.</param>
public sealed record InvocationResult(string BindingEpoch, string InvocationId, bool Success,
    NetworkValue? Payload = null, string? Code = null);

/// <summary>Лимиты памяти и работы; параметры не подменяют игровые сроки обязательным default timeout.</summary>
/// <param name="MaxActive">Максимум принятых незавершённых запросов.</param>
/// <param name="MaxEvents">Максимум ещё не забранных выходных событий.</param>
/// <param name="HistoryCapacity">Окно terminal request/result fingerprints без удержания payload.</param>
/// <param name="MaxProducers">Максимум producer watermarks за binding; они не вытесняются по TTL.</param>
/// <param name="TickLagDs">Минимальный представимый native tick; меньшие transit задержки становятся нулевыми.</param>
/// <param name="OrdinaryWeight">Максимум ordinary due-работ подряд при готовой background-работе.</param>
public sealed record TransportOptions(int MaxActive = 2048, int MaxEvents = 4096, int HistoryCapacity = 4096,
    int MaxProducers = 128, double TickLagDs = 1, int OrdinaryWeight = 8);
