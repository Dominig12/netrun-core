using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет единственного владельца actor state и границу необратимых native действий.</summary>
public sealed class EntityWorldTests
{
    private static readonly ActorKey Actor = new("actor", 1);
    private static readonly NodeKey Source = new("source", 1);
    private static readonly NodeKey Destination = new("destination", 1);
    private static EntityOperation Operation(ulong serial, EntityAction action, ActorKey? actor = null, ulong? revision = null) =>
        new(new("test", serial), actor ?? Actor, revision, action);
    private static EntityDefinition Definition(ActorKey? actor = null, int? expiry = null) =>
        new(actor ?? Actor, EntityKind.Resident, Source, 100, new("Runner"), "owner.1", ExpiresAtDs: expiry);
    private static EntityWorld Create(bool permitMove = false, EntityWorldOptions? options = null)
    {
        var world = new EntityWorld("binding", options, permitMove ? (_, _) => null : null);
        Assert.Equal(AdmissionStatus.Accepted, world.Submit(Operation(1, new CreateEntity(Definition())), 0).Status);
        Assert.True(world.TryDequeueEvent(out _));
        return world;
    }

    [Fact]
    public void DamageIsOwnedByCoreAndReplayDoesNotApplyTwice()
    {
        using var world = Create();
        var input = Operation(2, new DamageEntity(20));
        Assert.Equal(AdmissionStatus.Accepted, world.Submit(input, 0).Status);
        Assert.Equal(80, world.Read(Actor)!.Integrity);
        Assert.Equal(AdmissionStatus.Duplicate, world.Submit(input, 0).Status);
        Assert.Equal(80, world.Read(Actor)!.Integrity);
        Assert.Equal("request_conflict", world.Submit(input with { Action = new DamageEntity(21) }, 0).Code);
        Assert.Equal(80, world.Read(Actor)!.Integrity);
    }

    [Fact]
    public void WeakerShieldCannotReplaceOrExtendStrongerShield()
    {
        using var world = Create();
        world.Submit(Operation(2, new ShieldEntity(80, 30)), 0);
        Assert.Equal("weaker_shield", world.Submit(Operation(3, new ShieldEntity(20, 100)), 5).Code);
        Assert.Equal(30, world.Read(Actor)!.ShieldUntilDs);
        world.Submit(Operation(4, new DamageEntity(50)), 5);
        Assert.Equal(90, world.Read(Actor)!.Integrity, 9);
        world.Submit(Operation(5, new HealEntity(100)), 5);
        Assert.Equal(100, world.Read(Actor)!.Integrity);
    }

    [Fact]
    public void ExpiryHasOneRetainedDeadlineAndUsesSimulationClock()
    {
        using var world = Create();
        for (ulong i = 2; i < 1002; i++) world.Submit(Operation(i, new StunEntity(50)), 0);
        Assert.Equal(1, world.PendingDeadlineCount);
        Assert.Equal(0, world.Advance(49, 100));
        Assert.Equal(50, world.Read(Actor)!.StunnedUntilDs);
        Assert.Equal(1, world.Advance(50, 1));
        Assert.Equal(0, world.Read(Actor)!.StunnedUntilDs);
        Assert.Equal(0, world.PendingDeadlineCount);
    }

    [Fact]
    public void MoveIsCommittedOnlyAfterExactHandedOffNativeEffectAcknowledgement()
    {
        using var world = Create(permitMove: true);
        world.Submit(Operation(2, new MoveEntity(Destination)), 0);
        Assert.Equal(Source, world.Read(Actor)!.Node);
        var result = new EntityEffectResult("binding", Actor, "binding/entity/test:2", 1, true);
        Assert.False(world.Complete(result, 0).Accepted);
        Assert.True(world.TryDequeueEvent(out var output));
        var effect = Assert.IsType<EntityNativeEffect>(output);
        Assert.Equal(Source, effect.Source);
        Assert.False(world.Complete(result with { BindingEpoch = "old" }, 0).Accepted);
        Assert.True(world.Complete(result, 0).Accepted);
        Assert.Equal(Destination, world.Read(Actor)!.Node);
        Assert.Equal(2UL, world.Read(Actor)!.ResidenceRevision);
        Assert.False(world.Complete(result, 0).Accepted);
        Assert.Equal("entity_move_cooldown", world.Submit(Operation(3, new MoveEntity(Source)), 0).Code);
    }

    [Fact]
    public void NativeFailureDoesNotMoveOrRetry()
    {
        using var world = Create(permitMove: true);
        var operation = Operation(2, new MoveEntity(Destination));
        world.Submit(operation, 0);
        world.TryDequeueEvent(out var output);
        var effect = Assert.IsType<EntityNativeEffect>(output);
        Assert.True(world.Complete(new("binding", Actor, effect.EffectId, effect.ResidenceRevision, false), 0).Accepted);
        Assert.Equal(Source, world.Read(Actor)!.Node);
        Assert.Equal(AdmissionStatus.Duplicate, world.Submit(operation, 0).Status);
        world.TryDequeueEvent(out var changed);
        Assert.IsType<EntityChanged>(changed);
        Assert.False(world.TryDequeueEvent(out _));
    }

    [Fact]
    public void DeleteRevokesUnhandedEffectsAndOldGenerationCannotAffectReplacement()
    {
        using var world = Create(permitMove: true);
        world.Submit(Operation(2, new MoveEntity(Destination)), 0);
        world.Submit(Operation(3, new DeleteEntity()), 0);
        var replacement = new ActorKey("actor", 2);
        world.Submit(Operation(4, new CreateEntity(Definition(replacement)), replacement), 0);
        Assert.Equal("entity_unknown", world.Submit(Operation(5, new DamageEntity(100)), 0).Code);
        Assert.Equal(100, world.Read(replacement)!.Integrity);
        var outputs = new List<TransportEvent>();
        while (world.TryDequeueEvent(out var output)) outputs.Add(output!);
        Assert.DoesNotContain(outputs, item => item is EntityNativeEffect);
    }

    [Fact]
    public void OutputPressureDoesNotMutateStateOrSpendDomainSerial()
    {
        using var world = Create(options: new(MaxEvents: 1));
        world.Submit(Operation(2, new DamageEntity(1)), 0);
        var input = Operation(3, new DamageEntity(10));
        Assert.Equal(AdmissionStatus.WouldBlock, world.Submit(input, 0).Status);
        Assert.Equal(99, world.Read(Actor)!.Integrity);
        world.TryDequeueEvent(out _);
        Assert.Equal(AdmissionStatus.Accepted, world.Submit(input, 0).Status);
        Assert.Equal(89, world.Read(Actor)!.Integrity);
    }

    [Fact]
    public void DeadProcessCannotHealOrIssueNativeActions()
    {
        using var world = Create(permitMove: true);
        world.Submit(Operation(2, new DamageEntity(100)), 0);
        Assert.False(world.Read(Actor)!.Alive);
        Assert.Equal("entity_dead", world.Submit(Operation(3, new HealEntity(100)), 0).Code);
        Assert.Equal("entity_dead", world.Submit(Operation(4, new MoveEntity(Destination)), 0).Code);
    }

    [Fact]
    public void ExpiredProcessProducesCoreLifecycleProjection()
    {
        using var world = new EntityWorld("binding");
        world.Submit(Operation(1, new CreateEntity(Definition(expiry: 20))), 0);
        world.TryDequeueEvent(out _);
        Assert.Equal(1, world.Advance(20, 1));
        Assert.True(world.TryDequeueEvent(out var output));
        Assert.Equal("expired", Assert.IsType<EntityChanged>(output).Code);
        Assert.False(world.Read(Actor)!.Alive);
    }

    [Fact]
    public void UnsafeNumbersAndStaleRevisionsAreRejectedBeforeMutation()
    {
        using var world = Create();
        Assert.False(EntityWorld.IsValidOperation(Operation(2, new DamageEntity(double.NaN))));
        Assert.Equal("stale_actor_revision", world.Submit(Operation(3, new DamageEntity(20), revision: 99), 0).Code);
        Assert.Equal(100, world.Read(Actor)!.Integrity);
        Assert.Equal("authority_unavailable", world.Submit(Operation(4, new MoveEntity(Destination)), 0).Code);
    }

    [Fact]
    public void ExpiredBotLifetimeDoesNotCreateAnUnboundedLifecycleLoop()
    {
        using var world = new EntityWorld("binding");
        world.Submit(Operation(1, new CreateEntity(Definition(expiry: 10) with { Kind = EntityKind.Bot })), 0);
        world.TryDequeueEvent(out _);
        for (var i = 0; i < 100; i++) Assert.Equal(0, world.Advance(20, 10));
        Assert.True(world.Read(Actor)!.Alive);
        Assert.Equal(1UL, world.Read(Actor)!.Revision);
        Assert.Equal(0, world.PendingDeadlineCount);
        Assert.False(world.TryDequeueEvent(out _));
    }
}
