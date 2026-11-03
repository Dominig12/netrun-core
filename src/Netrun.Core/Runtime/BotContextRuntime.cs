using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Entities.Combat;
using Netrun.Core.Storage;
using Netrun.Core.Topology;

namespace Netrun.Core.Runtime;

public sealed partial class NetworkRuntime
{
    private static readonly IReadOnlyDictionary<string, BotProfile> BotContextProfiles =
        BotProfiles.CreateDefaults().ToDictionary(profile => profile.Id, StringComparer.Ordinal);

    private enum BotSnapshotPhase
    {
        Authority, Networks, Grants, Policies, Links, Routes, Programs, Cooldowns, Files,
        Participants, Targets, Nodes, FreezeNodes, FreezeLinks, FreezeTargets, FreezeExecutables, Publish
    }

    private readonly record struct BotRoutePriority(double Cost, string Address) : IComparable<BotRoutePriority>
    {
        public int CompareTo(BotRoutePriority other)
        {
            var result = Cost.CompareTo(other.Cost);
            return result != 0 ? result : StringComparer.Ordinal.Compare(Address, other.Address);
        }
    }

    /// <summary>Снимок строится cooperative из Core owners; wire не поставляет perception или готовую authority.</summary>
    private sealed class BotSnapshotWork(BotConfiguration configuration, ulong revision) : IDisposable
    {
        internal readonly BotConfiguration Configuration = configuration;
        internal readonly ulong Revision = revision;
        internal EntityWorld? World;
        internal GraphSnapshot? Graph;
        internal EntityView? Self, Owner;
        internal CombatSimulation? Combat;
        internal CombatEncounterView? Encounter;
        internal CombatActorStatusView? Status;
        internal StorageWorld? Storage;
        internal StorageDefinition? Store;
        internal ulong WorldVersion, AvailabilityVersion, CostVersion;
        internal ulong? StoreRevision;
        internal int AtDs, Cursor;
        internal BotSnapshotPhase Phase;
        internal EntityAccessAuthority? Authority;
        internal readonly Dictionary<NetworkKey, NetworkAccessPolicy> Networks = [];
        internal readonly Dictionary<NetworkKey, AccessLevel> NetworkLevels = [];
        internal readonly Dictionary<NodeKey, NodeAccessPolicy> Policies = [];
        internal readonly List<BotNodeFact> Nodes = [];
        internal readonly List<BotLink> Links = [];
        internal readonly List<BotTargetFact> Targets = [];
        internal readonly List<BotExecutable> Executables = [];
        internal readonly HashSet<NodeKey> Direct = [], EnemyNodes = [], Settled = [];
        internal readonly Dictionary<NodeKey, int> IceCounts = [];
        internal readonly Dictionary<NodeKey, double> Costs = [];
        internal readonly Dictionary<NodeKey, NodeKey> FirstHops = [];
        internal readonly PriorityQueue<NodeKey, BotRoutePriority> Frontier = new();
        internal NodeKey? Current;
        internal int EdgeCursor, RelayCursor = -1;
        internal bool RelaysExpanded;
        internal ValueList<string>? Programs;
        internal readonly Dictionary<string, int> ProgramCopies = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> ProgramReady = new(StringComparer.Ordinal);
        internal readonly Dictionary<ActorKey, CombatParticipantView> Participants = [];
        internal IEnumerator<StoredFileHeader>? Files;
        internal IEnumerator<EntityView>? Actors;
        internal IEnumerator<KeyValuePair<NodeKey, NodeFact>>? NodeCursor;
        internal BotNodeFact[]? NodeArray;
        internal BotLink[]? LinkArray;
        internal BotTargetFact[]? TargetArray;
        internal BotExecutable[]? ExecutableArray;
        internal ValueList<BotNodeFact>? PublishedNodes;
        internal ValueList<BotLink>? PublishedLinks;
        internal ValueList<BotTargetFact>? PublishedTargets;
        internal ValueList<BotExecutable>? PublishedExecutables;
        internal BotFact? Result;
        internal string? ErrorCode;
        internal bool Complete;

        /// <summary>Exact first hop of the captured Core route; no path traversal or native execution.</summary>
        internal NodeKey? NextHop(NodeKey destination) => FirstHops.GetValueOrDefault(destination);

        public void Dispose()
        {
            Files?.Dispose(); Files = null;
            Actors?.Dispose(); Actors = null;
            NodeCursor?.Dispose(); NodeCursor = null;
        }
    }

    /// <summary>O(1) capture identities/revisions; все обходы выполняются последующими bounded шагами.</summary>
    private BotSnapshotWork StartBotSnapshot(BotConfiguration configuration, ulong revision)
    {
        var work = new BotSnapshotWork(configuration, revision)
        {
            World = _entities, Graph = Graph, Combat = _combat, Storage = _storage,
            AtDs = SimulationTimeDs, AvailabilityVersion = _availability.Revision,
            CostVersion = _pluginRegistry.RouteCostPolicy?.Revision ?? 0
        };
        if (work.World is null || work.Graph is null)
            return FailBotSnapshot(work, "bot_world_unavailable");
        work.Self = work.World.Read(configuration.Actor);
        work.WorldVersion = work.World.StateVersion;
        if (work.Self is not { Alive: true, Kind: EntityKind.Bot })
            return FailBotSnapshot(work, "bot_actor_unavailable");
        if (work.Self.PendingEffectId is not null)
            return FailBotSnapshot(work, "bot_move_pending");
        if (!BotContextProfiles.ContainsKey(configuration.ProgramId) ||
            work.Self.ProfileId is { } profile && profile != configuration.ProgramId)
            return FailBotSnapshot(work, "bot_profile_mismatch");
        if (!BotNodeOnline(work, work.Self.Node) ||
            configuration.Home is { } home && !work.Graph.Nodes.ContainsKey(home))
            return FailBotSnapshot(work, "bot_source_unavailable");
        if (configuration.Inventory is { } inventory)
        {
            work.Store = work.Storage?.ReadStoreDefinition(inventory);
            work.StoreRevision = work.Storage?.ReadStoreRevision(inventory);
            if (work.Store is null || work.Store.Owner != configuration.Actor || work.StoreRevision is null)
                return FailBotSnapshot(work, "bot_inventory_unavailable");
        }
        work.Owner = configuration.Owner is { } owner ? work.World.Read(owner) : null;
        work.Authority = new(configuration.Access, key => work.Graph.LanIds.GetValueOrDefault(key));
        return work;
    }

    /// <summary>Один unit индексирует policy, рассматривает ребро/relay, actor, executable либо узел.</summary>
    private int AdvanceBotSnapshot(BotSnapshotWork work, int budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        if (work.Complete || budget == 0) return 0;
        if (!BotSnapshotCurrent(work)) { FailBotSnapshot(work, "bot_snapshot_changed"); return 1; }
        var used = 0;
        while (used < budget && !work.Complete)
        {
            AdvanceBotSnapshotOne(work);
            used++;
        }
        return used;
    }

    private bool BotSnapshotCurrent(BotSnapshotWork work) =>
        _entities == work.World && work.World?.StateVersion == work.WorldVersion &&
        Graph == work.Graph && _availability.Revision == work.AvailabilityVersion &&
        (_pluginRegistry.RouteCostPolicy?.Revision ?? 0) == work.CostVersion &&
        _combat == work.Combat && SimulationTimeDs == work.AtDs &&
        (work.Configuration.Inventory is not { } inventory ||
            _storage == work.Storage && work.Storage?.ReadStoreRevision(inventory) == work.StoreRevision);

    private static BotSnapshotWork FailBotSnapshot(BotSnapshotWork work, string code)
    {
        work.ErrorCode = code;
        work.Result = null;
        work.Complete = true;
        work.Dispose();
        return work;
    }

    private void NextBotSnapshotPhase(BotSnapshotWork work, BotSnapshotPhase phase)
    {
        work.Phase = phase;
        work.Cursor = 0;
    }

    private void AdvanceBotSnapshotOne(BotSnapshotWork work)
    {
        var configuration = work.Configuration;
        var access = configuration.Access;
        var graph = work.Graph!;
        var self = work.Self!;
        switch (work.Phase)
        {
            case BotSnapshotPhase.Authority:
                work.Authority!.Advance(1);
                if (work.Authority.ErrorCode is { } error) FailBotSnapshot(work, error);
                else if (work.Authority.Ready) NextBotSnapshotPhase(work, BotSnapshotPhase.Networks);
                break;
            case BotSnapshotPhase.Networks:
                if (work.Cursor < access.Networks.Count)
                {
                    var policy = access.Networks[work.Cursor++];
                    work.Networks[policy.Key] = policy;
                    work.NetworkLevels[policy.Key] = work.Authority!.LegitNetworkLevel(policy.Key);
                }
                else NextBotSnapshotPhase(work, BotSnapshotPhase.Grants);
                break;
            case BotSnapshotPhase.Grants:
                if (work.Cursor < access.NetworkGrants.Count)
                {
                    var grant = access.NetworkGrants[work.Cursor++];
                    if (work.Networks.TryGetValue(grant.Network, out var policy) &&
                        (grant.ScopeKeyEpoch is null || grant.ScopeKeyEpoch == policy.KeyEpoch) &&
                        grant.Level > work.NetworkLevels.GetValueOrDefault(grant.Network))
                        work.NetworkLevels[grant.Network] = grant.Level;
                }
                else NextBotSnapshotPhase(work, BotSnapshotPhase.Policies);
                break;
            case BotSnapshotPhase.Policies:
                if (work.Cursor < access.Nodes.Count)
                {
                    var policy = access.Nodes[work.Cursor++];
                    work.Policies.Add(policy.Node, policy);
                }
                else NextBotSnapshotPhase(work, BotSnapshotPhase.Links);
                break;
            case BotSnapshotPhase.Links:
                if (work.Cursor < graph.PhysicalEdges.Count)
                {
                    var edge = graph.PhysicalEdges[work.Cursor++];
                    var available = BotNodeOnline(work, edge.From) && BotNodeOnline(work, edge.To);
                    work.Links.Add(new(edge.From, edge.To, available));
                    if (available && edge.From == self.Node) work.Direct.Add(edge.To);
                    if (available && edge.To == self.Node) work.Direct.Add(edge.From);
                }
                else
                {
                    work.Costs[self.Node] = 0;
                    work.Frontier.Enqueue(self.Node, new(0, graph.Nodes[self.Node].State.Address));
                    NextBotSnapshotPhase(work, BotSnapshotPhase.Routes);
                }
                break;
            case BotSnapshotPhase.Routes:
                AdvanceBotRoutes(work);
                break;
            case BotSnapshotPhase.Programs:
                if (work.Cursor < work.Programs!.Count)
                {
                    var program = work.Programs[work.Cursor++];
                    work.ProgramCopies[program] = work.ProgramCopies.GetValueOrDefault(program) + 1;
                }
                else NextBotSnapshotPhase(work, BotSnapshotPhase.Cooldowns);
                break;
            case BotSnapshotPhase.Cooldowns:
                if (work.Status is { } status && work.Cursor < status.Cooldowns.Count)
                {
                    var cooldown = status.Cooldowns[work.Cursor++];
                    work.ProgramReady[cooldown.ProgramId] = (int)Math.Min(int.MaxValue, cooldown.UntilDs);
                }
                else
                {
                    if (configuration.Inventory is { } store) work.Files = work.Storage!.EnumerateStoreFiles(store).GetEnumerator();
                    NextBotSnapshotPhase(work, BotSnapshotPhase.Files);
                }
                break;
            case BotSnapshotPhase.Files:
                if (work.Files?.MoveNext() == true) AddBotExecutable(work, work.Files.Current);
                else
                {
                    work.Files?.Dispose(); work.Files = null;
                    NextBotSnapshotPhase(work, BotSnapshotPhase.Participants);
                }
                break;
            case BotSnapshotPhase.Participants:
                if (work.Encounter is { } encounter && work.Cursor < encounter.Participants.Count)
                {
                    var participant = encounter.Participants[work.Cursor++];
                    work.Participants[participant.Actor] = participant;
                }
                else
                {
                    work.Actors = work.World!.Enumerate().GetEnumerator();
                    NextBotSnapshotPhase(work, BotSnapshotPhase.Targets);
                }
                break;
            case BotSnapshotPhase.Targets:
                if (work.Actors!.MoveNext()) AddBotTarget(work, work.Actors.Current);
                else
                {
                    work.Actors.Dispose(); work.Actors = null;
                    work.NodeCursor = graph.Nodes.GetEnumerator();
                    NextBotSnapshotPhase(work, BotSnapshotPhase.Nodes);
                }
                break;
            case BotSnapshotPhase.Nodes:
                if (work.NodeCursor!.MoveNext()) AddBotNode(work, work.NodeCursor.Current);
                else
                {
                    work.NodeCursor.Dispose(); work.NodeCursor = null;
                    NextBotSnapshotPhase(work, BotSnapshotPhase.FreezeNodes);
                }
                break;
            case BotSnapshotPhase.FreezeNodes:
                if (work.NodeArray is null) work.NodeArray = new BotNodeFact[work.Nodes.Count];
                else if (work.Cursor < work.Nodes.Count) work.NodeArray[work.Cursor] = work.Nodes[work.Cursor++];
                else
                {
                    work.PublishedNodes = ValueList<BotNodeFact>.TakeOwnership(work.NodeArray);
                    work.NodeArray = null;
                    NextBotSnapshotPhase(work, BotSnapshotPhase.FreezeLinks);
                }
                break;
            case BotSnapshotPhase.FreezeLinks:
                if (work.LinkArray is null) work.LinkArray = new BotLink[work.Links.Count];
                else if (work.Cursor < work.Links.Count) work.LinkArray[work.Cursor] = work.Links[work.Cursor++];
                else
                {
                    work.PublishedLinks = ValueList<BotLink>.TakeOwnership(work.LinkArray);
                    work.LinkArray = null;
                    NextBotSnapshotPhase(work, BotSnapshotPhase.FreezeTargets);
                }
                break;
            case BotSnapshotPhase.FreezeTargets:
                if (work.TargetArray is null) work.TargetArray = new BotTargetFact[work.Targets.Count];
                else if (work.Cursor < work.Targets.Count) work.TargetArray[work.Cursor] = work.Targets[work.Cursor++];
                else
                {
                    work.PublishedTargets = ValueList<BotTargetFact>.TakeOwnership(work.TargetArray);
                    work.TargetArray = null;
                    NextBotSnapshotPhase(work, BotSnapshotPhase.FreezeExecutables);
                }
                break;
            case BotSnapshotPhase.FreezeExecutables:
                if (work.ExecutableArray is null) work.ExecutableArray = new BotExecutable[work.Executables.Count];
                else if (work.Cursor < work.Executables.Count) work.ExecutableArray[work.Cursor] = work.Executables[work.Cursor++];
                else
                {
                    work.PublishedExecutables = ValueList<BotExecutable>.TakeOwnership(work.ExecutableArray);
                    work.ExecutableArray = null;
                    NextBotSnapshotPhase(work, BotSnapshotPhase.Publish);
                }
                break;
            case BotSnapshotPhase.Publish:
                PublishBotSnapshot(work);
                break;
        }
    }

    /// <summary>Cooperative weighted Core search expands the implicit relay bucket once, without a relay clique.</summary>
    private void AdvanceBotRoutes(BotSnapshotWork work)
    {
        var graph = work.Graph!;
        if (work.Current is null)
        {
            if (!work.Frontier.TryDequeue(out var current, out var priority))
            {
                work.Programs = Loadout(work.Configuration.Actor);
                work.Status = work.Combat?.ReadActor(work.Configuration.Actor);
                work.Encounter = work.Combat?.ReadEncounter(work.Configuration.Actor);
                NextBotSnapshotPhase(work, BotSnapshotPhase.Programs);
                return;
            }
            if (work.Settled.Contains(current) || work.Costs[current] != priority.Cost) return;
            work.Settled.Add(current);
            work.Current = current;
            work.EdgeCursor = 0;
            work.RelayCursor = -1;
            return;
        }
        var source = work.Current!;
        if (graph.Adjacency.TryGetValue(source, out var edges) && work.EdgeCursor < edges.Count)
        {
            var edge = edges[work.EdgeCursor++];
            RelaxBotRoute(work, edge.From == source ? edge.To : edge.From,
                _pluginRegistry.RouteCostPolicy?.PhysicalCost(edge) ?? edge.LatencyDs);
            return;
        }
        if (!work.RelaysExpanded && BotRelayAvailable(work, source))
        {
            if (work.RelayCursor < 0) work.RelayCursor = 0;
            if (work.RelayCursor < graph.RelayBucket.Count)
            {
                var relay = graph.RelayBucket[work.RelayCursor++];
                if (BotRelayAvailable(work, relay))
                    RelaxBotRoute(work, relay, _pluginRegistry.RouteCostPolicy?.RelayCost(graph) ?? graph.RelayLatencyDs);
                return;
            }
            work.RelaysExpanded = true;
        }
        work.Current = null;
    }

    private void RelaxBotRoute(BotSnapshotWork work, NodeKey next, double cost)
    {
        if (!double.IsFinite(cost) || cost <= 0)
        { FailBotSnapshot(work, "invalid_route_cost"); return; }
        if (work.Settled.Contains(next) || !BotNodeOnline(work, next) ||
            !work.Authority!.CanTraverse(next, work.Self!.Node)) return;
        var total = work.Costs[work.Current!] + cost;
        if (!double.IsFinite(total)) { FailBotSnapshot(work, "invalid_route_cost"); return; }
        if (work.Costs.TryGetValue(next, out var previous) && previous <= total) return;
        work.Costs[next] = total;
        work.FirstHops[next] = work.Current == work.Self!.Node ? next : work.FirstHops[work.Current!];
        work.Frontier.Enqueue(next, new(total, work.Graph!.Nodes[next].State.Address));
    }

    private bool BotNodeOnline(BotSnapshotWork work, NodeKey node)
    {
        var available = _availability.Get(node);
        return work.Graph!.Nodes.ContainsKey(node) && available.Present && available.Online;
    }

    private bool BotRelayAvailable(BotSnapshotWork work, NodeKey node) =>
        _availability.BackboneEnabled && BotNodeOnline(work, node) &&
        work.Graph!.Nodes[node].State is { Position: not null, Network.IsNtnetRelay: true } &&
        _availability.Get(node).RelayOperational;

    private void AddBotExecutable(BotSnapshotWork work, StoredFileHeader file)
    {
        if (file.Kind != "program" || work.ProgramCopies.GetValueOrDefault(file.Content) <= 0 ||
            work.Combat?.ReadProgram(file.Content) is not { ConsoleUsable: true } ||
            !work.Storage!.CanUseFile(work.Configuration.Actor, work.Configuration.Inventory!, file.File)) return;
        work.Executables.Add(new(file.Content, file.File.Id, work.ProgramReady.GetValueOrDefault(file.Content),
            work.Combat.CooldownRemaining(work.Configuration.Actor, file.Content)));
        work.ProgramCopies[file.Content]--;
    }

    private void AddBotTarget(BotSnapshotWork work, EntityView view)
    {
        if (!view.Alive || view.PendingEffectId is not null || !BotNodeOnline(work, view.Node)) return;
        if (view.Kind == EntityKind.Ice) work.IceCounts[view.Node] = work.IceCounts.GetValueOrDefault(view.Node) + 1;
        var hostile = BotFactionsHostile(work.Self!.FactionId, view.FactionId);
        if (hostile) work.EnemyNodes.Add(view.Node);
        if (view.Actor == work.Self.Actor || view.Node != work.Self.Node) return;
        var status = work.Combat?.ReadActor(view.Actor);
        var participant = work.Participants.GetValueOrDefault(view.Actor);
        var defenceThreat = view.Kind == EntityKind.Resident || view.Kind == EntityKind.Bot &&
            view.ProfileId is { } profile && BotContextProfiles.TryGetValue(profile, out var metadata) && !metadata.Sanctioned;
        var fact = new CombatActorFact(view.Actor, view.Node, view.Integrity, view.MaxIntegrity, Loadout(view.Actor),
            view.Kind == EntityKind.Ice ? CombatActorKind.Ice : CombatActorKind.Entity,
            InitialShieldFactor: view.ShieldFactor,
            InitialShieldUntilDs: view.ShieldedByTurn ? long.MaxValue : view.ShieldUntilDs,
            InitialStunUntilDs: view.StunnedByTurn ? long.MaxValue : view.StunnedUntilDs);
        // Credentials of another actor are not inferred from this bot's raw access snapshot.
        work.Targets.Add(new(fact, view.FactionId, view.PrincipalId, hostile, defenceThreat,
            AccessLevel: (int)AccessLevel.User, IsBot: view.Kind == EntityKind.Bot,
            PendingProgram: status?.Cast is not null, StunImmune: participant?.StunImmune ?? false,
            IgnoreUntilDs: view.CombatIgnoreUntilDs));
    }

    private void AddBotNode(BotSnapshotWork work, KeyValuePair<NodeKey, NodeFact> entry)
    {
        var node = entry.Key;
        var reachable = work.Costs.TryGetValue(node, out var cost);
        var policy = work.Policies.GetValueOrDefault(node);
        var traversable = work.Authority!.CanTraverse(node, work.Self!.Node, reachable);
        var online = BotNodeOnline(work, node);
        work.Nodes.Add(new(node, work.Graph!.LanIds.GetValueOrDefault(node), entry.Value.State.NodeType)
        {
            Online = online,
            Station = entry.Value.State.Network?.IsStationLevel ?? false,
            AccessLevel = (int)work.Authority.NodeLevel(node, work.Self.Node, reachable),
            NetworkLevel = policy?.Network is { } network ? (int)work.NetworkLevels.GetValueOrDefault(network) : 0,
            CanTraverse = traversable,
            CanPlanVisit = online && reachable && traversable,
            CanRoute = reachable,
            RouteCost = reachable ? cost : double.PositiveInfinity,
            DirectEntry = traversable && online && node != work.Self.Node &&
                (work.Direct.Contains(node) || BotRelayAvailable(work, work.Self.Node) && BotRelayAvailable(work, node)),
            IceCount = work.IceCounts.GetValueOrDefault(node),
            HasEnemy = work.EnemyNodes.Contains(node),
            FortressFaction = policy?.Fortress?.FactionId
        });
    }

    private void PublishBotSnapshot(BotSnapshotWork work)
    {
        if (!BotSnapshotCurrent(work)) { FailBotSnapshot(work, "bot_snapshot_changed"); return; }
        var configuration = work.Configuration;
        var ownerAvailable = work.Owner is { Alive: true, PendingEffectId: null };
        var context = new BotContext
        {
            ConsoleAvailable = configuration.ConsoleAvailable,
            GoalsEnabled = configuration.GoalsEnabled,
            Home = configuration.Home ?? work.Self!.Node,
            LaunchedByOwner = configuration.Owner is not null,
            Orphaned = configuration.Owner is not null && !ownerAvailable,
            OwnerAvailable = ownerAvailable,
            OwnerReachable = ownerAvailable && work.Costs.ContainsKey(work.Owner!.Node),
            Locked = configuration.Access.Principal.Locked,
            Encounter = work.Encounter,
            NextProgramDs = (int)Math.Min(int.MaxValue, work.Status?.NextProgramDs ?? 0),
            Nodes = work.PublishedNodes!,
            Links = work.PublishedLinks!,
            Targets = work.PublishedTargets!,
            Executables = work.PublishedExecutables!
        };
        work.Result = new(configuration.Bot, work.Revision, work.Self!, configuration.ProgramId, context, configuration.Behavior);
        work.Complete = true;
        work.Dispose();
    }

    /// <summary>Штатная таблица factions.dm; неизвестный mod family не создаёт выдуманной вражды.</summary>
    private static bool BotFactionsHostile(string first, string second)
    {
        if (first == second) return false;
        var left = BotFactionFamily(first);
        var right = BotFactionFamily(second);
        return left is not null && right is not null && (Enemy(left, right) || Enemy(right, left));

        static bool Enemy(string source, string target) => source switch
        {
            "rogue" => target is "station" or "owner" or "redshift" or "collective" or "archivists",
            "redshift" => target is "station" or "owner" or "collective" or "archivists",
            "collective" or "archivists" => target == "redshift",
            _ => false
        };
    }

    private static string? BotFactionFamily(string faction) =>
        faction.StartsWith("owner:", StringComparison.Ordinal) ? "owner" :
        faction is "owner" or "station" or "rogue" or "redshift" or "collective" or "archivists" ? faction : null;
}
