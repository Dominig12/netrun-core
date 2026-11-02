using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Netrun.Core.Communication;
using Netrun.Core.Discovery;
using Netrun.Core.Projection;

namespace Netrun.Core.Runtime;

/// <summary>Одна логическая команда внешнего frame; смешивание команды и произвольных world-фактов запрещено формой ADT.</summary>
public abstract record RuntimeCommand;

/// <summary>Атомарно применяет ordered world-факты либо один snapshot control.</summary>
/// <param name="Entries">Стабильный до возврата список ограниченных domain records.</param>
public sealed record ApplyWorld(IReadOnlyList<WorldChange> Entries) : RuntimeCommand;

/// <summary>Передаёт запрос в bounded admission; принятие frame не означает native-эффект.</summary>
/// <param name="Request">Полный immutable request со своим producer identity.</param>
public sealed record SubmitRequest(NetworkRequest Request) : RuntimeCommand;

/// <summary>
/// Нормализованный отказ одного syntactically valid request_submit с недопустимым payload.
/// Decoder не сохраняет исходное дерево, raw JSON, native контексты или текст ошибки.
/// Это внутренний semantic command без самостоятельного внешнего wire kind.
/// </summary>
/// <param name="Key">Уже проверенный canonical request ID исходного сообщения.</param>
/// <param name="Code">Bounded safe token; по умолчанию invalid_payload.</param>
public sealed record RejectRequest(RequestKey Key, string Code = "invalid_payload") : RuntimeCommand;

/// <summary>Отменяет запрос либо сообщает, что native-эффект уже мог начаться.</summary>
/// <param name="Key">Точный producer/serial исходного запроса.</param>
public sealed record CancelRequest(RequestKey Key) : RuntimeCommand;

/// <summary>Начинает bounded async поиск сетевых кандидатов без native acceptance/permission effects.</summary>
/// <param name="Request">Полная identity и typed family/tag/channel policy поиска.</param>
public sealed record DiscoverServices(DiscoveryRequest Request) : RuntimeCommand;

/// <summary>Отменяет ещё активный поиск и отзывает его непрочитанные страницы.</summary>
/// <param name="Key">Точный producer/serial исходного discovery запроса.</param>
public sealed record CancelDiscovery(RequestKey Key) : RuntimeCommand;

/// <summary>Начинает одноразовую read-only graph/routes проекцию для native UI.</summary>
/// <param name="Request">Typed вид таблицы, exact source и route availability policy.</param>
public sealed record QueryProjection(ProjectionRequest Request) : RuntimeCommand;

/// <summary>Отзывает ещё не опубликованную клиентскую таблицу и её unread страницы.</summary>
/// <param name="Key">Точный producer/serial исходного UI запроса.</param>
public sealed record CancelProjection(RequestKey Key) : RuntimeCommand;

/// <summary>Начинает единственный persistent graph stream текущего binding.</summary>
/// <param name="Key">Монотонный producer identity подписки.</param>
public sealed record StartGraphSynchronization(RequestKey Key) : RuntimeCommand;

/// <summary>Подтверждает применение целой bounded страницы native mirror и разрешает следующую.</summary>
/// <param name="Key">Identity той же подписки.</param>
/// <param name="PageIndex">Положительный exact serial применённой страницы.</param>
public sealed record ContinueGraphSynchronization(RequestKey Key, ulong PageIndex) : RuntimeCommand;

/// <summary>Отменяет stream и освобождает baseline/target без перезапуска binding.</summary>
/// <param name="Key">Identity прекращаемой подписки.</param>
public sealed record CancelGraphSynchronization(RequestKey Key) : RuntimeCommand;

/// <summary>Возвращает подтверждённый native-результат без исполнения handler внутри ядра.</summary>
/// <param name="Result">Результат exact invocation и binding.</param>
public sealed record FinishInvocation(InvocationResult Result) : RuntimeCommand;

/// <summary>Читает текущую опубликованную топологию с явной конечной политикой доступности.</summary>
/// <param name="Source">Физическое поколение отправителя.</param>
/// <param name="Destination">Физическое поколение получателя.</param>
/// <param name="Mode">Machine либо Discovery.</param>
public sealed record QueryRoute(NodeKey Source, NodeKey Destination, RouteKind Mode = RouteKind.Machine) : RuntimeCommand;

/// <summary>Точка native UI без собственного узла; identity запроса не является physical node или ссылкой на DM object.</summary>
/// <param name="Key">Canonical producer serial для корреляции bounded logical reply.</param>
/// <param name="Position">Физические tile coordinates.</param>
/// <param name="SectorId">Объявленный связанный sector с ordered floors.</param>
/// <param name="BaseAreaId">Native base area либо null для общего nearest APC.</param>
public sealed record AnchorRequest(RequestKey Key, Position Position, string SectorId, string? BaseAreaId = null)
{
    /// <summary>Одинаковые typed bounds для direct runtime и wire codec без чтения мира.</summary>
    public static bool IsValid(AnchorRequest? request) => request is not null && TransportFingerprint.ValidKey(request.Key) &&
        NetworkTopology.IsValidAnchorPoint(request.Position, request.SectorId, request.BaseAreaId);
}

/// <summary>Разрешает arbitrary point в structural APC через текущий spatial index, без query workspace или новых event slots.</summary>
/// <param name="Request">Полная immutable point identity.</param>
public sealed record QueryAnchor(AnchorRequest Request) : RuntimeCommand;

/// <summary>Немедленное наблюдение anchor; node:null и code сохраняют различие pending/unknown sector/no APC.</summary>
/// <param name="Key">Исходный producer serial.</param>
/// <param name="Node">Выбранный exact APC generation либо null.</param>
/// <param name="GraphVersion">Structural publication, ноль при pending.</param>
/// <param name="GraphInputSequence">Граница facts этой publication, ноль при pending.</param>
/// <param name="Code">found/not_found/topology_pending/sector_unknown/sector_position.</param>
public sealed record AnchorQueryResult(RequestKey Key, NodeKey? Node, ulong GraphVersion, ulong GraphInputSequence, string Code);

/// <summary>Меняет доступность implicit quantum backbone, не перестраивая физические кабели.</summary>
/// <param name="Enabled">Общий допуск relay-переходов.</param>
public sealed record ConfigureBackbone(bool Enabled) : RuntimeCommand;

/// <summary>Единая причинная последовательность фактов, времени и логических команд одного binding.</summary>
/// <param name="Epochs">Идентичность владельца.</param>
/// <param name="Sequence">Положительный внешний sequence; повтор сохраняет полное semantic содержимое.</param>
/// <param name="SimulationTimeDs">Монотонная граница игрового времени в децисекундах.</param>
/// <param name="Command">Ровно одна логическая операция.</param>
public sealed record RuntimeInput(Epochs Epochs, ulong Sequence, int SimulationTimeDs, RuntimeCommand Command);

/// <summary>Граница ACK и отдельные результаты команды; состояние mirror не подменяется готовностью графа.</summary>
/// <param name="Apply">Cumulative ACK полностью обработанных frames.</param>
/// <param name="SyncState">Состояние публикации исходных фактов.</param>
/// <param name="GraphVersion">Версия пригодного графа; ноль при отсутствии опубликованного графа.</param>
/// <param name="TopologyPending">Исходные факты опережают пригодную топологию; старые маршруты запрещены.</param>
/// <param name="Admission">Отдельный результат request admission.</param>
/// <param name="Action">Отдельный результат отмены, native-result или настройки.</param>
/// <param name="Route">Immutable ответ маршрутизации.</param>
/// <param name="ErrorCode">Безопасный отказ модуля, если факты/команда приняты, но операция временно недоступна.</param>
/// <param name="CommunicationError">Неконфигурационный конфликт native advertisements; не закрывает обычную сеть или binding.</param>
/// <param name="GraphInputSequence">Граница world facts пригодного graph; ноль при pending.</param>
/// <param name="AvailabilityVersion">Текущая authoritative overlay revision для отзыва native cached read models.</param>
/// <param name="Anchor">Немедленный bounded point-to-APC reply; null для других команд.</param>
/// <param name="InfrastructureStamp">Structural proof APC/real relay/sector, независимый от ordinary leaf churn.</param>
/// <param name="AvailabilityInfrastructureStamp">Доступность forwarders/backbone, независимая от ordinary leaf availability.</param>
/// <param name="CommunicationRevision">Текущая отдельная версия native port policies, не ordinary routing authority.</param>
public sealed record RuntimeResult(ApplyResult Apply, MirrorSyncState SyncState, ulong GraphVersion,
    bool TopologyPending, TransportAdmission? Admission = null, TransportActionResult? Action = null,
    RouteResult? Route = null, string? ErrorCode = null, string? CommunicationError = null,
    ulong GraphInputSequence = 0, ulong AvailabilityVersion = 0, AnchorQueryResult? Anchor = null,
    ulong InfrastructureStamp = 0, ulong AvailabilityInfrastructureStamp = 0, ulong CommunicationRevision = 0);

/// <summary>Typed composition конфигурации модулей; null выбирает их консервативные default limits.</summary>
/// <param name="Core">Ограничения mirror/snapshot и общего replay ledger.</param>
/// <param name="Topology">Геометрия, binding policy и materialized graph limits.</param>
/// <param name="Router">Bounded LRU и workspace поиска.</param>
/// <param name="Transport">Admission, очереди результатов и справедливое продвижение запросов.</param>
/// <param name="DeferTopologyBuild">Объединяет structural inputs до следующего Pump; на pending маршруты не выдаются.</param>
/// <param name="PumpWorkBudget">Default work budget одного вызова Pump.</param>
/// <param name="Communication">Пределы и latency отдельного native fallback, не обычного графа.</param>
/// <param name="Discovery">Пределы candidate snapshots, pages и fair query slices.</param>
/// <param name="Projection">Пределы UI query slots и страниц; runtime ограничивает PageRows значением 32.</param>
/// <param name="SourceTrees">Пределы общего полного route-tree поиска и bounded LRU.</param>
/// <param name="GraphSynchronization">Ограничения единственного persistent graph stream.</param>
public sealed record RuntimeOptions(CoreLimits? Core = null, TopologyOptions? Topology = null,
    RouterOptions? Router = null, TransportOptions? Transport = null, bool DeferTopologyBuild = false,
    int PumpWorkBudget = 128, CommunicationOptions? Communication = null, DiscoveryOptions? Discovery = null,
    ProjectionOptions? Projection = null, SourceTreeOptions? SourceTrees = null,
    GraphSynchronizationOptions? GraphSynchronization = null);
