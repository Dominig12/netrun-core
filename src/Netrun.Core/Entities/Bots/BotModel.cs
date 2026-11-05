using Netrun.Core.Entities.Combat;

namespace Netrun.Core.Entities.Bots;

/// <summary>Идентичность мозга; повторный launch использует новое поколение и не наследует pending команду.</summary>
public sealed record BotKey(string Id, ulong Generation);

/// <summary>Проверенная настройка portable temperament; scope и права не выдаёт.</summary>
public sealed record BotBehavior(string Strategy = "profile", double Aggression = 100,
    double Caution = 30, bool Cooperation = true);

/// <summary>Факт доступного файла; UID принадлежит существующему actor inventory либо salvage cache.</summary>
public sealed record BotFileFact(string Uid, string Kind, int Size, bool Transferable = true,
    bool Fits = true, bool FortressReward = false, int RecycleAfterDs = 0);

/// <summary>Доступный executable и его готовность; metadata берётся из общего CombatPrograms.</summary>
public sealed record BotExecutable(string ProgramId, string ExecutableId, int ReadyAtDs = 0, ulong CooldownTurns = 0);

/// <summary>
/// Снимок узла для brain policy. ACL/стоимости/Frontier вычисляются Core access/navigation owner;
/// native поставляет только физические факты. Ни один флаг не является разрешением исполнить эффект в DM.
/// </summary>
public sealed record BotNodeFact(NodeKey Key, string? NetworkId, string Type)
{
    /// <summary>Текущее питание и native availability.</summary>
    public bool Online { get; init; } = true;
    /// <summary>Изолированный сегмент не участвует в обычном station patrol.</summary>
    public bool Isolated { get; init; }
    /// <summary>Физически перемещаемые endpoints не являются patrol/server целями.</summary>
    public bool Dynamic { get; init; }
    /// <summary>Принадлежность к station-level, нужна objective и recycle policy.</summary>
    public bool Station { get; init; } = true;
    /// <summary>Работающее физическое relay с открытыми внешними вратами.</summary>
    public bool RelayEntryAvailable { get; init; }
    /// <summary>Локальное station activity из журнала фактов, не чтение диска.</summary>
    public int LastActivityDs { get; init; } = int.MinValue;
    /// <summary>Уровень текущего principal в Core authority owner.</summary>
    public int AccessLevel { get; init; }
    /// <summary>Уровень LAN текущего principal.</summary>
    public int NetworkLevel { get; init; }
    /// <summary>Прямой hop допустим по снимку Core; конечный adapter снова проверяет native поколения.</summary>
    public bool DirectEntry { get; init; }
    /// <summary>Node входит в допустимую маршрутную область principal.</summary>
    public bool CanTraverse { get; init; }
    /// <summary>Посещение можно планировать, включая честный frontier hack посетителя.</summary>
    public bool CanPlanVisit { get; init; }
    /// <summary>Core source-tree достигает узла.</summary>
    public bool CanRoute { get; init; }
    /// <summary>Стоимость по source-tree для ранжирования равного salvage.</summary>
    public double RouteCost { get; init; } = double.PositiveInfinity;
    /// <summary>Первый hop source-tree к узлу (сосед текущего узла); null для себя и недостижимых. Нужен Core для пошагового движения к далёкой цели.</summary>
    public NodeKey? FirstHop { get; init; }
    /// <summary>Первый frontier на существующем пути к этому узлу.</summary>
    public NodeKey? Frontier { get; init; }
    /// <summary>Сетевые врата запрещают текущий breach.</summary>
    public bool BreachBlocked { get; init; }
    /// <summary>На узле есть ICE; raw count не выбирает цели боя.</summary>
    public int IceCount { get; init; }
    /// <summary>По Core ICE access policy сосед опасен именно этому principal.</summary>
    public bool HostileIce { get; init; }
    /// <summary>Есть процессы, враждебные по актуальным Core faction/access правилам.</summary>
    public bool HasEnemy { get; init; }
    /// <summary>Фракция существующей крепости.</summary>
    public string? FortressFaction { get; init; }
    /// <summary>Число действительно повреждённых модулей.</summary>
    public int DamagedFortressModules { get; init; }
    /// <summary>Период ordinary fort_repair.</summary>
    public int NextRepairDs { get; init; }
    /// <summary>Native device command allowlist узла.</summary>
    public ValueList<string> Commands { get; init; } = new([]);
    /// <summary>Salvage cache, не приватный machine disk.</summary>
    public ValueList<BotFileFact> Salvage { get; init; } = new([]);
    /// <summary>Существующий сервер принимает конкретный первый cargo файл.</summary>
    public bool AcceptsCargo { get; init; }
    /// <summary>Physical/critical-machine facts допускают подготовку sabotage.</summary>
    public bool RaidEligible { get; init; }
    /// <summary>До этого срока узел/APC нельзя снова использовать для sabotage.</summary>
    public int RaidReadyAtDs { get; init; }
    /// <summary>Открытый remote breach в отношении узла/LAN требует countertrace.</summary>
    public bool HasCountertraceTargets { get; init; }
}

/// <summary>Физическое соседство Core graph для bounded tactical BFS; relay buckets передаются явно отдельно.</summary>
public sealed record BotLink(NodeKey From, NodeKey To, bool Available = true);

/// <summary>Локальная perception без native ссылок; faction/contact вычисляет общий Core owner.</summary>
public sealed record BotTargetFact(CombatActorFact Actor, string FactionId, string? PrincipalId = null,
    bool FactionHostile = false, bool DefenceThreat = true, int AccessLevel = 0,
    bool IsBot = false, bool ContactAllowed = true, bool IceIntruder = false, bool IceAttackingSelf = false,
    bool PendingProgram = false, bool StunImmune = false, string? FortressModuleKind = null,
    bool Online = true, int IgnoreUntilDs = 0);

/// <summary>Открытый security incident: brain выбирает и держит claim, native не выбирает расследование.</summary>
public sealed record BotIncidentFact(string Id, NodeKey? Node, int Severity, int Reports,
    ActorKey? Suspect = null, BotKey? ClaimedBy = null, bool Open = true);

/// <summary>Неизменяемые дополнительные факты capabilities; здоровье и residence принадлежат EntityView.</summary>
public sealed record BotContext
{
    /// <summary>Существует native console representation.</summary>
    public bool ConsoleAvailable { get; init; } = true;
    /// <summary>Потеря owner principal делает owned agent orphan.</summary>
    public bool Orphaned { get; init; }
    /// <summary>Owned agent, в отличие от ambient visitor.</summary>
    public bool LaunchedByOwner { get; init; }
    /// <summary>Visitor membership выводится из Core faction arrival policy.</summary>
    public bool Visitor { get; init; }
    /// <summary>Сущность ожидает доставку recall file.</summary>
    public bool ReturnPending { get; init; }
    /// <summary>Обратный путь до owner существует по Core source-tree.</summary>
    public bool OwnerReachable { get; init; }
    /// <summary>Owner representation ещё существует.</summary>
    public bool OwnerAvailable { get; init; } = true;
    /// <summary>Трассировка заблокировала текущую точку.</summary>
    public bool Locked { get; init; }
    /// <summary>Установленный goal capability не отключён.</summary>
    public bool GoalsEnabled { get; init; } = true;
    /// <summary>autobreach capability установлен, включён и допускает NPC input.</summary>
    public bool CanHack { get; init; }
    /// <summary>Существующая minigame ожидает сигнал окончания.</summary>
    public bool BreachActive { get; init; }
    /// <summary>Обычный console command cooldown.</summary>
    public int NextCommandDs { get; init; }
    /// <summary>Внеочередной combat cooldown.</summary>
    public int NextProgramDs { get; init; }
    /// <summary>Station jurisdiction либо null для default home LAN.</summary>
    public ValueList<string>? Jurisdiction { get; init; }
    /// <summary>Исходный домашний узел.</summary>
    public NodeKey? Home { get; init; }
    /// <summary>Core route/authority/perception facts, с собственным порядком индекса.</summary>
    public ValueList<BotNodeFact> Nodes { get; init; } = new([]);
    /// <summary>Core physical graph без полного materialized quantum clique.</summary>
    public ValueList<BotLink> Links { get; init; } = new([]);
    /// <summary>Core combat perception текущего контакта.</summary>
    public ValueList<BotTargetFact> Targets { get; init; } = new([]);
    /// <summary>Действительно установленные executable; loadout profile сам по себе их не создаёт.</summary>
    public ValueList<BotExecutable> Executables { get; init; } = new([]);
    /// <summary>Действительно recovered cargo; установленные loadout-файлы сюда не входят.</summary>
    public ValueList<BotFileFact> Cargo { get; init; } = new([]);
    /// <summary>Текущее состояние Core CombatSimulation.</summary>
    public CombatEncounterView? Encounter { get; init; }
    /// <summary>Открытые incident facts, упорядоченные security owner.</summary>
    public ValueList<BotIncidentFact> Incidents { get; init; } = new([]);
    /// <summary>Общий sabotage cooldown.</summary>
    public int NextInvasionDs { get; init; }
    /// <summary>Число действующих sabotage lease во всём Core.</summary>
    public int ActiveInvasions { get; init; }
}

/// <summary>Версионированная публикация brain input; HP/status/residence — только immutable снимок общего EntityWorld.</summary>
public sealed record BotFact(BotKey Key, ulong Revision, EntityView Actor, string ProgramId,
    BotContext Context, BotBehavior? Behavior = null);

/// <summary>Адресный приказ, уже аутентифицированный owner-слоем; мозг повторно проверяет форму и capabilities.</summary>
public sealed record BotOrder(string Kind, NodeKey? Target = null, string? CommandId = null);

/// <summary>Закрытая поверхность решений; adapter преобразует их в EntityWorld/Combat/transport операции.</summary>
public abstract record BotAction;
/// <summary>Следовать к цели через общий Core navigation, без DM поиска пути.</summary>
public sealed record BotNavigate(NodeKey Destination, bool Direct) : BotAction;
/// <summary>Одна ordinary команда с обычным delivery-time ACL.</summary>
public sealed record BotCommand(NodeKey Node, string CommandId, string Nonce, string? FileUid = null) : BotAction;
/// <summary>Поднять существующий salvage файл по UID.</summary>
public sealed record BotRecover(NodeKey Node, string FileUid) : BotAction;
/// <summary>Возвратить owned agent как существующий файл.</summary>
public sealed record BotRecall : BotAction;
/// <summary>Удалить процесс с обычными правилами сохранения настоящего inventory.</summary>
public sealed record BotDissolve(string Code) : BotAction;
/// <summary>Выйти только через реально доступное физическое relay.</summary>
public sealed record BotLeaveNetwork : BotAction;
/// <summary>Запустить честный ordinary breach, не выдавая прав.</summary>
public sealed record BotStartBreach(NodeKey Frontier, string Kind, string TargetId) : BotAction;
/// <summary>Отозвать только ранее выданную операцию, без повторения mutation.</summary>
public sealed record BotCancelAction(ulong ActionSerial) : BotAction;
/// <summary>Передать тактическое решение общему CombatSimulation.</summary>
public sealed record BotCombat(CombatIntent Intent) : BotAction;
/// <summary>Предупредить живого игрока до realtime атаки либо полицейского подавления.</summary>
public sealed record BotWarning(ActorKey Target, string Code, int DeadlineDs) : BotAction;
/// <summary>Сохранить только ответ доставленной команды; callback не читает machine journal напрямую.</summary>
public sealed record BotStoreReport(string Name, string Text, int MaximumReports = 4) : BotAction;
/// <summary>Зарегистрировать/закрыть инцидент в общем Core security owner.</summary>
public sealed record BotIncident(string Kind, NodeKey? Node, ActorKey? Suspect, string? IncidentId = null,
    string? Resolution = null) : BotAction;
/// <summary>Начать/применить sabotage lease; реальный APC/telecomms эффект остаётся native adapter.</summary>
public sealed record BotSabotage(NodeKey Node, bool Apply, int DisruptAtDs) : BotAction;

/// <summary>Ровно одно решение с causal stamps; serial не повторяется в жизни BotKey.</summary>
public sealed record BotEffect(BotKey Bot, ActorKey Actor, ulong ActionSerial, ulong ExpectedRevision,
    ulong ResidenceRevision, NodeKey Source, BotAction Action, int DeadlineDs);

/// <summary>ACK либо terminal outcome; phase: queued/invoked/completed/rejected/cancelled/uncertain.</summary>
public sealed record BotEffectResult(BotKey Bot, ActorKey Actor, ulong ActionSerial,
    ulong ExpectedRevision, string Phase, string? Response = null);

/// <summary>Неизменяемый read-model; mutable brain/goals наружу не выдаются.</summary>
public sealed record BotView(BotKey Bot, ActorKey Actor, ulong Revision, string State, string? Order,
    ValueList<BotGoalView> Goals, BotEffect? PendingEffect, bool VisitorDeparting, bool VisitorCompleted,
    string? VisitorObjective, int VisitorProgress, int VisitorRequired, int? NextWakeDs);
/// <summary>Снимок абсолютного срока и целевого поколения одной цели.</summary>
public sealed record BotGoalView(ulong Serial, string Kind, NodeKey? Target, string? CommandId,
    int ExpiresAtDs, bool Waiting);

/// <summary>Bounded actor/output slots; один Advance work unit обслуживает одну FIFO wake task.</summary>
public sealed record BotEngineOptions(int MaxBots = int.MaxValue, int MaxEffects = int.MaxValue,
    int MaxTrackedIds = int.MaxValue, BotTuningOptions? Tuning = null);

/// <summary>Сроки преследования цели по типу (децисекунды); штатные значения из DM brain/goals.</summary>
public sealed record BotGoalTimings(
    int Guard = 300, int Patrol = 900, int Salvage = 900, int Travel = 900,
    int Repair = 1200, int Survey = 1200, int Hack = 3000, int Invasion = 3000,
    int Investigate = 1800, int PolicePatrol = 1200, int RecycleDeposit = 1200,
    int Depart = 900, int Recall = 600, int Terminate = 1200, int Command = 1200,
    int RecycleWait = 600);

/// <summary>Длительности памяти, окон предупреждения и дефолтных сроков бота (децисекунды).</summary>
public sealed record BotMemoryTimings(
    int HopBlacklist = 1200,            // узел без маршрута/проваленный переход
    int HackBlacklist = 600,            // проваленный frontier-взлом или отказ разгрузки
    int FleeBlacklist = 300,            // покинутый при отступлении узел
    int EnemyMemory = 600,              // срок удержания противника в памяти
    int DefaultActionDeadline = 300,    // дефолтный срок исполнения действия
    int GraceWindow = 900,              // grace-окно визитёра перед принудительным уходом
    int RogueReportCooldown = 200,      // пауза между повторными rogue-инцидентами
    int InvestigationSettle = 80,       // тихое ожидание отсутствия нарушителя
    int AttackTelegraph = 20,           // предупреждение игроку до realtime-атаки
    int PoliceWarning = 50);            // окно police_leave и порог эскалации

/// <summary>Боевые пороги бота: доли integrity и запасной power программы.</summary>
public sealed record BotCombatTuning(
    double ShieldIntegrityRatio = 0.5,  // ниже — ставить щит
    double RepairIntegrityRatio = 0.4,  // ниже — чиниться
    double StrikePowerFallback = 22);   // запасная мощь strike, если программа не загружена

/// <summary>Веса ценности salvage по типу файла; качество доминирует над размером.</summary>
public sealed record BotSalvageWeights(
    int Program = 180, int Daemon = 140, int Bot = 160, int Key = 120,
    int Techweb = 80, int Other = 10, int FortressBonus = 70, int SizeCap = 20);

/// <summary>
/// Настраиваемый тюнинг brain-owner. Штатные значения точно повторяют DM brain/goals,
/// поэтому конфигурация по умолчанию не меняет поведения AI. Структурные границы обхода
/// (BFS-лимиты, размер очереди целей) остаются фиксированными детерминированными константами.
/// </summary>
public sealed record BotTuningOptions(
    BotGoalTimings? Goals = null,
    BotMemoryTimings? Memory = null,
    BotCombatTuning? Combat = null,
    BotSalvageWeights? Salvage = null,
    int MaxActiveInvasions = 2,
    int SabotageHoldDs = 300,
    int IncidentClaimMinSeverity = 2,
    int IncidentClaimMinReports = 2,
    int ThinkJitterDs = 5,
    int ReportTruncateChars = 1000);
