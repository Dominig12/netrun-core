using Netrun.Core.Communication;

namespace Netrun.Core;

/// <summary>Идентичность физического узла; повторное использование адреса её не сохраняет.</summary>
/// <param name="NodeId">Логический ID.</param>
/// <param name="Generation">Поколение экземпляра.</param>
public sealed record NodeKey(string NodeId, ulong Generation);

/// <summary>Исходные native-факты для топологии, без рассчитанных рёбер и маршрутов.</summary>
/// <param name="BaseAreaId">Физическая base area носителя, либо null при отсутствии положения.</param>
/// <param name="SuppliedAreaId">Питаемая APC область, которая может отличаться от места установки.</param>
/// <param name="Department">Классификация питаемой APC области: security/command/engineering/medical/science/cargo/general.</param>
/// <param name="IsNtnetRelay">Реальный native relay; gateway сам по себе не задаёт этот признак.</param>
/// <param name="RelayOperational">Native operational отдельно от временного offline_until.</param>
/// <param name="IsStationLevel">Принадлежность текущего Z станции для fallback LAN.</param>
/// <param name="IsMiningLevel">Принадлежность текущего Z шахте для fallback LAN.</param>
/// <param name="InitialAnchor">Одноразовый seed существующей привязки при bootstrap, не готовый маршрут.</param>
public sealed record NodeNetworkFacts(string? BaseAreaId, string? SuppliedAreaId, string Department = "general",
    bool IsNtnetRelay = false, bool RelayOperational = true, bool IsStationLevel = true, bool IsMiningLevel = false,
    NodeKey? InitialAnchor = null);

/// <summary>Связанный сектор с исходным порядком этажей, а не сортировкой числовых Z.</summary>
/// <param name="SectorId">Идентификатор сектора.</param>
/// <param name="Generation">Поколение определения сектора.</param>
/// <param name="Revision">Версия полного определения.</param>
/// <param name="Levels">Неповторяющиеся Z снизу вверх; 1..4096 элементов.</param>
public sealed record SectorFact(string SectorId, ulong Generation, ulong Revision, ValueList<int> Levels);

/// <summary>Объявляет полное определение связности этажей.</summary>
/// <param name="Sector">Новая версия сектора.</param>
public sealed record SectorUpsert(SectorFact Sector) : WorldChange;
/// <summary>Удаляет сектор с сохранением поколения и tombstone.</summary>
/// <param name="SectorId">Удаляемый ID.</param>
/// <param name="Generation">Удаляемое поколение.</param>
/// <param name="Revision">Версия удаления.</param>
public sealed record SectorRemove(string SectorId, ulong Generation, ulong Revision) : WorldChange;

/// <summary>Категория эффектов определяет допустимость retry; авторизация остаётся в DM.</summary>
public enum OperationSafety
{
    /// <summary>Операция читает текущее состояние.</summary>
    ReadOnly,
    /// <summary>Повтор имеет тот же эффект при подтверждённом контракте адаптера.</summary>
    Idempotent,
    /// <summary>Произвольные изменения; неизвестный результат нельзя автоматически повторять.</summary>
    Mutating
}

/// <summary>Какой terminal результат ожидает отправитель.</summary>
public enum DeliverySemantics
{
    /// <summary>Успех определяется завершением receive, отдельного ответа может не быть.</summary>
    DeliveryOnly,
    /// <summary>Ожидается отдельное обратное сообщение с корреляцией.</summary>
    RequestReply
}

/// <summary>Объявленный тип операции без proc names, typepaths и произвольного исполняемого кода.</summary>
/// <param name="OperationId">Allowlisted ID обработчика.</param>
/// <param name="Safety">Семантика повторов и побочных эффектов.</param>
/// <param name="Delivery">Доставка либо коррелированный ответ.</param>
/// <param name="Ordered">Требуется последовательное исполнение операций этого сервиса.</param>
public sealed record OperationContract(string OperationId, OperationSafety Safety = OperationSafety.Mutating,
    DeliverySemantics Delivery = DeliverySemantics.DeliveryOnly, bool Ordered = true);

/// <summary>Объявление конкретного экземпляра сервиса; наличие записи не заменяет native-проверки.</summary>
/// <param name="ServiceId">Уникальный ID экземпляра, независимый от семейства сервиса.</param>
/// <param name="Generation">Поколение адаптера; замена компонента создаёт новое.</param>
/// <param name="Revision">Версия полного объявления.</param>
/// <param name="Owner">Узел и его поколение.</param>
/// <param name="Family">Тип сервиса, например atmos.device.</param>
/// <param name="Tag">Текущий domain tag; смена отзывает старые in-flight обращения.</param>
/// <param name="Online">Экспортированный допуск сервиса; финальный допуск проверяет DM.</param>
/// <param name="Operations">Ограниченный allowlist операций.</param>
/// <param name="Communication">Optional raw native port, принадлежащий тому же Owner; не добавляет обычные graph edges.</param>
public sealed record ServiceFact(string ServiceId, ulong Generation, ulong Revision, NodeKey Owner,
    string Family, string? Tag, bool Online, ValueList<OperationContract> Operations, CommunicationPortFact? Communication = null);

/// <summary>Публикует полное объявление экземпляра сервиса.</summary>
/// <param name="Service">Новая версия объявления.</param>
public sealed record ServiceUpsert(ServiceFact Service) : WorldChange;
/// <summary>Отзывает экземпляр сервиса и сохраняет tombstone.</summary>
/// <param name="ServiceId">Уникальный ID экземпляра.</param>
/// <param name="Generation">Удаляемое поколение адаптера.</param>
/// <param name="Revision">Версия отзыва.</param>
public sealed record ServiceRemove(string ServiceId, ulong Generation, ulong Revision) : WorldChange;
