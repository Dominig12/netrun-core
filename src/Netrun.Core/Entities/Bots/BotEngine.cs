using Netrun.Core.Entities.Combat;

namespace Netrun.Core.Entities.Bots;

/// <summary>
/// Единоличный владелец bot brain/goals. Не меняет здоровье и residence, не выполняет I/O,
/// а выдаёт stamped решения общим Core owners и native adapter. Все сроки используют simulation DS.
/// </summary>
public sealed partial class BotEngine : IDisposable
{
    /// <summary>Внутреннее состояние одной цели; serial не позволяет позднему ACK закончить следующую цель.</summary>
    private sealed class Goal(ulong serial, string kind, NodeKey? target, string? command, int expiresAt)
    {
        internal readonly ulong Serial = serial;
        internal readonly string Kind = kind;
        internal NodeKey? Target = target;
        internal readonly string? Command = command;
        internal readonly int ExpiresAt = expiresAt;
        internal bool Waiting, Scanned, Engaged, Interrupted;
        internal int ArrivedAt, DisruptAt;
        internal string? AttemptedFrontier, IncidentId;
    }

    /// <summary>По одному отменяемому simulation deadline на brain; serial упорядочивает одинаковое время FIFO.</summary>
    private sealed record ScheduledWake(BotKey Bot, int AtDs, ulong Serial);

    /// <summary>Состав brain независим от immutable EntityWorld view, которым он не владеет.</summary>
    private sealed class Brain(BotFact fact, BotProfile profile)
    {
        internal BotFact Fact = fact;
        internal readonly BotProfile Profile = profile;
        internal BotBehavior Behavior = fact.Behavior ?? new(Caution: profile.FleeRatio * 100);
        internal Dictionary<NodeKey, BotNodeFact> Nodes = [];
        internal Dictionary<NodeKey, List<NodeKey>> Adjacent = [];
        internal List<BotNodeFact> Debris = [], Servers = [], Relays = [], Fortresses = [];
        internal readonly List<Goal> Goals = [];
        internal readonly Memory<NodeKey> Visited = new(64), Blacklist = new(32);
        internal readonly Memory<ActorKey> Enemies = new(16), Warned = new(16);
        internal readonly HashSet<NodeKey> Surveyed = [];
        internal string State = "запуск";
        internal BotOrder? Order;
        internal NodeKey? Post = fact.Context.Home;
        internal ulong GoalSerial, ActionSerial;
        internal BotEffect? Pending;
        internal ulong PendingGoal;
        internal bool HandedOff;
        internal BotEffectResult? Result;
        internal BotAction? ResultAction;
        internal ulong ResultGoal;
        internal ulong? AwaitFactRevision;
        internal NodeKey[] PatrolCandidates = [];
        internal int PatrolCursor, PatrolShuffledUntil, SalvageCursor, ServerCursor, ExitCursor, FortressCursor, HackCursor;
        internal int SalvageWaitUntil, NextReport, NextWake, ExpiresAt = fact.Actor.ExpiresAtDs ?? 0;
        internal string? Objective;
        internal int Progress, Required;
        internal bool Departing, Completed, InCombat, Retired;
        internal ActorKey? CombatTarget, ControlledTarget, PendingAttack;
        internal int AttackAt;
        internal NodeKey? FleeFrom;
        internal string? ClaimedIncident;
        internal uint Random = Seed(fact.Key);
        internal ScheduledWake? Scheduled;
    }

    /// <summary>Ограниченная insertion-order память с точным удалением и обновлением, без stale-очередей.</summary>
    private sealed class Memory<T>(int capacity) where T : notnull
    {
        private readonly Dictionary<T, (int Value, LinkedListNode<T> Order)> _values = [];
        private readonly LinkedList<T> _order = [];
        internal int? Get(T key) => _values.TryGetValue(key, out var entry) ? entry.Value : null;
        internal void Put(T key, int value, bool refresh = false)
        {
            if (_values.TryGetValue(key, out var entry))
            {
                if (refresh) { _order.Remove(entry.Order); _order.AddLast(entry.Order); }
                _values[key] = (value, entry.Order);
                return;
            }
            _values.Add(key, (value, _order.AddLast(key)));
            while (_values.Count > capacity)
            {
                var oldest = _order.First!;
                _values.Remove(oldest.Value);
                _order.RemoveFirst();
            }
        }
        internal void Prune(int now)
        {
            var current = _order.First;
            while (current is not null)
            {
                var next = current.Next;
                if (_values[current.Value].Value <= now)
                { _values.Remove(current.Value); _order.Remove(current); }
                current = next;
            }
        }
    }

    private readonly BotEngineOptions _options;
    private readonly Dictionary<string, BotProfile> _profiles;
    private readonly Dictionary<string, CombatProgram> _programs;
    private readonly Dictionary<string, Brain> _brains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _generations = new(StringComparer.Ordinal);
    private readonly LinkedList<BotKey> _ready = [];
    private readonly Dictionary<BotKey, LinkedListNode<BotKey>> _readyEntries = [];
    private readonly SortedSet<ScheduledWake> _deadlines = new(Comparer<ScheduledWake>.Create((first, second) =>
        first.AtDs != second.AtDs ? first.AtDs.CompareTo(second.AtDs) : first.Serial.CompareTo(second.Serial)));
    private readonly Queue<BotEffect> _effects = [];
    private readonly Dictionary<NodeKey, int> _inspected = [];
    private readonly Dictionary<string, BotKey> _incidentClaims = new(StringComparer.Ordinal);
    private int _now;
    private ulong _wakeSerial;
    private bool _disposed;

    /// <summary>Создаёт пустой deterministic owner с точными штатными profile/program определениями.</summary>
    public BotEngine(BotEngineOptions? options = null, IEnumerable<BotProfile>? profiles = null,
        IEnumerable<CombatProgram>? programs = null)
    {
        _options = options ?? new();
        if (_options.MaxBots < 1 || _options.MaxEffects < 1 || _options.MaxTrackedIds < 1)
            throw new ArgumentException("Invalid bot options.", nameof(options));
        _profiles = (profiles ?? BotProfiles.CreateDefaults()).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        _programs = (programs ?? CombatPrograms.CreateDefaults()).ToDictionary(program => program.Id, StringComparer.Ordinal);
    }

    /// <summary>Количество действующих brain; output очередь отдельно ограничена MaxEffects.</summary>
    public int Count => _brains.Count;
    /// <summary>Непереданные эффекты, включая отозванные до handoff записи до следующего dequeue.</summary>
    public int EventCount => _effects.Count;
    /// <summary>Ближайшая работа без продвижения часов, переноса deadlines или выполнения brain.</summary>
    public int? NextWakeDs => _ready.Count > 0 ? _now : _deadlines.Min?.AtDs;

    /// <summary>Неизменяемое определение профиля для проверки Core configuration; loadout этим не выдаётся.</summary>
    public BotProfile? ReadProfile(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _profiles.GetValueOrDefault(id);
    }

    /// <summary>
    /// Только выбирает следующую готовую работу для admission в общую Core FIFO.
    /// Не изменяет clock, ready queue или deadline и не начинает вычисление brain.
    /// </summary>
    public BotKey? NextReadyBot(int simulationTimeDs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (simulationTimeDs < _now) throw new ArgumentOutOfRangeException(nameof(simulationTimeDs));
        if (_ready.First is { } ready) return ready.Value;
        return _deadlines.Min is { } due && due.AtDs <= simulationTimeDs ? due.Bot : null;
    }

    /// <summary>Применяет новую immutable публикацию; старые поколения/revisions и capacity отвергаются без мутации.</summary>
    public bool Upsert(BotFact fact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(fact);
        if (!ValidKey(fact.Key.Id, fact.Key.Generation) || fact.Revision == 0 ||
            fact.Actor.Kind != EntityKind.Bot || !ValidKey(fact.Actor.Actor.Id, fact.Actor.Actor.Generation) ||
            !_profiles.TryGetValue(fact.ProgramId, out var profile) || !ValidBehavior(fact.Behavior) ||
            fact.Context.Nodes.Any(node => node.Salvage.Count > 12))
            return false;
        var nodes = new Dictionary<NodeKey, BotNodeFact>();
        foreach (var node in fact.Context.Nodes)
            if (!nodes.TryAdd(node.Key, node)) return false;
        if (_brains.TryGetValue(fact.Key.Id, out var brain))
        {
            if (fact.Key.Generation < brain.Fact.Key.Generation) return false;
            if (fact.Key == brain.Fact.Key)
            {
                if (fact.Revision <= brain.Fact.Revision || fact.Actor.Actor != brain.Fact.Actor.Actor ||
                    fact.Actor.Revision < brain.Fact.Actor.Revision || fact.ProgramId != brain.Fact.ProgramId)
                    return false;
                var debrisChanged = !brain.Fact.Context.Nodes.Where(node => node.Salvage.Count > 0)
                    .SequenceEqual(fact.Context.Nodes.Where(node => node.Salvage.Count > 0));
                brain.Fact = fact;
                brain.Nodes = nodes;
                if (debrisChanged && brain.Goals.FirstOrDefault()?.Kind == "recycling_wait") ClearGoals(brain);
                if (fact.Behavior is { } behavior && brain.Behavior != behavior)
                {
                    if (brain.Behavior.Strategy != behavior.Strategy) ClearGoals(brain);
                    brain.Behavior = behavior;
                }
                if (brain.AwaitFactRevision < fact.Revision) brain.AwaitFactRevision = null;
                BuildIndexes(brain);
                AdmitWake(brain);
                return true;
            }
            Remove(brain.Fact.Key);
        }
        if (_generations.TryGetValue(fact.Key.Id, out var generation) && fact.Key.Generation <= generation)
            return false;
        if (_brains.Count >= _options.MaxBots || (!_generations.ContainsKey(fact.Key.Id) && _generations.Count >= _options.MaxTrackedIds))
            return false;
        brain = new(fact, profile) { Nodes = nodes, NextWake = _now };
        if (profile.Planner is "survey" or "collect" or "hack") brain.Order = new("patrol");
        if (fact.Context.Visitor)
        {
            brain.Objective = fact.Actor.FactionId == "redshift" ? "sabotage" : profile.Planner == "scavenge" ? "salvage" : "survey";
            brain.Required = brain.Objective == "sabotage" ? 1 : brain.Objective == "salvage" ? 2 : fact.Actor.FactionId == "collective" ? 4 : 3;
        }
        BuildIndexes(brain);
        _brains.Add(fact.Key.Id, brain);
        _generations[fact.Key.Id] = fact.Key.Generation;
        AdmitWake(brain);
        return true;
    }

    /// <summary>Удаляет только точное поколение; поздний результат больше не может оживить brain.</summary>
    public bool Remove(BotKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Find(key, out var brain)) return false;
        ReleaseIncident(brain);
        if (brain.Scheduled is { } scheduled) _deadlines.Remove(scheduled);
        if (_readyEntries.Remove(key, out var entry)) _ready.Remove(entry);
        return _brains.Remove(key.Id);
    }

    /// <summary>Сохраняет explicit order и bounded цель; аутентификацию owner делает вызывающий Core слой.</summary>
    public bool SetOrder(BotKey key, BotOrder order, int simulationTimeDs)
    {
        Clock(simulationTimeDs);
        if (!Find(key, out var brain) || order.Kind is not ("recall" or "terminate" or "guard" or "collect" or "patrol" or "command" or "hack"))
            return false;
        BotNodeFact? target = order.Target is { } node ? Node(brain, node) : null;
        if (order.Kind is "guard" or "collect" or "command" or "hack" && target is null) return false;
        if (order.Kind is "guard" or "collect" && (!target!.CanRoute || target.AccessLevel < 2)) return false;
        if (order.Kind == "command" && (string.IsNullOrEmpty(order.CommandId) || !target!.Commands.Contains(order.CommandId))) return false;
        if (order.Kind == "hack" && (!brain.Fact.Context.CanHack || !target!.CanRoute ||
            (order.CommandId is { } command && !target.Commands.Contains(command)))) return false;
        if (order.Kind == "collect" && BestSalvage(brain, target!) is null) return false;
        ClearGoals(brain);
        brain.Behavior = brain.Behavior with { Strategy = "profile" };
        if (order.Kind is "guard" or "collect") brain.Post = order.Target;
        brain.Order = order.Kind is "collect" or "hack" ? new("guard", order.Target) : order;
        switch (order.Kind)
        {
            case "recall": Push(brain, "recall", null, null, 600); break;
            case "terminate": Push(brain, "terminate", null, null, 1200); break;
            case "guard": Push(brain, "guard", order.Target, null, 300); break;
            case "collect": Push(brain, "salvage", order.Target, null, 900); break;
            case "patrol": Push(brain, "patrol", null, null, 900); break;
            case "command": Push(brain, "command", order.Target, order.CommandId, 1200); break;
            case "hack": Push(brain, "hack", order.Target, order.CommandId, 3000); break;
        }
        AdmitWake(brain);
        return true;
    }

    /// <summary>FIFO admitted wake tasks; завершение ожидания создаёт новую задачу, а не удерживает worker.</summary>
    public int Advance(int simulationTimeDs, int workBudget)
    {
        Clock(simulationTimeDs);
        ArgumentOutOfRangeException.ThrowIfNegative(workBudget);
        for (var admitted = 0; admitted < workBudget && _deadlines.Min is { } due && due.AtDs <= _now; admitted++)
        {
            _deadlines.Remove(due);
            if (Find(due.Bot, out var waiting) && waiting.Scheduled == due)
            { waiting.Scheduled = null; AdmitWake(waiting); }
        }
        var steps = Math.Min(workBudget, _ready.Count);
        for (var index = 0; index < steps; index++)
        {
            var key = _ready.First!.Value;
            _ready.RemoveFirst();
            _readyEntries.Remove(key);
            if (!Find(key, out var brain)) continue;
            RunBrain(brain);
        }
        return steps;
    }

    /// <summary>
    /// Выполняет одну уже принятую общим Runtime FIFO работу точного поколения.
    /// Давление output сохраняет ready/deadline; другие боты и их порядок не изменяются.
    /// </summary>
    public int AdvanceBot(BotKey key, int simulationTimeDs)
    {
        Clock(simulationTimeDs);
        if (!Find(key, out var brain)) return 0;
        var ready = _readyEntries.TryGetValue(key, out var entry);
        var due = brain.Scheduled is { } scheduled && scheduled.AtDs <= _now;
        if (!ready && !due || !brain.Retired && _effects.Count >= _options.MaxEffects) return 0;
        if (ready)
        {
            _ready.Remove(entry!);
            _readyEntries.Remove(key);
        }
        if (brain.Scheduled is { } previous)
        {
            _deadlines.Remove(previous);
            brain.Scheduled = null;
        }
        RunBrain(brain);
        return 1;
    }

    /// <summary>Общий single-brain dispatcher для standalone и Runtime FIFO adapters.</summary>
    private void RunBrain(Brain brain)
    {
        if (brain.Retired) return;
        Tick(brain);
        Schedule(brain);
    }

    /// <summary>Передаёт эффект единожды, пропуская отменённые до handoff entries.</summary>
    public bool TryDequeueEffect(out BotEffect effect)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_effects.TryDequeue(out var next))
        {
            if (!Find(next.Bot, out var brain) || brain.Pending?.ActionSerial != next.ActionSerial) continue;
            brain.HandedOff = true;
            effect = next;
            return true;
        }
        effect = null!;
        return false;
    }

    /// <summary>Принимает только собственный causal ACK; terminal response не читается из чужих node journals.</summary>
    public bool ApplyResult(BotEffectResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Find(result.Bot, out var brain) || brain.Pending is not { } pending || !brain.HandedOff ||
            result.Actor != pending.Actor || result.ActionSerial != pending.ActionSerial || result.ExpectedRevision != pending.ExpectedRevision ||
            result.Phase is not ("queued" or "invoked" or "completed" or "rejected" or "cancelled" or "uncertain"))
            return false;
        if (result.Phase is "queued" or "invoked") return true;
        brain.Result = result;
        brain.ResultAction = pending.Action;
        brain.ResultGoal = brain.PendingGoal;
        brain.Pending = null;
        brain.HandedOff = false;
        AdmitWake(brain);
        if (pending.Action is BotNavigate or BotRecover or BotCombat or BotSabotage or BotIncident || result.Phase == "uncertain")
            brain.AwaitFactRevision = brain.Fact.Revision;
        return true;
    }

    /// <summary>Читает read-model без планирования и выполнения действий.</summary>
    public BotView? Query(BotKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Find(key, out var brain)) return null;
        return new(key, brain.Fact.Actor.Actor, brain.Fact.Revision, brain.State, brain.Order?.Kind,
            new(brain.Goals.Select(goal => new BotGoalView(goal.Serial, goal.Kind, goal.Target, goal.Command, goal.ExpiresAt, goal.Waiting)), 4),
            brain.Pending, brain.Departing, brain.Completed, brain.Objective, brain.Progress, brain.Required,
            brain.NextWake == int.MaxValue ? null : brain.NextWake);
    }

    /// <summary>Первый hop source-tree к узлу для Core-инициированного пошагового движения; не планирует и не думает.</summary>
    public NodeKey? FirstHop(BotKey key, NodeKey destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Find(key, out var brain) ? Node(brain, destination)?.FirstHop : null;
    }

    /// <summary>Trusted combat event помечает exact attacker generation; текст сообщения приказом не является.</summary>
    public bool RememberAttacker(BotKey key, ActorKey attacker)
    {
        if (!Find(key, out var brain) || attacker == brain.Fact.Actor.Actor) return false;
        brain.Enemies.Put(attacker, Later(600));
        AdmitWake(brain);
        return true;
    }

    /// <summary>Trusted completion/signal создаёт одну FIFO task; повторный сигнал коалесцируется.</summary>
    public bool Wake(BotKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Find(key, out var brain)) return false;
        AdmitWake(brain);
        return true;
    }

    /// <summary>Не выполняет native teardown; отключённый owner теряет все staged решения.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _brains.Clear(); _generations.Clear(); _ready.Clear(); _readyEntries.Clear(); _deadlines.Clear(); _effects.Clear(); _inspected.Clear(); _incidentClaims.Clear();
    }

    /// <summary>Один actor input создаёт индексы; think не пересканирует registry станции.</summary>
    private static void BuildIndexes(Brain brain)
    {
        brain.Debris = []; brain.Servers = []; brain.Relays = []; brain.Fortresses = [];
        foreach (var node in brain.Fact.Context.Nodes)
        {
            if (node.Salvage.Count > 0) brain.Debris.Add(node);
            if (node.Type == "server") brain.Servers.Add(node);
            if (node.Type == "relay") brain.Relays.Add(node);
            if (node.FortressFaction is not null) brain.Fortresses.Add(node);
        }
        brain.Adjacent = [];
        foreach (var edge in brain.Fact.Context.Links)
        {
            if (!edge.Available) continue;
            if (!brain.Adjacent.TryGetValue(edge.From, out var from)) brain.Adjacent[edge.From] = from = [];
            if (!brain.Adjacent.TryGetValue(edge.To, out var to)) brain.Adjacent[edge.To] = to = [];
            from.Add(edge.To); to.Add(edge.From);
        }
    }

    /// <summary>Сроки saturate, не переполняют simulation clock.</summary>
    private int Later(int duration) => (int)Math.Min(int.MaxValue, (long)_now + duration);
    /// <summary>Логическое время никогда не возвращается назад.</summary>
    private void Clock(int now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (now < _now) throw new ArgumentOutOfRangeException(nameof(now));
        _now = now;
    }
    /// <summary>Ключ проверяется до доступа к registry и causal stamps.</summary>
    private static bool ValidKey(string id, ulong generation) => generation > 0 && id is { Length: > 0 and <= 128 } && !id.Any(char.IsControl);
    /// <summary>Настройки изменяются атомарно после проверки всего snapshot.</summary>
    private static bool ValidBehavior(BotBehavior? value) => value is null ||
        value.Strategy is "profile" or "patrol" or "scavenge" or "guard" &&
        double.IsFinite(value.Aggression) && value.Aggression is >= 0 and <= 100 &&
        double.IsFinite(value.Caution) && value.Caution is >= 0 and <= 100;
    /// <summary>Чтение registry не разрешает recycled BotKey в новую жизнь.</summary>
    private bool Find(BotKey key, out Brain brain)
    {
        if (_brains.TryGetValue(key.Id, out var current) && current.Fact.Key == key) { brain = current; return true; }
        brain = null!; return false;
    }
    /// <summary>Точное поколение node остаётся частью любого решения.</summary>
    private static BotNodeFact? Node(Brain brain, NodeKey? key) => key is not null ? brain.Nodes.GetValueOrDefault(key) : null;
    /// <summary>Детерминированный seed не зависит от randomized string.GetHashCode и wall-clock.</summary>
    private static uint Seed(BotKey key)
    {
        var value = 2166136261u;
        foreach (var character in key.Id) value = unchecked((value ^ character) * 16777619);
        return (value ^ (uint)key.Generation ^ (uint)(key.Generation >> 32)) | 1u;
    }
    /// <summary>Lazy Fisher-Yates и cadence используют явное per-brain PRNG состояние.</summary>
    private static int Random(Brain brain, int minimum, int exclusiveMaximum)
    {
        var value = brain.Random;
        value ^= value << 13; value ^= value >> 17; value ^= value << 5;
        brain.Random = value;
        return minimum + (int)(value % (uint)(exclusiveMaximum - minimum));
    }

    /// <summary>Коалесцирует события до одной FIFO записи и отменяет прежний deadline без stale heap growth.</summary>
    private void AdmitWake(Brain brain)
    {
        if (brain.Scheduled is { } scheduled) _deadlines.Remove(scheduled);
        brain.Scheduled = null;
        brain.NextWake = _now;
        if (!_readyEntries.ContainsKey(brain.Fact.Key))
            _readyEntries.Add(brain.Fact.Key, _ready.AddLast(brain.Fact.Key));
    }
}
