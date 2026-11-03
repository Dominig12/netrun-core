namespace Netrun.Core.Entities.Combat;

/// <summary>Вид боевого участника; один battery-actor представляет все пассивные модули крепости.</summary>
public enum CombatActorKind
{
    /// <summary>Резидент либо удалённая консоль.</summary>
    Entity,
    /// <summary>Автономный ICE с подготовкой программы.</summary>
    Ice,
    /// <summary>Единая очередь батареи крепости.</summary>
    Fortress
}

/// <summary>Immutable вход от общего registry; Node — уже проверенный anchor локального либо удалённого контакта.</summary>
public sealed record CombatActorFact(ActorKey Key, NodeKey Node, double Integrity, double MaxIntegrity,
    ValueList<string> Programs, CombatActorKind Kind = CombatActorKind.Entity, bool Valid = true,
    bool TakesTurns = true, double InitialShieldFactor = 1, long InitialShieldUntilDs = 0, long InitialStunUntilDs = 0);

/// <summary>Намерение не содержит native callbacks и не выдаёт физических прав или маршрутов.</summary>
public abstract record CombatIntent(ActorKey Actor);
/// <summary>Проверяет допуск действия, записывая фактических участников без расходования AP.</summary>
public sealed record AdmitCombatAction(ActorKey Actor, ActorKey? Target, int Cost) : CombatIntent(Actor);
/// <summary>Явное присоединение наблюдателя в хвост очереди своего anchor.</summary>
public sealed record JoinCombat(ActorKey Actor, NodeKey Node, string? Token = null) : CombatIntent(Actor);
/// <summary>Окончание текущего решения с проверкой точного serial:generation.</summary>
public sealed record PassCombatTurn(ActorKey Actor, string? Token = null) : CombatIntent(Actor);
/// <summary>Общий запуск программы игрока/бота/ICE; подготовка расходует AP ровно один раз.</summary>
public sealed record UseCombatProgram(ActorKey Actor, string ProgramId, ActorKey? Target = null, string? Token = null,
    bool Telegraph = false, double DamageScale = 1, string? ExecutableId = null) : CombatIntent(Actor);
/// <summary>Попытка завершить уже подготовленную программу; ранний resolve не наносит эффект.</summary>
public sealed record ResolveCombatCast(ActorKey Actor) : CombatIntent(Actor);
/// <summary>Отмена подготовки не возвращает AP, расходник или cooldown.</summary>
public sealed record CancelCombatCast(ActorKey Actor) : CombatIntent(Actor);
/// <summary>Расход AP для небоевых действий, в том числе перехода между узлами.</summary>
public sealed record SpendCombatAp(ActorKey Actor, int Cost, bool Deferred = false, bool Hostile = false,
    string? Token = null) : CombatIntent(Actor);
/// <summary>Завершение зарезервированного действия без повторной оплаты AP.</summary>
public sealed record ResolveCombatAction(ActorKey Actor) : CombatIntent(Actor);

/// <summary>Результат admission отделён от будущих native-побочных эффектов.</summary>
public sealed record CombatResult(bool Accepted, string Code);

/// <summary>Вид immutable выхода; общий entity registry применяет HP/trace, DM выполняет только native эффекты.</summary>
public enum CombatEffectKind
{
    /// <summary>Урон после turn-owned щита; registry уменьшает integrity.</summary>
    Damage,
    /// <summary>Восстановление; registry ограничивает максимумом integrity.</summary>
    Heal,
    /// <summary>Изменение щита; Value — factor, UntilDs — конечный срок либо long.MaxValue для turn ownership.</summary>
    Shield,
    /// <summary>Изменение оглушения; UntilDs — конечный срок либо long.MaxValue для turn ownership.</summary>
    Stun,
    /// <summary>Изменение trace; Value — delta.</summary>
    Trace,
    /// <summary>Подготовка; UntilDs задаёт момент срабатывания.</summary>
    CastStarted,
    /// <summary>Подготовка завершена либо отменена.</summary>
    CastEnded,
    /// <summary>Уничтожение одного установленного расходника.</summary>
    ConsumeProgram,
    /// <summary>Выход из grace после принятого hostile действия.</summary>
    Reveal,
    /// <summary>Пробуждение текущего AI; Value — задержка в децисекундах.</summary>
    Wake,
    /// <summary>Изменена проекция очереди; Node определяет подписку UI.</summary>
    EncounterChanged,
    /// <summary>Очередь закрыта, turn-owned статусы сняты и обычные AI-часы могут продолжиться.</summary>
    EncounterEnded
}

/// <summary>Scalar-эффект без ссылки на actor state, executable object или combat controller.</summary>
public sealed record CombatEffect(CombatEffectKind Kind, ActorKey Actor, ActorKey? Source = null,
    double Value = 0, long UntilDs = 0, string? ProgramId = null, string? ExecutableId = null,
    NodeKey? Node = null, bool Biofeedback = false, string? Code = null);

/// <summary>Неизменяемая проекция одного участника и его own-turn cooldown ledger.</summary>
public sealed record CombatParticipantView(ActorKey Actor, int Ap, ulong OwnTurn, bool Current,
    int StunTurns, bool StunImmune, ulong ShieldUntilTurn, double ShieldFactor,
    ValueList<CombatCooldownView> Cooldowns);
/// <summary>Оставшиеся собственные ходы до доступности программы.</summary>
public sealed record CombatCooldownView(string ProgramId, ulong RemainingTurns);
/// <summary>Подготовка без native target refs; отмена и deadline принадлежат Core.</summary>
public sealed record CombatCastView(string ProgramId, ActorKey Target, long DueDs, double DamageScale);
/// <summary>Wall-clock cooldown, используемый вне turn encounter.</summary>
public sealed record CombatWallCooldownView(string ProgramId, long UntilDs);
/// <summary>Собственная status/cast проекция без второй копии integrity.</summary>
public sealed record CombatActorStatusView(ActorKey Actor, double ShieldFactor, long ShieldUntilDs, long StunUntilDs,
    long NextProgramDs, CombatCastView? Cast, ValueList<CombatWallCooldownView> Cooldowns);
/// <summary>Очередь боя с encounter-level join token и decision-level input token.</summary>
public sealed record CombatEncounterView(NodeKey Node, string DecisionId, string JoinId, ulong Round,
    int QuietRounds, ActorKey? Current, long DeadlineDs, bool Deferred, bool Advancing,
    ValueList<CombatParticipantView> Participants);

/// <summary>Контекст полиморфного эффекта; роли и metadata сами по себе эффекта не создают.</summary>
public sealed record CombatProgramContext(ActorKey Source, CombatActorFact Target, double DamageScale,
    bool TurnBased, bool TargetStunImmune, double ActiveShieldFactor, bool TargetShielded, long NowDs);
/// <summary>Описанный программой status hook; null означает отсутствие изменения.</summary>
public sealed record CombatProgramStatus(double? ShieldFactor = null, int ShieldTurns = 0, int StunTurns = 0);
/// <summary>Результат полиморфного Apply не меняет shared definition и не выполняет native эффектов.</summary>
public sealed record CombatProgramApplication(ValueList<CombatEffect> Effects, CombatProgramStatus? Status = null);
