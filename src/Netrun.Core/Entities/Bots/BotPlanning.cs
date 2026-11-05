namespace Netrun.Core.Entities.Bots;

/// <summary>Перенос bounded itinerary/salvage/profile planning; production DM не выбирает цели.</summary>
public sealed partial class BotEngine
{
    /// <summary>Default scope повторяет home LAN либо явную custodian jurisdiction.</summary>
    private static ValueList<string>? Scope(Brain brain)
    {
        if (brain.Profile.Id == "custodian" && brain.Fact.Context.Jurisdiction is { Count: > 0 } jurisdiction) return jurisdiction;
        var home = Node(brain, brain.Fact.Actor.Node)?.NetworkId;
        return home is null ? null : new([home]);
    }

    /// <summary>Паттерн сначала выбирает policy, затем profile сохраняет собственный точный приоритет целей.</summary>
    private void Plan(Brain brain)
    {
        if (brain.Departing) { Push(brain, "depart_network", null, null, _goals.Depart); return; }
        var scope = Scope(brain);
        var post = brain.Post ?? brain.Fact.Context.Home;
        switch (brain.Behavior.Strategy)
        {
            case "guard": Push(brain, "guard", post, null, _goals.Guard); return;
            case "patrol": Push(brain, "patrol", null, null, _goals.Patrol); return;
            case "scavenge":
                var scavenged = FindSalvage(brain, scope);
                Push(brain, scavenged is null ? "patrol" : "salvage", scavenged, null, scavenged is null ? _goals.Patrol : _goals.Salvage);
                return;
        }
        if (brain.Fact.Context.Visitor && brain.Fact.Actor.FactionId == "redshift" &&
            _now >= brain.Fact.Context.NextInvasionDs && brain.Fact.Context.ActiveInvasions < _tuning.MaxActiveInvasions)
        {
            var here = Node(brain, brain.Fact.Actor.Node);
            var invasion = here?.RaidEligible == true && _now >= here.RaidReadyAtDs ? here.Key :
                Nearby(brain, node => node.RaidEligible && _now >= node.RaidReadyAtDs, null);
            if (invasion is not null) { Push(brain, "station_invasion", invasion, null, _goals.Invasion); return; }
        }
        switch (brain.Profile.Planner)
        {
            case "raider":
                var debris = FindSalvage(brain, null);
                if (debris is not null) { Push(brain, "salvage", debris, null, _goals.Salvage); return; }
                var ice = Nearby(brain, node => node.IceCount > 0, null);
                if (ice is not null) { Push(brain, "travel", ice, null, _goals.Travel); return; }
                break;
            case "collective":
                var enemy = Nearby(brain, node => node.Online && node.HasEnemy, null);
                if (enemy is not null) { Push(brain, "travel", enemy, null, _goals.Travel); return; }
                break;
            case "scavenge":
            case "collect":
                if (brain.Profile.Planner == "collect" && brain.Order?.Kind == "guard")
                { Push(brain, "guard", post, null, _goals.Guard); return; }
                var salvage = FindSalvage(brain, null);
                if (salvage is not null) { Push(brain, "salvage", salvage, null, _goals.Salvage); return; }
                break;
            case "repair":
                var repair = FindRepair(brain, scope);
                if (repair is not null) { Push(brain, "repair", repair, null, _goals.Repair); return; }
                break;
            case "guard":
            case "mender":
                if (brain.Order?.Kind == "patrol") { Push(brain, "patrol", null, null, _goals.Patrol); return; }
                if (brain.Profile.Planner == "mender" && Node(brain, post)?.DamagedFortressModules > 0)
                    Push(brain, "repair", post, null, _goals.Repair);
                else Push(brain, "guard", post, null, _goals.Guard);
                return;
            case "survey":
                if (brain.Order?.Kind == "guard") { Push(brain, "guard", post, null, _goals.Guard); return; }
                var survey = PatrolDestination(brain, scope);
                Push(brain, survey is null ? "guard" : "survey", survey ?? post, survey is null ? null : "scan", survey is null ? _goals.Guard : _goals.Survey);
                return;
            case "hack":
                var hack = brain.Order?.Kind == "patrol" && brain.Fact.Context.CanHack ? HackTarget(brain) : null;
                Push(brain, hack is null ? "guard" : "hack", hack ?? post, null, hack is null ? _goals.Guard : _goals.Hack);
                return;
            case "police":
                var incident = ClaimIncident(brain);
                if (incident is not null)
                {
                    var investigation = Push(brain, "investigate", incident.Node, null, _goals.Investigate);
                    investigation.IncidentId = incident.Id;
                }
                else Push(brain, "police_patrol", null, null, _goals.PolicePatrol);
                return;
            case "recycle":
                var server = brain.Fact.Context.Cargo.Count > 0 ? FindServer(brain) : null;
                if (server is not null) { Push(brain, "recycle_deposit", server, null, _goals.RecycleDeposit); return; }
                var recycle = FindSalvage(brain, null, true);
                if (recycle is not null) { Push(brain, "salvage", recycle, null, _goals.Salvage); return; }
                Push(brain, "recycling_wait", brain.Fact.Actor.Node, null,
                    brain.SalvageWaitUntil > _now ? Math.Min(_goals.RecycleWait, brain.SalvageWaitUntil - _now) : _goals.RecycleWait);
                brain.State = "ожидание обломков или свободного сервера";
                return;
            case "custom":
                if (brain.Order?.Kind != "patrol") { Push(brain, "guard", post, null, _goals.Guard); return; }
                break;
        }
        Push(brain, "patrol", null, null, _goals.Patrol);
    }

    /// <summary>Локальный tactical BFS рассматривает максимум 48 вершин и не вычисляет permissions в DM.</summary>
    private NodeKey? Nearby(Brain brain, Func<BotNodeFact, bool> predicate, ValueList<string>? scope)
    {
        var queue = new Queue<NodeKey>();
        var seen = new HashSet<NodeKey> { brain.Fact.Actor.Node };
        queue.Enqueue(brain.Fact.Actor.Node);
        for (var index = 0; index < 48 && queue.TryDequeue(out var key); index++)
        {
            var node = Node(brain, key);
            if (node is null) continue;
            if (key != brain.Fact.Actor.Node && !Blacklisted(brain, key) && predicate(node)) return key;
            if (!brain.Adjacent.TryGetValue(key, out var neighbors)) continue;
            foreach (var nextKey in neighbors)
            {
                if (!seen.Add(nextKey)) continue;
                var next = Node(brain, nextKey);
                if (next is not null && InScope(next, scope) && next.CanPlanVisit) queue.Enqueue(nextKey);
            }
        }
        return null;
    }

    /// <summary>Одна movement action: текущий host, direct hop, Core navigation либо честный visitor frontier.</summary>
    private int Approach(Brain brain, BotNodeFact? target, Goal goal)
    {
        if (target is null) return 2;
        if (brain.Fact.Actor.Node == target.Key)
        { brain.Visited.Put(target.Key, _now, true); return 1; }
        if (_now < brain.Fact.Actor.NextHopDs || brain.Pending is not null || brain.AwaitFactRevision is not null) return 0;
        if (target.DirectEntry || (target.CanTraverse && target.CanRoute))
        {
            brain.State = "в пути";
            Emit(brain, new BotNavigate(target.Key, target.DirectEntry), goal);
            return 0;
        }
        if (QueueFrontierHack(brain, target)) return 0;
        brain.Blacklist.Put(target.Key, Later(_memory.HopBlacklist));
        return 2;
    }

    /// <summary>Frontier goal вытесняет хвост, а не авторизует hop по одному факту физического маршрута.</summary>
    private bool QueueFrontierHack(Brain brain, BotNodeFact target)
    {
        if (!brain.Fact.Context.Visitor || !brain.Fact.Context.CanHack || target.BreachBlocked ||
            Blacklisted(brain, target.Key) || !target.Online || !target.CanRoute) return false;
        Push(brain, "hack", target.Key, null, _goals.Hack, true);
        brain.State = "подбор доступа к следующему узлу";
        return true;
    }

    /// <summary>Точные приоритеты salvage: качество → стоимость; machine disk никогда не читается.</summary>
    public static int SalvageValue(BotFileFact file) => SalvageValue(file, new());

    /// <summary>Параметризованный расчёт ценности; штатные веса повторяют DM и дают тот же порядок.</summary>
    public static int SalvageValue(BotFileFact file, BotSalvageWeights weights)
    {
        var value = file.Kind switch
        {
            "program" => weights.Program, "daemon" => weights.Daemon, "bot" => weights.Bot,
            "key" => weights.Key, "techweb" or "techweb_link" => weights.Techweb, _ => weights.Other
        };
        return value + (file.FortressReward ? weights.FortressBonus : 0) + Math.Min(weights.SizeCap, Math.Max(0, file.Size));
    }

    /// <summary>Максимум 12 файлов одного cache; recycler сохраняет ближайший fresh-debris deadline.</summary>
    private BotFileFact? BestSalvage(Brain brain, BotNodeFact node)
    {
        if (!node.Online) return null;
        BotFileFact? best = null;
        var value = -1;
        foreach (var file in node.Salvage)
        {
            if (!file.Transferable || !file.Fits) continue;
            if (brain.Profile.Id == "recycler" && _now < file.RecycleAfterDs)
            { brain.SalvageWaitUntil = brain.SalvageWaitUntil == 0 ? file.RecycleAfterDs : Math.Min(brain.SalvageWaitUntil, file.RecycleAfterDs); continue; }
            var score = SalvageValue(file, _salvage);
            if (score > value) { best = file; value = score; }
        }
        return best;
    }

    /// <summary>Не более 24 непустых caches за план; cursor в итоге покрывает весь sparse индекс.</summary>
    private NodeKey? FindSalvage(Brain brain, ValueList<string>? scope, bool stationOnly = false)
    {
        brain.SalvageWaitUntil = 0;
        var current = Node(brain, brain.Fact.Actor.Node);
        var local = current is null || (stationOnly && current.Isolated) ? null : BestSalvage(brain, current);
        var best = local is null ? null : current;
        var bestValue = local is null ? -1 : SalvageValue(local, _salvage);
        var bestCost = local is null ? double.PositiveInfinity : 0;
        for (var index = 0; index < Math.Min(24, brain.Debris.Count); index++)
        {
            if (brain.SalvageCursor >= brain.Debris.Count) brain.SalvageCursor = 0;
            var node = brain.Debris[brain.SalvageCursor++];
            if (node == current || Blacklisted(brain, node.Key) || (stationOnly && node.Isolated) || !InScope(node, scope) || !node.CanPlanVisit || !node.CanRoute) continue;
            var file = BestSalvage(brain, node);
            if (file is null) continue;
            var value = SalvageValue(file, _salvage);
            if (value > bestValue || value == bestValue && node.RouteCost < bestCost)
            { best = node; bestValue = value; bestCost = node.RouteCost; }
        }
        return best?.Key;
    }

    /// <summary>Lazy Fisher-Yates перемешивает максимум 24 очередных itinerary slots.</summary>
    private NodeKey? PatrolDestination(Brain brain, ValueList<string>? scope, bool police = false)
    {
        if (brain.PatrolCandidates.Length == 0 || brain.PatrolCursor >= brain.PatrolCandidates.Length)
        {
            brain.PatrolCandidates = brain.Fact.Context.Nodes.Where(node => node.CanRoute).Select(node => node.Key).ToArray();
            brain.PatrolCursor = 0; brain.PatrolShuffledUntil = 0;
        }
        var end = Math.Min(brain.PatrolCandidates.Length, brain.PatrolCursor + 24);
        for (var index = Math.Max(brain.PatrolCursor, brain.PatrolShuffledUntil); index < end; index++)
        {
            var random = Random(brain, index, brain.PatrolCandidates.Length);
            (brain.PatrolCandidates[index], brain.PatrolCandidates[random]) = (brain.PatrolCandidates[random], brain.PatrolCandidates[index]);
        }
        brain.PatrolShuffledUntil = end;
        var best = -1;
        var bestTime = int.MaxValue;
        var bestActivity = int.MinValue;
        for (var index = brain.PatrolCursor; index < end; index++)
        {
            var node = Node(brain, brain.PatrolCandidates[index]);
            if (node is null || !node.CanRoute || node.Isolated || node.Dynamic || !node.Online || Blacklisted(brain, node.Key) ||
                !InScope(node, scope) || !node.CanPlanVisit || police && node.AccessLevel < 2) continue;
            var inspected = police ? _inspected.GetValueOrDefault(node.Key, int.MinValue) : brain.Visited.Get(node.Key) ?? int.MinValue;
            if (best < 0 || inspected < bestTime || inspected == bestTime && node.LastActivityDs > bestActivity)
            { best = index; bestTime = inspected; bestActivity = node.LastActivityDs; }
        }
        if (best < 0) { brain.PatrolCursor = end; return null; }
        var selected = brain.PatrolCandidates[best];
        brain.PatrolCandidates[best] = brain.PatrolCandidates[brain.PatrolCursor];
        brain.PatrolCandidates[brain.PatrolCursor++] = selected;
        return selected;
    }

    /// <summary>Штатный hacker рассматривает максимум 12 home-LAN кандидатов за план.</summary>
    private NodeKey? HackTarget(Brain brain)
    {
        var candidates = brain.Fact.Context.Nodes;
        var home = Node(brain, brain.Fact.Actor.Node)?.NetworkId;
        for (var index = 0; index < Math.Min(12, candidates.Count); index++)
        {
            if (brain.HackCursor >= candidates.Count) brain.HackCursor = 0;
            var node = candidates[brain.HackCursor++];
            if (node.Key != brain.Fact.Actor.Node && node.NetworkId == home && !Blacklisted(brain, node.Key) &&
                node.CanRoute && node.Online && node.AccessLevel < 2 && !node.BreachBlocked) return node.Key;
        }
        return null;
    }

    /// <summary>Custodian обслуживает station fortress только в своей jurisdiction.</summary>
    private NodeKey? FindRepair(Brain brain, ValueList<string>? scope)
    {
        for (var index = 0; index < Math.Min(24, brain.Fortresses.Count); index++)
        {
            if (brain.FortressCursor >= brain.Fortresses.Count) brain.FortressCursor = 0;
            var node = brain.Fortresses[brain.FortressCursor++];
            if (node.FortressFaction == "station" && node.NetworkId is not null && InScope(node, scope) &&
                node.DamagedFortressModules > 0 && !Blacklisted(brain, node.Key)) return node.Key;
        }
        return null;
    }

    /// <summary>Первый действительный server, максимум 24 sparse slots за план.</summary>
    private NodeKey? FindServer(Brain brain)
    {
        var current = Node(brain, brain.Fact.Actor.Node);
        if (current is not null && AcceptsCargo(brain, current)) return current.Key;
        for (var index = 0; index < Math.Min(24, brain.Servers.Count); index++)
        {
            if (brain.ServerCursor >= brain.Servers.Count) brain.ServerCursor = 0;
            var node = brain.Servers[brain.ServerCursor++];
            if (AcceptsCargo(brain, node) && node.CanRoute && node.CanPlanVisit) return node.Key;
        }
        return null;
    }

    /// <summary>Реальный file fit/transfer проверяется общим inventory owner и повторно adapter при исполнении.</summary>
    private bool AcceptsCargo(Brain brain, BotNodeFact node) => node.Type == "server" && !node.Dynamic && !node.Isolated &&
        !Blacklisted(brain, node.Key) && node.Online && node.AcceptsCargo && node.AccessLevel >= 2;

    /// <summary>Не больше 16 relay за план; отсутствие выхода не создаёт teleport.</summary>
    private NodeKey? ExitNode(Brain brain)
    {
        BotNodeFact? best = null;
        for (var index = 0; index < Math.Min(16, brain.Relays.Count); index++)
        {
            if (brain.ExitCursor >= brain.Relays.Count) brain.ExitCursor = 0;
            var node = brain.Relays[brain.ExitCursor++];
            if (!node.RelayEntryAvailable || Blacklisted(brain, node.Key) || !node.CanPlanVisit || !node.CanRoute) continue;
            if (best is null || node.RouteCost < best.RouteCost) best = node;
        }
        return best?.Key;
    }

    /// <summary>Survey считает только новое station устройство с USER; relay/dynamic не приносят progress.</summary>
    private void AdvanceVisitorObjective(Brain brain)
    {
        if (brain.Departing || brain.Objective is null) return;
        var current = Node(brain, brain.Fact.Actor.Node);
        if (brain.Objective == "survey" && current is { Station: true, Dynamic: false, AccessLevel: >= 2 } &&
            current.Type != "relay" && brain.Surveyed.Add(current.Key)) brain.Progress++;
        if (brain.Progress >= brain.Required) { brain.Completed = true; BeginDeparture(brain); }
    }

    /// <summary>Ровно одна grace window 900 DS; повторные сигналы её не продлевают.</summary>
    private void BeginDeparture(Brain brain)
    {
        if (brain.Departing || !brain.Fact.Context.Visitor) return;
        brain.Departing = true;
        ClearGoals(brain);
        brain.ExpiresAt = Later(_memory.GraceWindow);
        Push(brain, "depart_network", null, null, _goals.Depart);
        brain.State = brain.Completed ? "задача выполнена: уход через NTnet" : "срок задания истёк: уход через NTnet";
    }

    /// <summary>Сохранённый claim сохраняет приоритет; новые low-инциденты ждут повторного report.</summary>
    private BotIncidentFact? ClaimIncident(Brain brain)
    {
        var incidents = brain.Fact.Context.Incidents;
        var current = incidents.FirstOrDefault(incident => incident.Id == brain.ClaimedIncident && incident.Open &&
            (incident.ClaimedBy is null || incident.ClaimedBy == brain.Fact.Key));
        if (current is not null) return current;
        ReleaseIncident(brain);
        foreach (var incident in incidents)
        {
            if (!incident.Open || incident.ClaimedBy is { } owner && owner != brain.Fact.Key ||
                incident.Severity < _tuning.IncidentClaimMinSeverity && incident.Reports < _tuning.IncidentClaimMinReports ||
                incident.Node is { } node && Blacklisted(brain, node)) continue;
            if (_incidentClaims.TryGetValue(incident.Id, out var claimed) && claimed != brain.Fact.Key && Find(claimed, out _)) continue;
            _incidentClaims[incident.Id] = brain.Fact.Key;
            brain.ClaimedIncident = incident.Id;
            return incident;
        }
        brain.ClaimedIncident = null;
        return null;
    }

    /// <summary>Отмена и expiry немедленно освобождают claim для другой FIFO задачи полиции.</summary>
    private void ReleaseIncident(Brain brain)
    {
        if (brain.ClaimedIncident is { } id && _incidentClaims.GetValueOrDefault(id) == brain.Fact.Key)
            _incidentClaims.Remove(id);
        brain.ClaimedIncident = null;
    }

    /// <summary>Named suspect имеет приоритет только пока находится на месте и остаётся реальной угрозой.</summary>
    private static BotTargetFact? Suspect(Brain brain, BotIncidentFact incident)
    {
        bool IsSuspect(BotTargetFact target) => target.Actor.Key != brain.Fact.Actor.Actor && target.Actor.Node == brain.Fact.Actor.Node &&
            target.Actor.Valid && (target.FactionId == "rogue" || target.DefenceThreat && target.AccessLevel < 2);
        return brain.Fact.Context.Targets.FirstOrDefault(target => target.Actor.Key == incident.Suspect && IsSuspect(target)) ??
            brain.Fact.Context.Targets.FirstOrDefault(IsSuspect);
    }

    /// <summary>Native адрес без generation никогда не используется как память блокировки.</summary>
    private bool Blacklisted(Brain brain, NodeKey key) => brain.Blacklist.Get(key) > _now;
    /// <summary>Scope null допускает весь Core route domain; isolated проверяется конкретной policy.</summary>
    private static bool InScope(BotNodeFact node, ValueList<string>? scope) => scope is null || node.NetworkId is { } id && scope.Contains(id);
}
