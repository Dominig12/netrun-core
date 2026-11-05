using System.Text;
using Netrun.Core.Entities.Combat;

namespace Netrun.Core.Entities.Bots;

/// <summary>Сроки, signal waits и ordinary command lifecycle из DM brain/goals.</summary>
public sealed partial class BotEngine
{
    /// <summary>Один вызов исполняет не более одного stamped действия.</summary>
    private void Tick(Brain brain)
    {
        var goal = brain.Goals.FirstOrDefault();
        if (goal is not null && _now >= goal.ExpiresAt) Finish(brain, goal, false);
        if (brain.Pending is { } expired && _now >= expired.DeadlineDs)
        {
            brain.Result = new(expired.Bot, expired.Actor, expired.ActionSerial, expired.ExpectedRevision,
                brain.HandedOff ? "uncertain" : "cancelled");
            brain.ResultAction = expired.Action;
            brain.ResultGoal = brain.PendingGoal;
            brain.Pending = null;
            brain.HandedOff = false;
        }
        ConsumeNonCommandResult(brain);
        if (brain.Result?.Phase == "uncertain")
        {
            var uncertain = brain.Goals.FirstOrDefault(entry => entry.Serial == brain.ResultGoal);
            if (uncertain is not null) Finish(brain, uncertain, false);
            ClearResult(brain);
            brain.AwaitFactRevision = brain.Fact.Revision;
        }
        if (brain.ResultAction is BotCommand or BotStartBreach &&
            !brain.Goals.Any(entry => entry.Serial == brain.ResultGoal)) ClearResult(brain);
        var actor = brain.Fact.Actor;
        var context = brain.Fact.Context;
        var invasion = brain.Goals.FirstOrDefault();
        if (invasion is { Kind: "station_invasion", DisruptAt: > 0 } &&
            (actor.Node != invasion.Target || actor.StunnedUntilDs > _now || context.Encounter is not null ||
                Node(brain, invasion.Target) is not { Online: true, AccessLevel: >= 2 })) invasion.Interrupted = true;
        if (!actor.Alive || !actor.Autonomous || !context.ConsoleAvailable) return;
        brain.Blacklist.Prune(_now); brain.Enemies.Prune(_now);
        if (context.Orphaned)
        { brain.State = "связь с владельцем потеряна"; Emit(brain, new BotDissolve("orphaned")); return; }
        if (brain.ExpiresAt > 0 && _now >= brain.ExpiresAt)
        {
            if (context.Visitor && !brain.Departing) BeginDeparture(brain);
            else { brain.State = "завершён"; Emit(brain, new BotDissolve(brain.Departing ? "relay_unavailable" : "expired")); return; }
        }
        if (actor.StunnedUntilDs > _now) { brain.State = "оглушён"; return; }
        if (brain.Pending is not null || brain.AwaitFactRevision is not null) return;
        if (brain.Profile.Planner is "repair" or "police" && _now >= brain.NextReport)
        {
            var rogue = context.Targets.FirstOrDefault(target => target.IsBot && target.FactionHostile &&
                target.Actor.Node == actor.Node && target.Actor.Valid);
            if (rogue is not null && Emit(brain, new BotIncident("rogue", actor.Node, rogue.Actor.Key)))
            { brain.NextReport = Later(_memory.RogueReportCooldown); return; }
        }
        // Background breach preparation precedes combat, но movement/device actions остаются после него.
        goal = brain.Goals.FirstOrDefault();
        if (goal?.Kind == "hack" && context.CanHack && !goal.Waiting)
        {
            var background = AdvanceHack(brain, goal, true);
            if (background != 0) Finish(brain, goal, false);
            if (brain.Pending is not null) return;
        }
        if (CombatStep(brain)) return;
        if (context.ReturnPending) { brain.State = "возврат пакета"; return; }
        if (!context.GoalsEnabled) return;
        AdvanceVisitorObjective(brain);
        goal = brain.Goals.FirstOrDefault();
        if (goal is null)
        {
            Plan(brain);
            goal = brain.Goals.FirstOrDefault();
        }
        if (goal is null) { brain.State = "ожидание"; return; }
        var result = AdvanceGoal(brain, goal);
        if (result != 0 || _now >= goal.ExpiresAt) Finish(brain, goal, result == 1);
    }

    /// <summary>0 — продолжить, 1 — выполнено, 2 — отказ; все timeouts заданы при создании цели.</summary>
    private int AdvanceGoal(Brain brain, Goal goal)
    {
        var node = Node(brain, goal.Target);
        switch (goal.Kind)
        {
            case "terminate":
                brain.State = "завершение"; Emit(brain, new BotDissolve("terminated"), goal); return 0;
            case "travel":
            case "patrol":
                if (goal.Kind == "patrol" && node is null)
                { goal.Target = PatrolDestination(brain, Scope(brain)); node = Node(brain, goal.Target); }
                brain.State = goal.Kind == "patrol" ? "патруль" : "в пути";
                return Approach(brain, node, goal) switch { 1 => 1, 2 => 2, _ => 0 };
            case "guard":
            case "recycling_wait":
                if (node is null) return 2;
                if (node.Key == brain.Fact.Actor.Node) { brain.State = "охрана"; return 0; }
                return Approach(brain, node, goal) == 2 ? 2 : 0;
            case "command":
            case "survey":
                if (node is null) return 2;
                if (node.Key != brain.Fact.Actor.Node) return Approach(brain, node, goal) == 2 ? 2 : 0;
                brain.State = "команда устройства";
                var command = CommandStep(brain, goal, node, goal.Command ?? "scan");
                if (command != 1 || goal.Kind != "survey") return command;
                var response = Truncate(brain.Result?.Response ?? "", _tuning.ReportTruncateChars);
                ClearResult(brain);
                Emit(brain, new BotStoreReport($"survey-{node.Key.NodeId}.txt", $"{node.Key.NodeId}\n{response}"), goal);
                return 0;
            case "repair":
                if (node is null || node.DamagedFortressModules == 0) return 1;
                if (node.Key != brain.Fact.Actor.Node) return Approach(brain, node, goal) == 2 ? 2 : 0;
                brain.State = "ремонт";
                if (brain.ResultAction is BotCommand || _now >= node.NextRepairDs)
                {
                    var repair = CommandStep(brain, goal, node, "fort_repair");
                    if (repair == 2) return 2;
                    if (repair == 1) { ClearResult(brain); brain.AwaitFactRevision = brain.Fact.Revision; }
                }
                return 0;
            case "salvage":
                if (node is null || node.Salvage.Count == 0) return 1;
                if (node.Key != brain.Fact.Actor.Node) return Approach(brain, node, goal) == 2 ? 2 : 0;
                brain.State = "сбор обломков";
                var file = BestSalvage(brain, node);
                if (file is null) return 1;
                Emit(brain, new BotRecover(node.Key, file.Uid), goal);
                return 0;
            case "recall":
                if (!brain.Fact.Context.OwnerAvailable) { brain.State = "ожидание владельца"; return 2; }
                brain.State = "возврат";
                if (brain.Fact.Context.OwnerReachable) Emit(brain, new BotRecall(), goal);
                return 0;
            case "depart_network":
                brain.State = "уход во внешнюю сеть через реле NTnet";
                var current = Node(brain, brain.Fact.Actor.Node);
                if (current?.RelayEntryAvailable == true && !brain.Fact.Context.Locked && brain.Fact.Context.Encounter is null)
                { Emit(brain, new BotLeaveNetwork(), goal); return 0; }
                if (node?.RelayEntryAvailable != true || Blacklisted(brain, node.Key))
                { goal.Target = ExitNode(brain); node = Node(brain, goal.Target); }
                if (node is null) { brain.State = "ожидание доступного реле NTnet"; return 0; }
                if (Approach(brain, node, goal) == 2) goal.Target = null;
                return 0;
            case "hack": return AdvanceHack(brain, goal, false);
            case "recycle_deposit": return AdvanceDeposit(brain, goal, node);
            case "police_patrol": return AdvancePolicePatrol(brain, goal, node);
            case "investigate": return AdvanceInvestigation(brain, goal, node);
            case "station_invasion": return AdvanceInvasion(brain, goal, node);
            default: return 2;
        }
    }

    /// <summary>Команда не повторяется после uncertain/timeout; nonce и terminal outcome принадлежат единственной цели.</summary>
    private int CommandStep(Brain brain, Goal goal, BotNodeFact node, string command, string? uid = null)
    {
        if (brain.Result is { } result && brain.ResultGoal == goal.Serial && brain.ResultAction is BotCommand previous)
        {
            if (previous.Node != node.Key || previous.CommandId != command) return 2;
            return result.Phase == "completed" ? 1 : 2;
        }
        if (brain.Pending is not null || brain.AwaitFactRevision is not null || _now < brain.Fact.Context.NextCommandDs) return 0;
        if (!node.Commands.Contains(command) && command != "file.deposit") return 2;
        var nonce = $"bot-{brain.ActionSerial + 1}";
        brain.State = "команда устройства";
        Emit(brain, new BotCommand(node.Key, command, nonce, uid), goal);
        return 0;
    }

    /// <summary>Network GUEST → node USER → честный переход, без присвоения grant внутри brain.</summary>
    private int AdvanceHack(Brain brain, Goal goal, bool backgroundOnly)
    {
        var context = brain.Fact.Context;
        var target = Node(brain, goal.Target);
        if (!context.CanHack || target is null || !context.ConsoleAvailable) return 2;
        if (context.Locked || !target.Online) return HackFailure(brain, target, target);
        if (brain.ResultGoal == goal.Serial && brain.ResultAction is BotStartBreach && brain.Result is { } outcome)
        {
            goal.Waiting = false;
            ClearResult(brain);
            if (outcome.Phase != "completed") return HackFailure(brain, target, target);
        }
        if (goal.Waiting || context.BreachActive) { brain.State = "взлом"; return 0; }
        if (brain.Fact.Actor.Node == target.Key && target.AccessLevel >= 2)
        {
            brain.State = goal.Command is null ? "взлом завершён" : "команда после взлома";
            return backgroundOnly && goal.Command is not null ? 0 : goal.Command is null ? 1 : CommandStep(brain, goal, target, goal.Command);
        }
        if (!target.CanRoute) return HackFailure(brain, target, target);
        var frontier = Node(brain, target.Frontier ?? target.Key);
        if (frontier is null) return HackFailure(brain, target, target);
        if (frontier.AccessLevel < 2)
        {
            var kind = frontier.NetworkLevel < 1 ? "network" : "node";
            var id = kind == "network" ? frontier.NetworkId : frontier.Key.NodeId;
            var attempted = $"{kind}:{id}";
            if (string.IsNullOrEmpty(id) || goal.AttemptedFrontier == attempted || frontier.BreachBlocked)
                return HackFailure(brain, target, frontier);
            if (Emit(brain, new BotStartBreach(frontier.Key, kind, id), goal, goal.ExpiresAt))
            { goal.AttemptedFrontier = attempted; goal.Waiting = true; brain.State = "взлом"; }
            return 0;
        }
        if (backgroundOnly || _now < brain.Fact.Actor.NextHopDs) return 0;
        brain.State = "переход к цели взлома";
        Emit(brain, new BotNavigate(frontier.Key, true), goal);
        return 0;
    }

    /// <summary>Только фактический failed frontier попадает в минутный blacklist, отмена — нет.</summary>
    private int HackFailure(Brain brain, BotNodeFact target, BotNodeFact frontier)
    {
        brain.Blacklist.Put(frontier.Key, Later(_memory.HackBlacklist));
        if (frontier.Key != target.Key) brain.Blacklist.Put(target.Key, Later(_memory.HackBlacklist));
        return 2;
    }

    /// <summary>Outcome deposit потребляется до проверки исчезнувшего cargo UID.</summary>
    private int AdvanceDeposit(Brain brain, Goal goal, BotNodeFact? node)
    {
        if (node is null) return 2;
        if (brain.ResultGoal == goal.Serial && brain.ResultAction is BotCommand { CommandId: "file.deposit" })
        {
            var completed = brain.Result?.Phase == "completed";
            ClearResult(brain);
            if (!completed) { brain.Blacklist.Put(node.Key, Later(_memory.HackBlacklist)); return 2; }
            brain.AwaitFactRevision = brain.Fact.Revision;
            return 0;
        }
        var cargo = brain.Fact.Context.Cargo.FirstOrDefault();
        if (cargo is null) return 1;
        if (!AcceptsCargo(brain, node)) { brain.Blacklist.Put(node.Key, Later(_memory.HackBlacklist)); return 2; }
        if (node.Key != brain.Fact.Actor.Node) return Approach(brain, node, goal) == 2 ? 2 : 0;
        brain.State = "разгрузка обломков";
        return CommandStep(brain, goal, node, "file.deposit", cargo.Uid);
    }

    /// <summary>Сначала обычный delivered packet_log, затем только его ответ превращается в inspection report.</summary>
    private int AdvancePolicePatrol(Brain brain, Goal goal, BotNodeFact? node)
    {
        if (ClaimIncident(brain) is not null) return 1;
        if (node is null) { goal.Target = PatrolDestination(brain, null, true); node = Node(brain, goal.Target); }
        if (node is null) return 2;
        brain.State = "обход сети";
        var movement = Approach(brain, node, goal);
        if (movement != 1) return movement == 2 ? 2 : 0;
        var result = CommandStep(brain, goal, node, "packet_log");
        if (result != 1) return result;
        _inspected[node.Key] = _now;
        var text = brain.Result?.Response ?? "Журнал пуст.";
        ClearResult(brain);
        Emit(brain, new BotStoreReport("last-inspection.txt", text, 1), goal);
        return 0;
    }

    /// <summary>Осмотр → countertrace/предупреждение → восемь секунд отсутствия нарушителя → отчёт.</summary>
    private int AdvanceInvestigation(Brain brain, Goal goal, BotNodeFact? node)
    {
        var incident = brain.Fact.Context.Incidents.FirstOrDefault(entry => entry.Id == goal.IncidentId && entry.Open);
        if (incident is null) return 1;
        if (node is null) return ReportIncident(brain, goal, incident, "место происшествия недоступно");
        if (node.Key != brain.Fact.Actor.Node)
            return Approach(brain, node, goal) == 2 ? ReportIncident(brain, goal, incident, "нет пути к месту происшествия") : 0;
        brain.State = "расследование";
        if (!goal.Scanned)
        {
            var scan = CommandStep(brain, goal, node, "scan");
            if (scan == 1) { goal.Scanned = true; goal.ArrivedAt = _now; ClearResult(brain); }
            else if (scan == 2) return ReportIncident(brain, goal, incident, "доставка команды осмотра отклонена");
            return 0;
        }
        if (node.HasCountertraceTargets)
        {
            var trace = CommandStep(brain, goal, node, "countertrace");
            if (trace != 0) ClearResult(brain);
            goal.Engaged = true;
            return 0;
        }
        var suspect = Suspect(brain, incident);
        if (suspect is not null)
        {
            goal.Engaged = true; goal.ArrivedAt = _now;
            if (suspect.IsBot) brain.Enemies.Put(suspect.Actor.Key, Later(_memory.EnemyMemory));
            else if (brain.Warned.Get(suspect.Actor.Key) is not { } warned)
            {
                if (Emit(brain, new BotWarning(suspect.Actor.Key, "police_leave", Later(_memory.PoliceWarning)), goal))
                    brain.Warned.Put(suspect.Actor.Key, _now);
            }
            else if (_now - warned >= _memory.PoliceWarning) brain.Enemies.Put(suspect.Actor.Key, Later(_memory.EnemyMemory));
            return 0;
        }
        if (_now - goal.ArrivedAt < _memory.InvestigationSettle) return 0;
        return ReportIncident(brain, goal, incident, goal.Engaged ? "нарушитель обезврежен или покинул узел" : "нарушений не обнаружено");
    }

    /// <summary>Физическое применение sabotage происходит только после непрерывного удержания 300 DS.</summary>
    private int AdvanceInvasion(Brain brain, Goal goal, BotNodeFact? node)
    {
        if (goal.Interrupted || !brain.Fact.Context.Visitor || brain.Fact.Actor.FactionId != "redshift" || node is null || !node.Online ||
            brain.Fact.Context.Encounter is not null || brain.Fact.Actor.StunnedUntilDs > _now)
            return 2;
        if (goal.DisruptAt > 0 && brain.Fact.Actor.Node != node.Key) return 2;
        if (brain.Fact.Actor.Node != node.Key) return Approach(brain, node, goal) == 2 ? 2 : 0;
        if (node.AccessLevel < 2) return QueueFrontierHack(brain, node) ? 0 : 2;
        if (goal.DisruptAt == 0)
        {
            if (!node.RaidEligible || _now < node.RaidReadyAtDs || _now < brain.Fact.Context.NextInvasionDs || brain.Fact.Context.ActiveInvasions >= _tuning.MaxActiveInvasions)
                return 2;
            if (Emit(brain, new BotSabotage(node.Key, false, Later(_tuning.SabotageHoldDs)), goal)) goal.DisruptAt = Later(_tuning.SabotageHoldDs);
            return 0;
        }
        brain.State = "удержание узла";
        if (_now < goal.DisruptAt) return 0;
        Emit(brain, new BotSabotage(node.Key, true, goal.DisruptAt), goal);
        return 0;
    }

    /// <summary>Отчёт закрывает только выбранный incident, не читая или подменяя target console.</summary>
    private int ReportIncident(Brain brain, Goal goal, BotIncidentFact incident, string resolution)
    {
        Emit(brain, new BotIncident("close", incident.Node, incident.Suspect, incident.Id, resolution), goal);
        return 0;
    }

    /// <summary>Один serial и один pending slot; would_block не потребляет цель и не делает mutation.</summary>
    private bool Emit(Brain brain, BotAction action, Goal? goal = null, int? deadline = null)
    {
        if (brain.Pending is not null || _effects.Count >= _options.MaxEffects || brain.ActionSerial == ulong.MaxValue) return false;
        var actor = brain.Fact.Actor;
        var effect = new BotEffect(brain.Fact.Key, actor.Actor, ++brain.ActionSerial, actor.Revision,
            actor.ResidenceRevision, actor.Node, action, deadline ?? Later(_memory.DefaultActionDeadline));
        brain.Pending = effect;
        brain.PendingGoal = goal?.Serial ?? 0;
        brain.HandedOff = false;
        _effects.Enqueue(effect);
        return true;
    }

    /// <summary>Не-command ACK обрабатывается ровно один раз до следующего dispatcher шага.</summary>
    private void ConsumeNonCommandResult(Brain brain)
    {
        if (brain.Result is not { } result || brain.ResultAction is BotCommand or BotStartBreach) return;
        var goal = brain.Goals.FirstOrDefault(entry => entry.Serial == brain.ResultGoal);
        var completed = result.Phase == "completed";
        switch (brain.ResultAction)
        {
            case BotRecover:
                if (completed && brain.Objective == "salvage" && !brain.Departing && Node(brain, brain.Fact.Actor.Node)?.Station == true) brain.Progress++;
                if (!completed && goal is not null) Finish(brain, goal, false);
                break;
            case BotNavigate when !completed:
                if (goal is not null) { if (goal.Target is { } target) brain.Blacklist.Put(target, Later(_memory.HopBlacklist)); Finish(brain, goal, false); }
                break;
            case BotNavigate when brain.FleeFrom is { } previous:
                brain.Blacklist.Put(previous, Later(_memory.FleeBlacklist)); brain.FleeFrom = null; break;
            case BotCombat { Intent: UseCombatProgram use } when completed:
                if (_programs.GetValueOrDefault(use.ProgramId)?.Role == "stun") brain.ControlledTarget = use.Target;
                break;
            case BotStoreReport:
                if (goal is not null) Finish(brain, goal, true);
                break;
            case BotSabotage { Apply: true }:
                if (goal is not null) Finish(brain, goal, completed);
                break;
            case BotSabotage when !completed:
                if (goal is not null) Finish(brain, goal, false);
                break;
            case BotIncident { Kind: "close" }:
                if (goal is not null) Finish(brain, goal, completed);
                break;
            case BotDissolve or BotRecall or BotLeaveNetwork when completed:
                brain.Retired = true; brain.State = "завершён"; break;
        }
        ClearResult(brain);
    }

    /// <summary>Каждый сигнал завершения освобождает response, а не хранит JSON/operation object в brain.</summary>
    private static void ClearResult(Brain brain)
    { brain.Result = null; brain.ResultAction = null; brain.ResultGoal = 0; }

    /// <summary>Ограниченная FIFO цель; frontier вставляется впереди и вытесняет хвост.</summary>
    private Goal Push(Brain brain, string kind, NodeKey? target, string? command, int timeout, bool first = false)
    {
        var goal = new Goal(++brain.GoalSerial, kind, target, command, Later(timeout));
        if (first) brain.Goals.Insert(0, goal); else brain.Goals.Add(goal);
        while (brain.Goals.Count > 4) brain.Goals.RemoveAt(first ? brain.Goals.Count - 1 : 0);
        return goal;
    }

    /// <summary>Отмена/expiry одноразового приказа переводит его в guard и не сохраняет команду для relaunch.</summary>
    private void ClearGoals(Brain brain)
    {
        brain.Goals.Clear();
        if (brain.Order?.Kind == "command") brain.Order = new("guard", brain.Post);
        if (brain.Pending is not null && !brain.HandedOff) brain.Pending = null;
        brain.PendingGoal = 0;
        ClearResult(brain);
        ReleaseIncident(brain);
    }

    /// <summary>Завершение относится к точному goal serial, не к позиции изменившегося списка.</summary>
    private void Finish(Brain brain, Goal goal, bool successful)
    {
        if (!brain.Goals.Remove(goal)) return;
        if (goal.Kind == "command" && brain.Order?.Kind == "command") brain.Order = new("guard", brain.Post);
        if (brain.PendingGoal == goal.Serial)
        {
            if (!brain.HandedOff) brain.Pending = null;
            brain.PendingGoal = 0;
        }
        ClearResult(brain);
        if (goal.Kind == "investigate") ReleaseIncident(brain);
        if (successful && goal.Kind == "station_invasion" && brain.Objective == "sabotage")
        { brain.Completed = true; BeginDeparture(brain); }
    }

    /// <summary>Signal waits и активный turn не опрашиваются каждые think_busy; у brain один заменяемый deadline.</summary>
    private void Schedule(Brain brain)
    {
        var actor = brain.Fact.Actor;
        var context = brain.Fact.Context;
        var goal = brain.Goals.FirstOrDefault();
        var next = int.MaxValue;
        if (!brain.Retired)
        {
            if (brain.ExpiresAt > _now) next = Math.Min(next, brain.ExpiresAt);
            if (goal is not null) next = Math.Min(next, goal.ExpiresAt);
            if (brain.Pending is { } pending) next = Math.Min(next, pending.DeadlineDs);
            else if (actor.Autonomous && context.ConsoleAvailable && brain.AwaitFactRevision is null)
            {
                if (actor.StunnedUntilDs > _now) next = Math.Min(next, actor.StunnedUntilDs);
                else if (context.Encounter is null && !context.ReturnPending && goal?.Waiting != true &&
                    !(goal?.Kind is "guard" or "recycling_wait" && goal.Target == actor.Node))
                {
                    var cadence = brain.InCombat ? brain.Profile.ThinkCombatDs : brain.Goals.Count > 0 ? brain.Profile.ThinkBusyDs : brain.Profile.ThinkIdleDs;
                    next = Math.Min(next, Later(Math.Max(1, cadence + Random(brain, -_tuning.ThinkJitterDs, _tuning.ThinkJitterDs + 1))));
                }
            }
        }
        brain.NextWake = next;
        if (brain.Scheduled is { } previous) _deadlines.Remove(previous);
        brain.Scheduled = null;
        if (next != int.MaxValue)
        {
            var scheduled = new ScheduledWake(brain.Fact.Key, Math.Max(Later(1), next), ++_wakeSerial);
            brain.Scheduled = scheduled;
            _deadlines.Add(scheduled);
        }
    }

    /// <summary>copytext_char для report не разделяет surrogate pair.</summary>
    private static string Truncate(string value, int maximum)
    {
        var result = new StringBuilder(Math.Min(value.Length, maximum));
        foreach (var rune in value.EnumerateRunes())
        { if (maximum-- == 0) break; result.Append(rune); }
        return result.ToString();
    }
}
