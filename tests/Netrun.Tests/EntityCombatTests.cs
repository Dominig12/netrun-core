using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Combat;
using Xunit;

namespace Netrun.Tests;

/// <summary>Паритет правил turn_combat/programs.dm без native mobs, таймеров BYOND или HP-владельца в симуляции.</summary>
public sealed class EntityCombatTests
{
    private static readonly ActorKey First = new("first", 1), Second = new("second", 1), Third = new("third", 1);
    private static readonly NodeKey Node = new("node", 1), OtherNode = new("other", 1);
    private static readonly ValueList<string> AllPrograms = new(CombatPrograms.CreateDefaults().Select(program => program.Id)
        .Concat(["custom", "cooldown", "long_cast", "small_emp"]));

    /// <summary>Полиморфная программа имеет собственный статусный hook и эффект независимо от Role/Power.</summary>
    private sealed record CustomProgram : CombatProgram
    {
        public override bool CanApply(CombatProgramContext context) => context.Target.Integrity > 20;
        public override CombatProgramApplication Apply(CombatProgramContext context) => new(new([
            new(CombatEffectKind.Damage, context.Target.Key, context.Source, Math.Floor(9 * context.DamageScale), ProgramId: Id)
        ]));
    }

    private sealed class Scene
    {
        internal readonly CombatSimulation Core;
        internal long Now;
        internal Scene(CombatActorKind secondKind = CombatActorKind.Entity, double firstShield = 1, long firstShieldUntil = 0)
        {
            Core = new(CombatPrograms.CreateDefaults().Concat([
                new CustomProgram { Id = "custom", Role = "unusual", Power = 999, ApCost = 1, CooldownTurns = 2, Consumable = true },
                new CombatProgram { Id = "cooldown", ApCost = 2, CooldownTurns = 2 },
                new AttackCombatProgram { Id = "long_cast", Power = 50, ApCost = 2, WindupDs = 200, Hostile = true },
                new StunCombatProgram { Id = "small_emp", Role = "stun", Power = 60, ApCost = 1, StunTurns = 2, Hostile = true }
            ]));
            Core.UpsertActor(new(First, Node, 100, 100, AllPrograms, InitialShieldFactor: firstShield, InitialShieldUntilDs: firstShieldUntil));
            Core.UpsertActor(new(Second, Node, 100, 100, AllPrograms, secondKind));
            Core.UpsertActor(new(Third, Node, 100, 100, AllPrograms));
        }
        internal CombatResult Send(CombatIntent intent)
        {
            var result = Core.Apply(intent, Now);
            Core.Advance(Now, 10000);
            return result;
        }
        internal void Start() => Assert.True(Send(new AdmitCombatAction(First, Second, 1)).Accepted);
        internal CombatEncounterView View => Core.ReadEncounter(First)!;
        internal CombatEffect[] Drain()
        {
            var result = new List<CombatEffect>();
            while (Core.TryDequeueEffect(out var effect)) result.Add(effect!);
            return result.ToArray();
        }
        internal void Pass(ActorKey actor) => Assert.True(Send(new PassCombatTurn(actor)).Accepted);
    }

    [Fact]
    public void QueueApDeferredActionAndOwnTurnCooldownMatchDm()
    {
        var scene = new Scene();
        scene.Start();
        Assert.Equal(First, scene.View.Current);
        Assert.Equal(3, scene.Core.RemainingAp(First));
        var deadline = scene.View.DeadlineDs;
        Assert.False(scene.Send(new SpendCombatAp(First, 0)).Accepted);
        Assert.False(scene.Send(new SpendCombatAp(Second, 1)).Accepted);
        Assert.True(scene.Send(new SpendCombatAp(First, 2, Deferred: true)).Accepted);
        Assert.True(scene.View.Deferred);
        Assert.False(scene.Send(new PassCombatTurn(First)).Accepted);
        Assert.True(scene.Send(new ResolveCombatAction(First)).Accepted);
        Assert.Equal(1, scene.Core.RemainingAp(First));
        Assert.Equal(deadline, scene.View.DeadlineDs);
        scene.Pass(First); scene.Pass(Second);
        Assert.True(scene.Send(new UseCombatProgram(First, "cooldown", Second)).Accepted);
        Assert.Equal(1, scene.Core.RemainingAp(First));
        Assert.Equal(2UL, scene.Core.CooldownRemaining(First, "cooldown"));
        scene.Pass(First); scene.Pass(Second);
        Assert.Equal(1UL, scene.Core.CooldownRemaining(First, "cooldown"));
        // Hostile activity intentionally restarts the quiet ledger before the third peaceful rotation.
        Assert.True(scene.Send(new UseCombatProgram(First, "sting", Second)).Accepted);
        scene.Pass(First); scene.Pass(Second);
        Assert.Equal(0UL, scene.Core.CooldownRemaining(First, "cooldown"));
        Assert.Equal(3, scene.Core.RemainingAp(First));
    }

    [Fact]
    public void OldDecisionCannotSpendLaterOwnTurnOrCreateReplacementEncounter()
    {
        var scene = new Scene();
        scene.Start();
        var old = scene.View.DecisionId;
        scene.Pass(First); scene.Pass(Second);
        Assert.NotEqual(old, scene.View.DecisionId);
        Assert.False(scene.Send(new PassCombatTurn(First, old)).Accepted);
        Assert.False(scene.Send(new UseCombatProgram(First, "sting", Second, old)).Accepted);
        Assert.Equal(3, scene.Core.RemainingAp(First));
        var live = scene.View.DecisionId;
        Assert.True(scene.Send(new UseCombatProgram(First, "sting", Second, live)).Accepted);
        Assert.Equal(live, scene.View.DecisionId);
        scene.Core.RemoveNode(Node, scene.Now);
        Assert.False(scene.Send(new UseCombatProgram(First, "sting", Second, live)).Accepted);
        Assert.Equal(0, scene.Core.EncounterCount);
        scene.Start();
        Assert.NotEqual(old, scene.View.DecisionId);
    }

    [Fact]
    public void StableJoinTokenSurvivesTurnRotationAndAppendsOnlyCaller()
    {
        var scene = new Scene();
        scene.Start();
        var oldDecision = scene.View.DecisionId;
        var join = scene.View.JoinId;
        scene.Pass(First); scene.Pass(Second);
        Assert.False(scene.Send(new JoinCombat(Third, OtherNode, join)).Accepted);
        Assert.True(scene.Send(new JoinCombat(Third, Node, oldDecision)).Accepted);
        Assert.Equal(First, scene.View.Current);
        Assert.Equal(3, scene.Core.RemainingAp(First));
        Assert.Equal(3, scene.View.Participants.Count);
        Assert.False(scene.Send(new JoinCombat(Third, Node, join)).Accepted);
        scene.Pass(First); scene.Pass(Second);
        Assert.Equal(Third, scene.Core.ReadEncounter(Third)!.Current);
        scene.Core.RemoveActor(Second, scene.Now);
        Assert.Equal(Third, scene.Core.ReadEncounter(Third)!.Current);
    }

    [Fact]
    public void RejectedOffTurnOrUnaffordableAttackDoesNotEnrollNeutralTarget()
    {
        var scene = new Scene();
        scene.Start();
        Assert.False(scene.Send(new UseCombatProgram(Second, "sting", Third)).Accepted);
        Assert.Null(scene.Core.ReadEncounter(Third));
        Assert.True(scene.Send(new SpendCombatAp(First, 2)).Accepted);
        Assert.False(scene.Send(new UseCombatProgram(First, "surge", Third)).Accepted);
        Assert.Null(scene.Core.ReadEncounter(Third));
        Assert.Equal(2, scene.View.Participants.Count);
        Assert.Equal(1, scene.Core.RemainingAp(First));
    }

    [Fact]
    public void ExpiredInputCannotActBeforeTimeoutWorkIsPumped()
    {
        var scene = new Scene();
        scene.Start(); scene.Drain();
        scene.Now = scene.View.DeadlineDs;
        scene.Core.Advance(scene.Now, 0);
        Assert.Equal(0, scene.Core.RemainingAp(First));
        var rejected = scene.Core.Apply(new UseCombatProgram(First, "sting", Second), scene.Now);
        Assert.False(rejected.Accepted);
        Assert.Equal("decision_expired", rejected.Code);
        Assert.DoesNotContain(scene.Drain(), effect => effect.Kind == CombatEffectKind.Damage);
        scene.Core.Advance(scene.Now, 10);
        Assert.Equal(Second, scene.View.Current);
        Assert.Equal(3, scene.Core.RemainingAp(Second));
    }

    [Fact]
    public void ShieldsSurviveThinkingAndExpireAtNextOwnTurn()
    {
        var scene = new Scene(); scene.Start(); scene.Drain();
        Assert.True(scene.Send(new UseCombatProgram(First, "barrier")).Accepted);
        Assert.Equal(long.MaxValue, Assert.Single(scene.Drain(), effect => effect.Kind == CombatEffectKind.Shield).UntilDs);
        scene.Pass(First); scene.Drain();
        scene.Now = 60;
        Assert.True(scene.Send(new UseCombatProgram(Second, "sting", First)).Accepted);
        Assert.Equal(4, Assert.Single(scene.Drain(), effect => effect.Kind == CombatEffectKind.Damage).Value);
        scene.Pass(Second);
        var expired = Assert.Single(scene.Drain(), effect => effect.Kind == CombatEffectKind.Shield);
        Assert.Equal(scene.Now, expired.UntilDs);
        Assert.Equal(First, scene.View.Current);
    }

    [Fact]
    public void WeakerShieldCannotAdoptOrExtendStrongerFinitePreCombatBuff()
    {
        var scene = new Scene(firstShield: 0.3, firstShieldUntil: 50);
        scene.Start(); scene.Drain();
        Assert.False(scene.Send(new UseCombatProgram(First, "shell")).Accepted);
        Assert.Equal(3, scene.Core.RemainingAp(First));
        Assert.DoesNotContain(scene.Drain(), effect => effect.Kind == CombatEffectKind.Shield);
        Assert.Equal(0UL, scene.Core.CooldownRemaining(First, "shell"));
    }

    [Fact]
    public void StunSkipsOwnTurnAndImmunityLastsThroughRealOpportunity()
    {
        var scene = new Scene(); scene.Start();
        Assert.True(scene.Send(new UseCombatProgram(First, "jam", Second)).Accepted);
        scene.Pass(First);
        Assert.Equal(First, scene.View.Current);
        Assert.True(scene.View.Participants.Single(participant => participant.Actor == Second).StunImmune);
        Assert.False(scene.Send(new UseCombatProgram(First, "blackout", Second)).Accepted);
        scene.Pass(First);
        Assert.Equal(Second, scene.View.Current);
        Assert.True(scene.View.Participants.Single(participant => participant.Actor == Second).StunImmune);
        scene.Pass(Second);
        Assert.False(scene.View.Participants.Single(participant => participant.Actor == Second).StunImmune);
    }

    [Fact]
    public void AllStunnedQueueAdvancesInBudgetedStepsAndRetainsOneDeadline()
    {
        var scene = new Scene(); scene.Start();
        Assert.True(scene.Send(new UseCombatProgram(First, "small_emp", Second)).Accepted);
        Assert.True(scene.Send(new UseCombatProgram(First, "small_emp", First)).Accepted);
        Assert.True(scene.Core.Apply(new PassCombatTurn(First), scene.Now).Accepted);
        Assert.Equal(1, scene.Core.Advance(scene.Now, 1));
        Assert.True(scene.View.Advancing);
        scene.Core.Advance(scene.Now, 10);
        Assert.False(scene.View.Advancing);
        Assert.True(scene.View.Round <= 3);
        Assert.Equal(0, scene.Core.RemainingAp(scene.View.Current!));
        scene.Now = scene.View.DeadlineDs;
        scene.Core.Advance(scene.Now, 10);
        Assert.True(scene.Core.RemainingAp(scene.View.Current!) > 0);
    }

    [Fact]
    public void TelegraphSpendsOnceResolvesOnlyAtDueAndRetainsRemainingAp()
    {
        var scene = new Scene(CombatActorKind.Ice);
        scene.Start(); scene.Pass(First); scene.Drain();
        Assert.True(scene.Send(new UseCombatProgram(Second, "strike", First, Telegraph: true, DamageScale: 0.5)).Accepted);
        Assert.True(scene.View.Deferred);
        Assert.Equal(1, scene.Core.RemainingAp(Second));
        var started = Assert.Single(scene.Drain(), effect => effect.Kind == CombatEffectKind.CastStarted);
        Assert.Equal(5, started.UntilDs);
        Assert.False(scene.Send(new ResolveCombatCast(Second)).Accepted);
        Assert.DoesNotContain(scene.Drain(), effect => effect.Kind == CombatEffectKind.Damage);
        Assert.False(scene.Send(new PassCombatTurn(Second)).Accepted);
        scene.Now = 5;
        scene.Core.Advance(scene.Now, 100);
        Assert.Equal(11, Assert.Single(scene.Drain(), effect => effect.Kind == CombatEffectKind.Damage).Value);
        Assert.Equal(1, scene.Core.RemainingAp(Second));
        Assert.False(scene.View.Deferred);
        Assert.False(scene.Send(new ResolveCombatCast(Second)).Accepted);
    }

    [Fact]
    public void DeadlineCancelsUnfinishedCastAndStaleTimerCannotDeliverIt()
    {
        var scene = new Scene(CombatActorKind.Ice);
        Assert.True(scene.Send(new UseCombatProgram(Second, "long_cast", First, Telegraph: true)).Accepted);
        Assert.Equal(200, Assert.Single(scene.Drain(), effect => effect.Kind == CombatEffectKind.CastStarted).UntilDs);
        scene.Now = 120;
        scene.Core.Advance(scene.Now, 100);
        var timeout = scene.Drain();
        Assert.Contains(timeout, effect => effect.Kind == CombatEffectKind.CastEnded && effect.Code == "cancelled");
        Assert.DoesNotContain(timeout, effect => effect.Kind == CombatEffectKind.Damage);
        Assert.Equal(First, scene.Core.ReadEncounter(First)!.Current);
        scene.Now = 200; scene.Core.Advance(scene.Now, 100);
        Assert.DoesNotContain(scene.Drain(), effect => effect.Kind == CombatEffectKind.Damage);
    }

    [Fact]
    public void ConsumableIceCastIsConsumedAtLaunchAndCancellationDoesNotRefund()
    {
        var scene = new Scene(CombatActorKind.Ice);
        scene.Start(); scene.Pass(First); scene.Drain();
        Assert.True(scene.Send(new UseCombatProgram(Second, "logic_bomb", First, Telegraph: true, ExecutableId: "card-7")).Accepted);
        var consumed = Assert.Single(scene.Drain(), effect => effect.Kind == CombatEffectKind.ConsumeProgram);
        Assert.Equal("card-7", consumed.ExecutableId);
        Assert.Equal(0, scene.Core.RemainingAp(Second));
        Assert.True(scene.Send(new CancelCombatCast(Second)).Accepted);
        Assert.DoesNotContain(scene.Drain(), effect => effect.Kind is CombatEffectKind.ConsumeProgram or CombatEffectKind.Damage);
        Assert.Equal(First, scene.View.Current);
    }

    [Fact]
    public void ThreeCompleteQuietRoundsCloseFightAndReleaseStatuses()
    {
        var scene = new Scene(); scene.Start(); scene.Drain();
        for (var decision = 0; decision < 6; decision++)
        {
            var actor = scene.View.Current!;
            Assert.True(scene.Send(new UseCombatProgram(actor, "shell")).Accepted);
            Assert.True(scene.Send(new UseCombatProgram(actor, "mend")).Accepted);
            scene.Pass(actor);
            if (decision < 5) Assert.Equal(1, scene.Core.EncounterCount);
        }
        Assert.Equal(0, scene.Core.EncounterCount);
        var effects = scene.Drain();
        Assert.Equal(2, effects.Count(effect => effect.Kind == CombatEffectKind.EncounterEnded));
        Assert.Contains(effects, effect => effect.Kind == CombatEffectKind.Shield && effect.UntilDs == scene.Now);
        Assert.Null(scene.Core.ReadEncounter(First));
        Assert.Null(scene.Core.ReadEncounter(Second));
    }

    [Fact]
    public void HostileCommittedTelegraphRestartsQuietHistoryEvenWhenCancelled()
    {
        var scene = new Scene(CombatActorKind.Ice); scene.Start();
        for (var round = 0; round < 2; round++) { scene.Pass(First); scene.Pass(Second); }
        Assert.Equal(2, scene.View.QuietRounds);
        scene.Pass(First);
        Assert.True(scene.Send(new UseCombatProgram(Second, "strike", First, Telegraph: true)).Accepted);
        Assert.Equal(0, scene.View.QuietRounds);
        Assert.True(scene.Send(new CancelCombatCast(Second)).Accepted);
        scene.Pass(Second);
        Assert.Equal(0, scene.View.QuietRounds);
        for (var decision = 0; decision < 6; decision++) scene.Pass(scene.View.Current!);
        Assert.Equal(0, scene.Core.EncounterCount);
    }

    [Fact]
    public void RetreatCannotRefillUnfinishedDecisionBudget()
    {
        var scene = new Scene(); scene.Start();
        Assert.True(scene.Send(new SpendCombatAp(First, 2)).Accepted);
        Assert.True(scene.Core.Apply(new SpendCombatAp(First, 1), scene.Now).Accepted);
        scene.Core.UpsertActor(new(First, OtherNode, 100, 100, AllPrograms));
        scene.Core.Advance(scene.Now, 100);
        Assert.Equal(0, scene.Core.EncounterCount);
        scene.Core.UpsertActor(new(First, Node, 100, 100, AllPrograms));
        Assert.False(scene.Send(new UseCombatProgram(First, "sting", Second)).Accepted);
        Assert.Equal(Second, scene.View.Current);
        Assert.Equal(0, scene.Core.RemainingAp(First));
    }

    [Fact]
    public void ConcreteProgramHooksWorkWithoutRoleSwitchAndDoNotMutateDefinitions()
    {
        var scene = new Scene();
        scene.Core.UpsertActor(new(Second, Node, 10, 100, AllPrograms));
        scene.Start(); scene.Drain();
        Assert.False(scene.Send(new UseCombatProgram(First, "custom", Second)).Accepted);
        Assert.Equal(3, scene.Core.RemainingAp(First));
        Assert.Equal(0UL, scene.Core.CooldownRemaining(First, "custom"));
        Assert.DoesNotContain(scene.Drain(), effect => effect.Kind == CombatEffectKind.ConsumeProgram);
        scene.Core.UpsertActor(new(Second, Node, 100, 100, AllPrograms));
        Assert.True(scene.Send(new UseCombatProgram(First, "custom", Second, DamageScale: 0.5, ExecutableId: "named-card")).Accepted);
        var effects = scene.Drain();
        Assert.Equal(4, Assert.Single(effects, effect => effect.Kind == CombatEffectKind.Damage).Value);
        Assert.Equal("named-card", Assert.Single(effects, effect => effect.Kind == CombatEffectKind.ConsumeProgram).ExecutableId);
        Assert.Equal(2UL, scene.Core.CooldownRemaining(First, "custom"));
        var bare = new CombatProgram { Id = "unimplemented", Role = "attack", Power = 999 };
        Assert.Empty(bare.Apply(new(First, new(Second, Node, 100, 100, AllPrograms), 1, true, false, 1, false, 0)).Effects);
        var attack = CombatPrograms.CreateDefaults().Single(program => program.Id == "strike");
        Assert.Equal(22, attack.Power);
        Assert.Equal(11, attack.Apply(new(First, new(Second, Node, 100, 100, AllPrograms), 0.5, true, false, 1, false, 0)).Effects[0].Value);
        Assert.Equal(22, attack.Power);
    }

    [Fact]
    public void DefaultDefinitionsAndPolymorphicBotValuesMatchDm()
    {
        var programs = CombatPrograms.CreateDefaults().ToDictionary(program => program.Id);
        Assert.Equal(19, programs.Count);
        Assert.All(programs.Values, program => Assert.InRange(program.ApCost, 1, 3));
        Assert.Equal(8, programs["sting"].Power);
        Assert.Equal(18, programs["pulse"].Power);
        Assert.Equal(28, programs["surge"].Power);
        Assert.Equal(70, programs["logic_bomb"].Power);
        Assert.Equal(2, programs["emp"].StunTurns);
        Assert.Equal(2, programs["aegis"].ShieldTurns);
        Assert.False(programs["trace"].ConsoleUsable);
        Assert.True(programs["trace"].TargetsBreach);
        Assert.Equal(5, programs["strike"].BotValue(10, true));
        Assert.Equal(22, programs["strike"].BotValue(10, false));
        Assert.Equal(70 * 2 / 3d, programs["aegis"].BotValue(10, true));
        Assert.Equal(20 * 2 / 3d * 0.8, programs["emp"].BotValue(10, true));
    }
}
