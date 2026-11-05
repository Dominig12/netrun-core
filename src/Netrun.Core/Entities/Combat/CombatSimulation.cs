using System.Globalization;

namespace Netrun.Core.Entities.Combat;

/// <summary>Единственный последовательный owner очередей боя; не содержит DM/native callbacks и не владеет HP registry.</summary>
public sealed class CombatSimulation
{
    /// <summary>Фундаментальная const turn_combat.dm: три AP на ход (используется в compile-time паттернах).</summary>
    public const int MaximumAp = 3;
    /// <summary>Настраиваемые тайминги; штатные значения повторяют turn_combat.dm (двенадцать секунд, три тихих раунда).</summary>
    private readonly CombatOptions _options;

    private sealed class Actor(CombatActorFact fact)
    {
        internal CombatActorFact Fact = fact;
        internal Encounter? Encounter;
        internal int CarriedAp = -1;
        internal long CarriedUntil;
        internal long NextProgram;
        internal readonly Dictionary<string, long> WallCooldowns = new(StringComparer.Ordinal);
        internal double ShieldFactor = fact.InitialShieldFactor;
        internal long ShieldUntil = fact.InitialShieldUntilDs, StunUntil = fact.InitialStunUntilDs;
        internal Cast? Cast;
        internal ulong CastGeneration;
    }

    private sealed class Participant(Actor actor)
    {
        internal readonly Actor Actor = actor;
        internal int Ap, StunTurns;
        internal ulong OwnTurn, ShieldUntilTurn;
        internal double ShieldFactor = 1;
        internal bool StunImmune, RealTurn;
        internal readonly Dictionary<string, ulong> Cooldowns = new(StringComparer.Ordinal);
    }

    private sealed class Encounter(NodeKey node, ulong serial)
    {
        internal readonly NodeKey Node = node;
        internal readonly ulong Serial = serial;
        internal readonly List<Participant> Participants = [];
        internal int CurrentIndex = -1, QuietRounds, AdvanceRemaining;
        internal ulong Round = 1, Generation;
        internal bool Active, Deferred, Advancing, Closed, RoundHadCombat;
        internal long Deadline;
        internal Participant? Current => CurrentIndex >= 0 && CurrentIndex < Participants.Count ? Participants[CurrentIndex] : null;
        internal string DecisionId => Serial.ToString(CultureInfo.InvariantCulture) + ":" + Generation.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record Cast(CombatProgram Program, ActorKey Target, double DamageScale, long Due,
        ulong Generation, string? ExecutableId);
    private sealed record Scheduled(Encounter? Encounter, ActorKey? Actor, ulong Generation);
    private readonly Dictionary<ActorKey, Actor> _actors = [];
    private readonly Dictionary<NodeKey, Encounter> _encounters = [];
    private readonly Dictionary<string, CombatProgram> _programs = new(StringComparer.Ordinal);
    private readonly Dictionary<ActorKey, HashSet<ActorKey>> _targetCasts = [];
    private readonly Queue<CombatEffect> _effects = [];
    private readonly Queue<Encounter> _advancing = [];
    private readonly PriorityQueue<Scheduled, (long Due, ulong Serial)> _scheduled = new();
    private ulong _encounterSerial, _scheduleSerial;
    private long _now;

    /// <summary>Создаёт изолированную симуляцию; explicit definitions могут включать доверенные полиморфные программы.</summary>
    public CombatSimulation(IEnumerable<CombatProgram>? programs = null, CombatOptions? options = null)
    {
        _options = options ?? new();
        if (_options.DecisionTimeDs < 1 || _options.IdleRounds < 1 || _options.TelegraphInCombatDs < 0 ||
            _options.TelegraphMinDs < 0 || _options.GlobalProgramCooldownDs < 0 || _options.EntityWakeDs < 0 ||
            _options.AdvancePassesPerParticipant < 1 || !double.IsFinite(_options.MaximumDamageScale) || _options.MaximumDamageScale < 1)
            throw new ArgumentException("Invalid combat options.", nameof(options));
        foreach (var program in programs ?? CombatPrograms.CreateDefaults())
        {
            if (string.IsNullOrWhiteSpace(program.Id) || program.Id.Length > 128 ||
                program.ApCost is < 1 or > MaximumAp || program.CooldownTurns < 0 || program.CooldownDs < 0 ||
                program.ShieldTurns < 0 || program.StunTurns < 0 || program.WindupDs < 0 ||
                !double.IsFinite(program.Power) || program.Power < 0 || !_programs.TryAdd(program.Id, program))
                throw new ArgumentException("Invalid combat program.", nameof(programs));
        }
    }

    /// <summary>Число живых локальных encounter; не включает closed work/timer tombstones.</summary>
    public int EncounterCount => _encounters.Count;
    /// <summary>Количество ещё не применённых scalar-эффектов.</summary>
    public int EffectCount => _effects.Count;
    /// <summary>Число известных боевых actor generations.</summary>
    public int ActorCount => _actors.Count;

    /// <summary>Обновляет immutable facts; смена anchor/валидности немедленно отзывает старое членство и подготовку.</summary>
    public void UpsertActor(CombatActorFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.Key is null || string.IsNullOrWhiteSpace(fact.Key.Id) || fact.Key.Id.Length > 128 || fact.Key.Generation == 0 ||
            fact.Node is null || fact.Node.Generation == 0 || string.IsNullOrWhiteSpace(fact.Node.NodeId) ||
            !double.IsFinite(fact.Integrity) || !double.IsFinite(fact.MaxIntegrity) || fact.MaxIntegrity <= 0 ||
            !double.IsFinite(fact.InitialShieldFactor) || fact.InitialShieldFactor is < 0.1 or > 1 || fact.Programs is null)
            throw new ArgumentException("Invalid combat actor.", nameof(fact));
        if (!_actors.TryGetValue(fact.Key, out var actor))
        {
            _actors.Add(fact.Key, new(fact));
            return;
        }
        var departed = actor.Fact.Node != fact.Node || !fact.Valid || !fact.TakesTurns || fact.Integrity <= 0;
        actor.Fact = fact;
        if (departed)
        {
            CancelCast(actor, resolve: false);
            InvalidateTargetCasts(fact.Key);
            if (actor.Encounter is { } encounter) Remove(encounter, actor, leaving: true);
        }
        // В encounter статусы принадлежат own-turn ledger, а finite/native facts не могут отозвать этот ownership.
        var member = actor.Encounter is { } current ? Member(current, actor) : null;
        if (member is null || member.ShieldUntilTurn == 0)
        {
            actor.ShieldFactor = fact.InitialShieldFactor;
            actor.ShieldUntil = fact.InitialShieldUntilDs;
        }
        if (member is null || member.StunTurns == 0) actor.StunUntil = fact.InitialStunUntilDs;
    }

    /// <summary>Удаление конкретного поколения не затрагивает новый actor с тем же opaque Id.</summary>
    public void RemoveActor(ActorKey actor, long nowDs)
    {
        if (!SetTime(nowDs)) throw new ArgumentOutOfRangeException(nameof(nowDs));
        if (!_actors.TryGetValue(actor, out var state)) return;
        CancelCast(state, resolve: false);
        InvalidateTargetCasts(actor);
        if (state.Encounter is { } encounter) Remove(encounter, state, leaving: true);
        _actors.Remove(actor);
    }

    /// <summary>Физическое удаление anchor закрывает его очередь до миграции native-представлений.</summary>
    public void RemoveNode(NodeKey node, long nowDs)
    {
        if (!SetTime(nowDs)) throw new ArgumentOutOfRangeException(nameof(nowDs));
        if (_encounters.TryGetValue(node, out var encounter)) Close(encounter);
    }

    /// <summary>Обрабатывает одно намерение без ожидания native эффектов; продвижение очереди выполняет Advance.</summary>
    public CombatResult Apply(CombatIntent intent, long nowDs)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!SetTime(nowDs)) return new(false, "clock_regressed");
        if (!_actors.TryGetValue(intent.Actor, out var actor) || !Usable(actor)) return new(false, "actor_unavailable");
        return intent switch
        {
            AdmitCombatAction admit => Admit(actor, admit.Target, admit.Cost),
            JoinCombat join => JoinIntent(actor, join),
            PassCombatTurn pass => Pass(actor, pass.Token),
            UseCombatProgram use => Use(actor, use),
            ResolveCombatCast => ResolveCast(actor),
            CancelCombatCast => CancelCastIntent(actor),
            SpendCombatAp spend => Accepts(actor, spend.Token) ? Spend(actor, spend.Cost, spend.Deferred, spend.Hostile) : new(false, "stale_decision"),
            ResolveCombatAction => Resolve(actor),
            _ => new(false, "unknown_combat_intent")
        };
    }

    /// <summary>Одна единица budget рассматривает один timer либо одного участника, а не всю очередь/мир.</summary>
    public int Advance(long nowDs, int workBudget)
    {
        if (!SetTime(nowDs)) throw new ArgumentOutOfRangeException(nameof(nowDs));
        if (workBudget < 0) throw new ArgumentOutOfRangeException(nameof(workBudget));
        var work = 0;
        while (work < workBudget)
        {
            if (_advancing.TryDequeue(out var encounter))
            {
                work++;
                if (!encounter.Closed && encounter.Advancing) StepAdvance(encounter);
                continue;
            }
            if (!_scheduled.TryPeek(out _, out var priority) || priority.Due > _now) break;
            var scheduled = _scheduled.Dequeue();
            work++;
            if (scheduled.Encounter is { } timed)
            {
                if (!timed.Closed && timed.Active && !timed.Advancing && timed.Generation == scheduled.Generation && _now >= timed.Deadline)
                    Expire(timed);
            }
            else if (scheduled.Actor is { } key && _actors.TryGetValue(key, out var actor) && actor.Cast?.Generation == scheduled.Generation)
                ResolveCast(actor);
        }
        return work;
    }

    /// <summary>Извлекает immutable scalar effect; вызывающий registry применяет его до следующего логического намерения.</summary>
    public bool TryDequeueEffect(out CombatEffect? effect) => _effects.TryDequeue(out effect);

    /// <summary>Читает проекцию без Pump, изменения собственных ходов или физического времени.</summary>
    public CombatEncounterView? ReadEncounter(ActorKey actor) => _actors.TryGetValue(actor, out var state) && state.Encounter is { } encounter
        ? View(encounter) : null;

    /// <summary>Позволяет локальному наблюдателю видеть очередь до явного Join.</summary>
    public CombatEncounterView? ReadEncounter(NodeKey node) => _encounters.TryGetValue(node, out var encounter) ? View(encounter) : null;

    /// <summary>Читает статусы/подготовку без обновления боевых или физических часов.</summary>
    public CombatActorStatusView? ReadActor(ActorKey actor) => !_actors.TryGetValue(actor, out var state) ? null :
        new(actor, state.ShieldFactor, state.ShieldUntil, state.StunUntil, state.NextProgram,
            state.Cast is { } cast ? new(cast.Program.Id, cast.Target, cast.Due, cast.DamageScale) : null,
            new(state.WallCooldowns.Select(pair => new CombatWallCooldownView(pair.Key, pair.Value)), int.MaxValue));

    /// <summary>Immutable shared definition; конкретный cast хранит собственный scale и target отдельно.</summary>
    public CombatProgram? ReadProgram(string id) => _programs.GetValueOrDefault(id);

    /// <summary>Проверяет own-turn cooldown; вне очереди возвращает ноль, wall-clock ledger отдельный.</summary>
    public ulong CooldownRemaining(ActorKey actor, string programId)
    {
        if (!_actors.TryGetValue(actor, out var state) || state.Encounter is not { } encounter || Member(encounter, state) is not { } member) return 0;
        return member.Cooldowns.TryGetValue(programId, out var until) && until > member.OwnTurn ? until - member.OwnTurn : 0;
    }

    /// <summary>Неактивный бой даёт обычные три AP; просроченное решение authority уже не имеет.</summary>
    public int RemainingAp(ActorKey key) => _actors.TryGetValue(key, out var actor)
        ? actor.Encounter is not { Active: true } encounter ? MaximumAp : HasTurn(encounter, actor) ? Member(encounter, actor)?.Ap ?? 0 : 0 : 0;

    private bool SetTime(long now)
    {
        if (now < _now || now < 0 || now > long.MaxValue - _options.DecisionTimeDs - 1000000) return false;
        _now = now;
        return true;
    }

    private static bool Usable(Actor actor) => actor.Fact.Valid && actor.Fact.Integrity > 0;
    private static Participant? Member(Encounter encounter, Actor actor) => encounter.Participants.Find(member => member.Actor == actor);
    private bool HasTurn(Encounter encounter, Actor actor) => !encounter.Closed && !encounter.Advancing &&
        (!encounter.Active || (_now < encounter.Deadline && encounter.Current?.Actor == actor));
    private bool Accepts(Actor actor, string? token) => token is null || actor.Encounter is { Active: true, Closed: false } encounter && token == encounter.DecisionId;

    private CombatResult Admit(Actor actor, ActorKey? targetKey, int cost)
    {
        if (cost is < 1 or > MaximumAp) return new(false, "invalid_ap_cost");
        var encounter = actor.Encounter;
        if (encounter is not null && ExpiredInput(encounter)) return new(false, "decision_expired");
        if (encounter is { Active: true } && (!HasTurn(encounter, actor) || encounter.Deferred || (Member(encounter, actor)?.Ap ?? 0) < cost))
            return new(false, "not_your_turn");
        Actor? target = null;
        if (targetKey is not null && (!_actors.TryGetValue(targetKey, out target) || !Usable(target) || target.Fact.Node != actor.Fact.Node))
            return new(false, "target_unavailable");
        if (encounter is null) _encounters.TryGetValue(actor.Fact.Node, out encounter);
        if (encounter is { Active: true } && Member(encounter, actor) is null)
        {
            if (Join(encounter, actor) is not null) Changed(encounter);
            return new(false, "joined_waiting");
        }
        if (encounter is null)
        {
            if (target is null || target == actor || !target.Fact.TakesTurns) return new(true, "admitted");
            if (target.Encounter is not null) return new(false, "target_in_other_encounter");
            encounter = new(actor.Fact.Node, ++_encounterSerial);
            _encounters.Add(encounter.Node, encounter);
        }
        var previous = Member(encounter, actor);
        var participant = Join(encounter, actor);
        if (participant is null) return new(false, "actor_contact_invalid");
        if (target is not null && target != actor && target.Fact.TakesTurns && Join(encounter, target) is null)
        {
            if (previous is null) Remove(encounter, actor, leaving: true);
            return new(false, "target_in_other_encounter");
        }
        if (!encounter.Active && encounter.Participants.Count >= 2)
        {
            encounter.Active = true;
            encounter.CurrentIndex = encounter.Participants.IndexOf(participant);
            if (BeginTurn(participant)) FinishAdvance(encounter);
            else RequestAdvance(encounter);
        }
        return !encounter.Active || HasTurn(encounter, actor) && !encounter.Deferred && participant.Ap >= cost
            ? new(true, "admitted") : new(false, "not_your_turn");
    }

    private Participant? Join(Encounter encounter, Actor actor)
    {
        var previous = Member(encounter, actor);
        if (previous is not null) return previous;
        if (!Usable(actor) || !actor.Fact.TakesTurns || actor.Fact.Node != encounter.Node ||
            actor.Encounter is { } other && other != encounter) return null;
        var participant = new Participant(actor);
        encounter.Participants.Add(participant);
        actor.Encounter = encounter;
        return participant;
    }

    private CombatResult JoinIntent(Actor actor, JoinCombat join)
    {
        if (actor.Encounter is not null || actor.Fact.Node != join.Node || !_encounters.TryGetValue(join.Node, out var encounter) ||
            !encounter.Active || encounter.Closed || !AcceptsJoin(encounter, join.Token)) return new(false, "join_rejected");
        ExpiredInput(encounter);
        if (encounter.Closed || Join(encounter, actor) is null) return new(false, "join_rejected");
        Changed(encounter);
        return new(true, "joined");
    }

    private static bool AcceptsJoin(Encounter encounter, string? token)
    {
        var serial = encounter.Serial.ToString(CultureInfo.InvariantCulture);
        if (token is null || token == serial) return true;
        var parts = token.Split(':');
        return parts.Length == 2 && parts[0] == serial && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var decision) &&
            decision >= 1 && decision <= encounter.Generation && decision.ToString(CultureInfo.InvariantCulture) == parts[1];
    }

    private CombatResult Pass(Actor actor, string? token)
    {
        if (!Accepts(actor, token)) return new(false, "stale_decision");
        if (actor.Encounter is not { Active: true } encounter) return new(false, "no_encounter");
        if (ExpiredInput(encounter)) return new(false, "decision_expired");
        if (!HasTurn(encounter, actor) || encounter.Deferred) return new(false, "not_your_turn");
        RequestAdvance(encounter);
        return new(true, "passed");
    }

    private CombatResult Spend(Actor actor, int cost, bool deferred, bool hostile)
    {
        if (cost is < 1 or > MaximumAp) return new(false, "invalid_ap_cost");
        if (actor.Encounter is not { Active: true } encounter) return new(true, "spent");
        if (ExpiredInput(encounter)) return new(false, "decision_expired");
        if (!HasTurn(encounter, actor) || encounter.Deferred || Member(encounter, actor) is not { } participant || participant.Ap < cost)
            return new(false, "insufficient_ap");
        participant.Ap -= cost;
        if (hostile) { encounter.RoundHadCombat = true; encounter.QuietRounds = 0; }
        encounter.Deferred = deferred;
        if (!deferred && participant.Ap <= 0) RequestAdvance(encounter); else Changed(encounter);
        return new(true, "spent");
    }

    private CombatResult Resolve(Actor actor)
    {
        if (actor.Encounter is not { Active: true } encounter || !HasTurn(encounter, actor) || !encounter.Deferred)
            return new(false, "no_deferred_action");
        encounter.Deferred = false;
        if (encounter.Current?.Ap <= 0) RequestAdvance(encounter);
        else { Changed(encounter); Wake(actor); }
        return new(true, "resolved");
    }

    private CombatResult Use(Actor actor, UseCombatProgram use)
    {
        if (!Accepts(actor, use.Token)) return new(false, "stale_decision");
        if (!_programs.TryGetValue(use.ProgramId, out var program) || !actor.Fact.Programs.Contains(use.ProgramId) ||
            actor.Fact.Kind != CombatActorKind.Ice && !program.ConsoleUsable) return new(false, "program_unavailable");
        if (!double.IsFinite(use.DamageScale) || use.DamageScale < 0 || use.DamageScale > _options.MaximumDamageScale) return new(false, "invalid_damage_scale");
        if (actor.Cast is not null || actor.StunUntil > _now) return new(false, "actor_busy");
        var wasInCombat = actor.Encounter is { Active: true };
        if (wasInCombat ? CooldownRemaining(actor.Fact.Key, program.Id) > 0 : _now < actor.NextProgram || _now < actor.WallCooldowns.GetValueOrDefault(program.Id))
            return new(false, "program_cooldown");
        var targetKey = program.NeedsTarget ? use.Target : actor.Fact.Key;
        if (targetKey is null || !_actors.TryGetValue(targetKey, out var target) || !Usable(target) || actor.Fact.Node != target.Fact.Node)
            return new(false, "target_unavailable");
        var admission = Admit(actor, targetKey, program.ApCost);
        if (!admission.Accepted) return admission;
        var context = Context(actor, target, use.DamageScale);
        if (!program.CanApply(context)) return new(false, "status_rejected");
        var participant = actor.Encounter is { } owner ? Member(owner, actor) : null;
        var spent = Spend(actor, program.ApCost, deferred: actor.Encounter is { Active: true }, program.Hostile || program.TargetsBreach);
        if (!spent.Accepted) return spent;
        if (participant is not null) participant.Cooldowns[program.Id] = participant.OwnTurn + (ulong)program.CooldownTurns;
        if (program.Hostile) _effects.Enqueue(new(CombatEffectKind.Reveal, actor.Fact.Key, ProgramId: program.Id));
        if (use.Telegraph)
        {
            var telegraph = wasInCombat ? _options.TelegraphInCombatDs : Math.Max(_options.TelegraphMinDs, program.WindupDs);
            var cast = new Cast(program, target.Fact.Key, use.DamageScale, _now + telegraph, ++actor.CastGeneration, use.ExecutableId);
            actor.Cast = cast;
            if (!_targetCasts.TryGetValue(target.Fact.Key, out var sources)) _targetCasts.Add(target.Fact.Key, sources = []);
            sources.Add(actor.Fact.Key);
            actor.WallCooldowns[program.Id] = cast.Due + program.CooldownDs;
            _scheduled.Enqueue(new(null, actor.Fact.Key, cast.Generation), (cast.Due, ++_scheduleSerial));
            if (program.Consumable) Consume(actor, program, use.ExecutableId);
            _effects.Enqueue(new(CombatEffectKind.CastStarted, actor.Fact.Key, target.Fact.Key, UntilDs: cast.Due, ProgramId: program.Id));
            return new(true, "casting");
        }
        actor.NextProgram = _now + _options.GlobalProgramCooldownDs;
        actor.WallCooldowns[program.Id] = _now + program.CooldownDs;
        Deliver(actor, target, program, use.DamageScale);
        Resolve(actor);
        if (actor.Fact.Kind != CombatActorKind.Ice && program.Trace > 0)
            _effects.Enqueue(new(CombatEffectKind.Trace, actor.Fact.Key, Value: program.Trace, ProgramId: program.Id));
        if (program.Consumable) Consume(actor, program, use.ExecutableId);
        return new(true, "executed");
    }

    private CombatProgramContext Context(Actor source, Actor target, double scale)
    {
        var participant = target.Encounter is { } encounter ? Member(encounter, target) : null;
        return new(source.Fact.Key, target.Fact, scale, source.Encounter is { Active: true }, participant?.StunImmune ?? false,
            target.ShieldUntil > _now ? target.ShieldFactor : 1, target.ShieldUntil > _now, _now);
    }

    private void Deliver(Actor actor, Actor target, CombatProgram program, double scale)
    {
        var result = program.Apply(Context(actor, target, scale));
        var participant = target.Encounter is { Active: true } encounter ? Member(encounter, target) : null;
        foreach (var effect in result.Effects)
        {
            var applied = effect;
            if (effect.Kind == CombatEffectKind.Shield)
            {
                target.ShieldFactor = effect.Value;
                target.ShieldUntil = participant is not null && result.Status is { ShieldTurns: > 0 } ? long.MaxValue : effect.UntilDs;
                applied = effect with { UntilDs = target.ShieldUntil };
            }
            if (effect.Kind == CombatEffectKind.Stun)
            {
                target.StunUntil = participant is not null && result.Status is { StunTurns: > 0 } ? long.MaxValue : Math.Max(target.StunUntil, effect.UntilDs);
                applied = effect with { UntilDs = target.StunUntil };
                CancelCast(target, resolve: true);
            }
            _effects.Enqueue(applied);
        }
        if (participant is null || result.Status is not { } status) return;
        if (status.ShieldFactor is { } factor && status.ShieldTurns > 0 &&
            (participant.ShieldUntilTurn == 0 || participant.ShieldFactor >= factor))
        {
            participant.ShieldUntilTurn = participant.OwnTurn + (ulong)status.ShieldTurns;
            participant.ShieldFactor = factor;
        }
        if (status.StunTurns > 0) participant.StunTurns = Math.Max(participant.StunTurns, status.StunTurns);
        if (target.Encounter is { } owner) Changed(owner);
    }

    private void Consume(Actor actor, CombatProgram program, string? executableId) =>
        _effects.Enqueue(new(CombatEffectKind.ConsumeProgram, actor.Fact.Key, ProgramId: program.Id, ExecutableId: executableId));

    private CombatResult ResolveCast(Actor actor)
    {
        if (actor.Cast is not { } cast) return new(false, "no_cast");
        if (!_actors.TryGetValue(cast.Target, out var target) || !Usable(actor) || !Usable(target) || actor.StunUntil > _now || actor.Fact.Node != target.Fact.Node)
            return CancelCastIntent(actor);
        if (actor.Encounter is { Active: true } encounter && (!HasTurn(encounter, actor) || !encounter.Deferred))
        {
            CancelCast(actor, resolve: false);
            ExpiredInput(encounter);
            return new(false, "decision_expired");
        }
        if (!cast.Program.CanApply(Context(actor, target, cast.DamageScale))) return CancelCastIntent(actor);
        if (_now < cast.Due) return new(false, "cast_pending");
        CancelCast(actor, resolve: false, code: "resolved");
        actor.NextProgram = _now + _options.GlobalProgramCooldownDs;
        Deliver(actor, target, cast.Program, cast.DamageScale);
        Resolve(actor);
        return new(true, "resolved");
    }

    private CombatResult CancelCastIntent(Actor actor)
    {
        if (actor.Cast is null) return new(false, "no_cast");
        CancelCast(actor, resolve: true);
        return new(true, "cast_cancelled");
    }

    private void CancelCast(Actor actor, bool resolve, string code = "cancelled")
    {
        if (actor.Cast is not { } cast) return;
        actor.Cast = null;
        if (_targetCasts.TryGetValue(cast.Target, out var sources))
        {
            sources.Remove(actor.Fact.Key);
            if (sources.Count == 0) _targetCasts.Remove(cast.Target);
        }
        _effects.Enqueue(new(CombatEffectKind.CastEnded, actor.Fact.Key, cast.Target, ProgramId: cast.Program.Id, Code: code));
        if (resolve) Resolve(actor);
    }

    private void InvalidateTargetCasts(ActorKey target)
    {
        if (!_targetCasts.TryGetValue(target, out var sources)) return;
        foreach (var source in sources.ToArray())
            if (_actors.TryGetValue(source, out var actor)) CancelCast(actor, resolve: true);
    }

    private bool ExpiredInput(Encounter encounter)
    {
        if (!encounter.Active || encounter.Closed || encounter.Advancing || _now < encounter.Deadline) return false;
        Expire(encounter);
        return true;
    }

    private void Expire(Encounter encounter)
    {
        if (encounter.Deferred && encounter.Current is { } participant)
        {
            encounter.Deferred = false;
            CancelCast(participant.Actor, resolve: false);
        }
        RequestAdvance(encounter);
    }

    private bool BeginTurn(Participant participant)
    {
        participant.OwnTurn++;
        participant.Ap = participant.OwnTurn == 1 && participant.Actor.CarriedUntil > _now && participant.Actor.CarriedAp >= 0
            ? Math.Clamp(participant.Actor.CarriedAp, 0, MaximumAp) : MaximumAp;
        if (participant.OwnTurn == 1) participant.Actor.CarriedAp = -1;
        participant.RealTurn = false;
        if (participant.ShieldUntilTurn > 0 && participant.OwnTurn >= participant.ShieldUntilTurn) ClearShield(participant);
        if (participant.StunTurns <= 0) return participant.RealTurn = participant.Ap > 0;
        participant.StunTurns--;
        participant.StunImmune = true;
        if (participant.StunTurns == 0) ClearStun(participant);
        participant.Ap = 0;
        return false;
    }

    private void RequestAdvance(Encounter encounter)
    {
        if (encounter.Closed || encounter.Advancing) return;
        encounter.Deferred = false;
        if (encounter.Current is { RealTurn: true } outgoing) outgoing.StunImmune = false;
        encounter.Advancing = true;
        encounter.AdvanceRemaining = Math.Max(1, encounter.Participants.Count * _options.AdvancePassesPerParticipant);
        _advancing.Enqueue(encounter);
    }

    private void StepAdvance(Encounter encounter)
    {
        if (encounter.Participants.Count <= 1) { Close(encounter); return; }
        encounter.CurrentIndex++;
        if (encounter.CurrentIndex >= encounter.Participants.Count)
        {
            encounter.CurrentIndex = 0;
            encounter.QuietRounds = encounter.RoundHadCombat ? 0 : encounter.QuietRounds + 1;
            encounter.RoundHadCombat = false;
            if (encounter.QuietRounds >= _options.IdleRounds) { Close(encounter); return; }
            encounter.Round++;
        }
        var participant = encounter.Current!;
        encounter.AdvanceRemaining--;
        if (!Usable(participant.Actor) || participant.Actor.Fact.Node != encounter.Node)
        {
            Remove(encounter, participant.Actor, leaving: true);
            if (!encounter.Closed) _advancing.Enqueue(encounter);
            return;
        }
        if (BeginTurn(participant) || encounter.AdvanceRemaining <= 0) FinishAdvance(encounter);
        else _advancing.Enqueue(encounter);
    }

    private void FinishAdvance(Encounter encounter)
    {
        encounter.Advancing = false;
        encounter.Deadline = _now + _options.DecisionTimeDs;
        encounter.Generation++;
        _scheduled.Enqueue(new(encounter, null, encounter.Generation), (encounter.Deadline, ++_scheduleSerial));
        Changed(encounter);
        if (encounter.Current is { Ap: > 0 } participant) Wake(participant.Actor);
    }

    private void Remove(Encounter encounter, Actor actor, bool leaving)
    {
        var index = encounter.Participants.FindIndex(participant => participant.Actor == actor);
        if (index < 0 || encounter.Closed) return;
        var participant = encounter.Participants[index];
        var wasCurrent = index == encounter.CurrentIndex;
        if (leaving && encounter.Active && _now < encounter.Deadline && actor.Fact.Kind != CombatActorKind.Fortress)
        {
            actor.CarriedAp = wasCurrent ? participant.Ap : 0;
            actor.CarriedUntil = encounter.Deadline;
        }
        CancelCast(actor, resolve: false);
        actor.Encounter = null;
        ClearShield(participant);
        ClearStun(participant);
        encounter.Participants.RemoveAt(index);
        if (index <= encounter.CurrentIndex) encounter.CurrentIndex--;
        Wake(actor);
        if (encounter.Participants.Count == 0 || encounter.Active && encounter.Participants.Count <= 1) { Close(encounter); return; }
        if (wasCurrent) RequestAdvance(encounter); else Changed(encounter);
    }

    private void Close(Encounter encounter)
    {
        if (encounter.Closed) return;
        encounter.Closed = true;
        encounter.Generation++;
        _encounters.Remove(encounter.Node);
        foreach (var participant in encounter.Participants)
        {
            participant.Actor.Encounter = null;
            CancelCast(participant.Actor, resolve: false);
            ClearShield(participant);
            ClearStun(participant);
            Wake(participant.Actor);
            _effects.Enqueue(new(CombatEffectKind.EncounterEnded, participant.Actor.Fact.Key, Node: encounter.Node));
        }
        encounter.Participants.Clear();
    }

    private void ClearShield(Participant participant)
    {
        if (participant.ShieldUntilTurn > 0 && participant.Actor.ShieldUntil == long.MaxValue)
        {
            participant.Actor.ShieldUntil = _now;
            _effects.Enqueue(new(CombatEffectKind.Shield, participant.Actor.Fact.Key, Value: 1, UntilDs: _now));
        }
        participant.ShieldUntilTurn = 0;
        participant.ShieldFactor = 1;
    }

    private void ClearStun(Participant participant)
    {
        if (participant.Actor.StunUntil == long.MaxValue)
        {
            participant.Actor.StunUntil = _now;
            _effects.Enqueue(new(CombatEffectKind.Stun, participant.Actor.Fact.Key, UntilDs: _now));
        }
        participant.StunTurns = 0;
    }

    private void Wake(Actor actor) => _effects.Enqueue(new(CombatEffectKind.Wake, actor.Fact.Key,
        Value: actor.Fact.Kind == CombatActorKind.Entity ? _options.EntityWakeDs : 0));
    private void Changed(Encounter encounter)
    {
        if (encounter.Current is { } participant)
            _effects.Enqueue(new(CombatEffectKind.EncounterChanged, participant.Actor.Fact.Key, Node: encounter.Node));
    }

    private static CombatEncounterView View(Encounter encounter) => new(encounter.Node, encounter.DecisionId,
        encounter.Serial.ToString(CultureInfo.InvariantCulture), encounter.Round, encounter.QuietRounds,
        encounter.Current?.Actor.Fact.Key, encounter.Deadline, encounter.Deferred, encounter.Advancing,
        new(encounter.Participants.Select(participant => new CombatParticipantView(participant.Actor.Fact.Key,
            participant.Ap, participant.OwnTurn, encounter.Current == participant, participant.StunTurns,
            participant.StunImmune, participant.ShieldUntilTurn, participant.ShieldFactor,
            new(participant.Cooldowns.Select(pair => new CombatCooldownView(pair.Key,
                pair.Value > participant.OwnTurn ? pair.Value - participant.OwnTurn : 0)), int.MaxValue))), int.MaxValue));
}
