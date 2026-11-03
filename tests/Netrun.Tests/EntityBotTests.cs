using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Entities.Combat;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет перенос настоящего brain lifecycle, FIFO wake, signal goals и delivered command outcomes.</summary>
public sealed class EntityBotTests
{
    private static readonly NodeKey Home = new("home", 1);
    private static readonly ActorKey Actor = new("actor", 1);
    private static readonly BotKey Bot = new("brain", 1);

    /// <summary>Общий owner хранит HP; тест передаёт только read-model EntityWorld.</summary>
    private static EntityView View(ActorKey? actor = null, NodeKey? node = null, ulong revision = 1,
        int stun = 0, int? expiry = null) => new(actor ?? Actor, revision, revision, EntityKind.Bot, node ?? Home,
        100, 100, new("Bot"), "owner", "principal", "custom", true, stun, 0, 1, 0, true, null, expiry);

    /// <summary>ACL/route facts уже вычислены Core owner; DM не задаёт готовое brain решение.</summary>
    private static BotNodeFact Node(string id = "home", string? network = "lan", string type = "server") =>
        new(new(id, 1), network, type)
        {
            AccessLevel = 2,
            NetworkLevel = 1,
            DirectEntry = id == "home",
            CanTraverse = true,
            CanPlanVisit = true,
            CanRoute = true,
            RouteCost = id == "home" ? 0 : 1,
            Commands = new(["scan", "toggle", "fort_repair", "packet_log", "countertrace"])
        };

    /// <summary>Минимальный actor snapshot с настоящим default profile.</summary>
    private static BotFact Fact(string profile = "custom", BotContext? context = null, ulong revision = 1,
        EntityView? actor = null, BotKey? key = null, BotBehavior? behavior = null) =>
        new(key ?? Bot, revision, actor ?? View(), profile,
            context ?? new() { Home = Home, Nodes = new([Node()]) }, behavior);

    /// <summary>Выдача проверяется отдельно от результата, как у настоящего duplex adapter.</summary>
    private static BotEffect Take(BotEngine engine)
    { Assert.True(engine.TryDequeueEffect(out var effect)); return effect; }

    /// <summary>Точный ACK сохраняет все causal identity поля выданного решения.</summary>
    private static BotEffectResult Result(BotEffect effect, string phase = "completed", string? response = null) =>
        new(effect.Bot, effect.Actor, effect.ActionSerial, effect.ExpectedRevision, phase, response);

    /// <summary>Перенос не превращает разные loadout/interval/authority профили в один упрощённый бот.</summary>
    [Fact]
    public void ProfilesPreserveAuthoredStatsAndLoadouts()
    {
        var profiles = BotProfiles.CreateDefaults().ToDictionary(profile => profile.Id);
        Assert.Equal(17, profiles.Count);
        Assert.Equal(128, profiles["recycler"].Storage);
        Assert.Equal(100, profiles["recycler"].ThinkIdleDs);
        Assert.Equal(160, profiles["police"].MaxIntegrity);
        Assert.True(profiles["police"].Sanctioned);
        Assert.Equal(4800, profiles["police"].LifetimeDs);
        Assert.Equal(new[] { "autobreach", "icepick", "cloak" }, profiles["breacher"].Daemons);
        Assert.Equal(0, profiles["redshift_hacker"].FileSize);
        Assert.Empty(profiles["custom"].Programs);
        Assert.False(profiles["custom"].Sanctioned);
    }

    /// <summary>Повторный wake коалесцируется и не обгоняет ранее admitted задачу другого бота.</summary>
    [Fact]
    public void ReadyTasksRemainFifoAndOnePendingEffectDoesNotBlockOthers()
    {
        using var engine = new BotEngine();
        var secondKey = new BotKey("second", 1);
        Assert.True(engine.Upsert(Fact()));
        Assert.True(engine.Upsert(Fact(key: secondKey, actor: View(new("second-actor", 1)))));
        Assert.True(engine.SetOrder(Bot, new("command", Home, "toggle"), 0));
        Assert.True(engine.SetOrder(secondKey, new("command", Home, "scan"), 0));
        Assert.Equal(1, engine.Advance(0, 1));
        Assert.Equal(Bot, Take(engine).Bot);
        engine.Wake(Bot); engine.Wake(Bot);
        Assert.Equal(1, engine.Advance(0, 1));
        Assert.Equal(secondKey, Take(engine).Bot);
        engine.Advance(0, 10);
        Assert.False(engine.TryDequeueEffect(out _));
    }

    /// <summary>Queued/invoked — только progress, terminal response чужого actor/serial не завершает цель.</summary>
    [Fact]
    public void CommandsWaitForTheirOwnOutcomeAndOneShotOrderBecomesGuard()
    {
        using var engine = new BotEngine();
        engine.Upsert(Fact());
        engine.SetOrder(Bot, new("command", Home, "toggle"), 0);
        engine.Advance(0, 1);
        var effect = Take(engine);
        Assert.IsType<BotCommand>(effect.Action);
        Assert.False(engine.ApplyResult(Result(effect) with { Actor = new("someone-else", 1) }));
        Assert.False(engine.ApplyResult(Result(effect) with { ActionSerial = effect.ActionSerial + 1 }));
        Assert.False(engine.ApplyResult(Result(effect) with { ExpectedRevision = 99 }));
        Assert.True(engine.ApplyResult(Result(effect, "queued")));
        Assert.True(engine.ApplyResult(Result(effect, "invoked")));
        engine.Advance(100, 10);
        Assert.False(engine.TryDequeueEffect(out _));
        Assert.True(engine.ApplyResult(Result(effect, response: "delivered")));
        engine.Advance(100, 1);
        Assert.Equal("guard", engine.Query(Bot)!.Order);
        Assert.Empty(engine.Query(Bot)!.Goals);
        engine.Advance(200, 10);
        Assert.False(engine.TryDequeueEffect(out _));
    }

    /// <summary>Потерянный terminal response не запускает mutation повторно после 300 DS watchdog.</summary>
    [Fact]
    public void UncertainCommandIsNotAutomaticallyRetried()
    {
        using var engine = new BotEngine();
        engine.Upsert(Fact());
        engine.SetOrder(Bot, new("command", Home, "toggle"), 0);
        engine.Advance(0, 1);
        Take(engine);
        engine.Advance(300, 1);
        Assert.Equal("guard", engine.Query(Bot)!.Order);
        engine.Advance(10_000, 100);
        Assert.False(engine.TryDequeueEffect(out _));
    }

    /// <summary>Старое поколение не получает ACK, mutation либо память после replacement/removal.</summary>
    [Fact]
    public void RecycledBrainRejectsOldResultsAndOldGenerationFacts()
    {
        using var engine = new BotEngine();
        engine.Upsert(Fact());
        engine.SetOrder(Bot, new("command", Home, "toggle"), 0);
        engine.Advance(0, 1);
        var effect = Take(engine);
        var replacement = Fact(key: Bot with { Generation = 2 }, actor: View(Actor with { Generation = 2 }));
        Assert.True(engine.Upsert(replacement));
        Assert.False(engine.ApplyResult(Result(effect)));
        Assert.False(engine.Upsert(Fact(revision: 99)));
        Assert.False(engine.Remove(Bot));
        Assert.True(engine.Remove(replacement.Key));
        Assert.False(engine.Upsert(replacement with { Revision = 100 }));
    }

    /// <summary>Would-block сохраняет команду и первый serial до освобождения output slot.</summary>
    [Fact]
    public void OutputPressureDoesNotLoseOrPrematurelyConsumeCommand()
    {
        using var engine = new BotEngine(new(MaxEffects: 1));
        var second = new BotKey("second", 1);
        engine.Upsert(Fact()); engine.Upsert(Fact(key: second, actor: View(new("other", 1))));
        engine.SetOrder(Bot, new("command", Home, "scan"), 0);
        engine.SetOrder(second, new("command", Home, "toggle"), 0);
        engine.Advance(0, 2);
        Take(engine);
        Assert.Null(engine.Query(second)!.PendingEffect);
        engine.Wake(second);
        engine.Advance(0, 1);
        var admitted = Take(engine);
        Assert.Equal(second, admitted.Bot);
        Assert.Equal(1ul, admitted.ActionSerial);
    }

    /// <summary>Guard at post спит до абсолютного срока, а не опрашивает узел каждые think_busy.</summary>
    [Fact]
    public void GuardHasOneDeadlineAndNoPeriodicPolling()
    {
        using var engine = new BotEngine();
        engine.Upsert(Fact());
        engine.SetOrder(Bot, new("guard", Home), 0);
        engine.Advance(0, 1);
        Assert.Equal(300, engine.Query(Bot)!.NextWakeDs);
        Assert.Equal(0, engine.Advance(100, 100));
        Assert.False(engine.TryDequeueEffect(out _));
    }

    /// <summary>Оглушение не продлевает срок ожидающей цели и не сохраняет one-shot command.</summary>
    [Fact]
    public void GoalDeadlineExpiresBeforeStunPriority()
    {
        using var engine = new BotEngine();
        engine.Upsert(Fact(actor: View(stun: 2000)));
        engine.SetOrder(Bot, new("command", Home, "toggle"), 0);
        engine.Advance(0, 1);
        engine.Advance(1200, 1);
        Assert.Equal("оглушён", engine.Query(Bot)!.State);
        Assert.Equal("guard", engine.Query(Bot)!.Order);
        Assert.Empty(engine.Query(Bot)!.Goals);
    }

    /// <summary>Salvage качество важнее расстояния, затем меньшая стоимость Core route tree разрывает ничью.</summary>
    [Fact]
    public void CollectorRanksSalvageQualityBeforeDistance()
    {
        var near = Node("near") with { RouteCost = 1, Salvage = new([new("key", "key", 1)]) };
        var far = Node("far") with { RouteCost = 100, Salvage = new([new("program", "program", 2)]) };
        using var engine = new BotEngine();
        engine.Upsert(Fact("collector", new() { Home = Home, Nodes = new([Node(), near, far]) }));
        engine.Advance(0, 1);
        var movement = Assert.IsType<BotNavigate>(Take(engine).Action);
        Assert.Equal(far.Key, movement.Destination);
        Assert.Equal(252, BotEngine.SalvageValue(new("reward", "program", 2, FortressReward: true)));
    }

    /// <summary>Recycler сохраняет окно игрока и ставит точный deadline свежих обломков.</summary>
    [Fact]
    public void RecyclerHonorsFreshSalvageGraceWithoutBusyPolling()
    {
        var home = Node() with { Salvage = new([new("fresh", "program", 2, RecycleAfterDs: 200)]) };
        using var engine = new BotEngine();
        engine.Upsert(Fact("recycler", new() { Home = Home, Nodes = new([home]) }));
        engine.Advance(0, 1);
        Assert.Equal("recycling_wait", Assert.Single(engine.Query(Bot)!.Goals).Kind);
        Assert.Equal(200, engine.Query(Bot)!.NextWakeDs);
        Assert.Equal(0, engine.Advance(199, 1));
        engine.Advance(200, 1);
        var recover = Assert.IsType<BotRecover>(Take(engine).Action);
        Assert.Equal("fresh", recover.FileUid);
    }

    /// <summary>Expiry visitor начинает ровно одну grace window, отсутствие relay не создаёт teleport.</summary>
    [Fact]
    public void VisitorLifetimeStartsBoundedPhysicalDeparture()
    {
        using var engine = new BotEngine();
        engine.Upsert(Fact("raider", new() { Visitor = true, Home = Home, Nodes = new([Node(type: "relay")]) }, actor: View(expiry: 100)));
        engine.Advance(0, 1);
        engine.Advance(100, 1);
        var view = engine.Query(Bot)!;
        Assert.True(view.VisitorDeparting);
        Assert.False(engine.TryDequeueEffect(out _));
        engine.Wake(Bot); engine.Advance(500, 1);
        engine.Advance(1000, 1);
        Assert.IsType<BotDissolve>(Take(engine).Action);
    }

    /// <summary>Hack ждёт сигнала, не присваивает USER и не переиздаёт один network frontier.</summary>
    [Fact]
    public void HackingGoalWaitsForItsOwnBreachOutcome()
    {
        var frontier = Node("locked") with { AccessLevel = 0, NetworkLevel = 0, CanTraverse = false, Frontier = new("locked", 1) };
        using var engine = new BotEngine();
        engine.Upsert(Fact("hacker", new() { Home = Home, CanHack = true, Nodes = new([Node(), frontier]) }));
        Assert.True(engine.SetOrder(Bot, new("hack", frontier.Key), 0));
        engine.Advance(0, 1);
        var effect = Take(engine);
        Assert.Equal("network", Assert.IsType<BotStartBreach>(effect.Action).Kind);
        Assert.True(Assert.Single(engine.Query(Bot)!.Goals).Waiting);
        Assert.True(engine.ApplyResult(Result(effect, "invoked")));
        Assert.Equal(0, engine.Advance(100, 10));
        Assert.False(engine.TryDequeueEffect(out _));
        Assert.Equal(0, frontier.AccessLevel);
        engine.Advance(3000, 1);
        Assert.DoesNotContain(engine.Query(Bot)!.Goals, goal => goal.Kind == "hack");
    }

    /// <summary>Report использует только delivered response текущей команды и ограничивает Unicode текст.</summary>
    [Fact]
    public void SurveyStoresDeliveredResponseInsteadOfReadingNodeJournal()
    {
        using var engine = new BotEngine();
        engine.Upsert(Fact("surveyor"));
        engine.Advance(0, 1);
        var scan = Take(engine);
        Assert.Equal("scan", Assert.IsType<BotCommand>(scan.Action).CommandId);
        engine.ApplyResult(Result(scan, response: new string('x', 1200)));
        engine.Advance(0, 1);
        var report = Assert.IsType<BotStoreReport>(Take(engine).Action);
        Assert.Equal("survey-home.txt", report.Name);
        Assert.Equal(1000, report.Text.Split('\n')[1].Length);
    }

    /// <summary>Own-turn policy выбирает достаточный дешёвый урон по AP, сохраняя сильные программы для иных целей.</summary>
    [Fact]
    public void CombatPolicyUsesSharedProgramValuesAndOwnTurnBudget()
    {
        var target = new ActorKey("enemy", 1);
        var encounter = new CombatEncounterView(Home, "decision", "join", 1, 0, Actor, 120, false, false,
            new([new(Actor, 3, 1, true, 0, false, 0, 1, new([]))]));
        var context = new BotContext
        {
            Home = Home,
            Nodes = new([Node()]),
            Encounter = encounter,
            Executables = new([new("strike", "strike-file"), new("needle", "needle-file")]),
            Targets = new([new(new(target, Home, 10, 10, new([])), "rogue", FactionHostile: true)])
        };
        using var engine = new BotEngine();
        engine.Upsert(Fact(context: context, behavior: new(Caution: 0)));
        engine.Advance(0, 1);
        var combat = Assert.IsType<BotCombat>(Take(engine).Action);
        var program = Assert.IsType<UseCombatProgram>(combat.Intent);
        Assert.Equal("needle", program.ProgramId);
        Assert.Equal("decision", program.Token);
    }
}
