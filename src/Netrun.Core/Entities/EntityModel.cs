using Netrun.Core.Transport;

namespace Netrun.Core.Entities;

/// <summary>Вид процесса; native представление не владеет его вычисляемым состоянием.</summary>
public enum EntityKind { Resident, Bot, Ice }

/// <summary>Неизменяемая персона процесса, независимая от native моба или консоли.</summary>
public sealed record EntityPersona(string Alias, string Avatar = "orb", string Accent = "silver",
    string Tagline = "", string Theme = "ion");

/// <summary>Создание процесса; после принятия integrity/status/residence изменяет только EntityWorld.</summary>
public sealed record EntityDefinition(ActorKey Actor, EntityKind Kind, NodeKey Node, double MaxIntegrity,
    EntityPersona Persona, string FactionId, string? PrincipalId = null, string? ProfileId = null,
    bool Autonomous = false, int? ExpiresAtDs = null);

/// <summary>Read model процесса. Копия в DM используется для отображения и native обратной связи.</summary>
public sealed record EntityView(ActorKey Actor, ulong Revision, ulong ResidenceRevision, EntityKind Kind,
    NodeKey Node, double Integrity, double MaxIntegrity, EntityPersona Persona, string FactionId,
    string? PrincipalId, string? ProfileId, bool Autonomous, int StunnedUntilDs, int ShieldUntilDs,
    double ShieldFactor, int NextHopDs, bool Alive, string? PendingEffectId, int? ExpiresAtDs,
    bool StunnedByTurn = false, bool ShieldedByTurn = false, double Trace = 0, int CombatIgnoreUntilDs = 0);

/// <summary>Закрытый набор намерений; неизвестный код никогда не выполняется как DM proc.</summary>
public abstract record EntityAction;
/// <summary>Регистрирует новый процесс с точным поколением.</summary>
public sealed record CreateEntity(EntityDefinition Definition) : EntityAction;
/// <summary>Удаляет процесс и отзывает незавершённые действия.</summary>
public sealed record DeleteEntity(string Code = "removed") : EntityAction;
/// <summary>Наносит урон; shield вычисляется владельцем EntityWorld.</summary>
public sealed record DamageEntity(double Amount, bool IgnoreShield = false) : EntityAction;
/// <summary>Восстанавливает целостность до максимума процесса.</summary>
public sealed record HealEntity(double Amount) : EntityAction;
/// <summary>Продлевает оглушение без сокращения уже действующего срока.</summary>
public sealed record StunEntity(int DurationDs) : EntityAction;
/// <summary>Накладывает щит; более слабый не заменяет и не продлевает сильный.</summary>
public sealed record ShieldEntity(double Percent, int DurationDs) : EntityAction;
/// <summary>Запрашивает native переход; residence подтверждается отдельным точным результатом эффекта.</summary>
public sealed record MoveEntity(NodeKey Destination) : EntityAction;
/// <summary>Смена клиента останавливает/возобновляет автономное поведение в том же процессе.</summary>
public sealed record ControlEntity(bool Autonomous) : EntityAction;
/// <summary>Обновляет полный проверенный профиль; частичных изменений при отказе нет.</summary>
public sealed record SetEntityPersona(EntityPersona Persona) : EntityAction;
/// <summary>Изменяет принадлежность процесса, не выдавая native ID-доступов.</summary>
public sealed record SetEntityFaction(string FactionId, string? PrincipalId) : EntityAction;
/// <summary>Возвращает версионированное состояние без перерасчёта графа или действий.</summary>
public sealed record ReadEntity : EntityAction;

/// <summary>Внутренний результат Combat owner; long.MaxValue обозначает turn-owned статус до явного снятия.</summary>
public sealed record SetEntityCombatState(double ShieldFactor, long ShieldUntilDs, long StunUntilDs) : EntityAction;
/// <summary>Внутреннее изменение трассировки, рассчитанное Core программой/системой.</summary>
public sealed record AdjustEntityTrace(double Delta) : EntityAction;
/// <summary>Враждебный боевой ввод снимает grace, не выдавая новых native прав.</summary>
public sealed record ClearEntityCombatGrace : EntityAction;

/// <summary>Монотонное намерение. ExpectedRevision=null используется только для доверенных событий мира.</summary>
public sealed record EntityOperation(RequestKey Key, ActorKey Actor, ulong? ExpectedRevision, EntityAction Action);

/// <summary>Результат ровно одного выданного native действия; неизвестный исход не повторяется автоматически.</summary>
public sealed record EntityEffectResult(string BindingEpoch, ActorKey Actor, string EffectId,
    ulong ResidenceRevision, bool Success);

/// <summary>Состояние/отказ операции; projection не удерживает исходное намерение или mutable native объект.</summary>
public sealed record EntityChanged(string BindingEpoch, RequestKey Key, ActorKey Actor,
    EntityView? View, string Code) : TransportEvent(BindingEpoch, Key);

/// <summary>Единственное native действие процесса. DM проверяет поколение и residence перед исполнением.</summary>
public sealed record EntityNativeEffect(string BindingEpoch, RequestKey Key, ActorKey Actor, string EffectId,
    ulong ResidenceRevision, NodeKey Source, NodeKey Destination) : TransportEvent(BindingEpoch, Key);

/// <summary>Ограничения владельца; would_block не изменяет actor или monotonic producer watermark.</summary>
public sealed record EntityWorldOptions(int MaxEntities = int.MaxValue, int MaxTrackedIds = int.MaxValue,
    int MaxEvents = int.MaxValue, int MaxProducers = int.MaxValue, int HistoryCapacity = 256);
