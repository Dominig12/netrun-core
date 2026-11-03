using System.Text;
using Netrun.Core;
using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Combat;
using Netrun.Core.Runtime;
using Netrun.Core.Scheduling;
using Netrun.Core.Transport;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет настоящий FIFO/runtime/protocol: Combat изменяет только общий authoritative EntityWorld.</summary>
public sealed class CombatRuntimeTests
{
    private static readonly Epochs Epochs = new("world", "engine", "binding");
    private static readonly ActorKey First = new("first", 1), Second = new("second", 1);
    private static readonly NodeKey Home = new("home", 1), Remote = new("remote", 1);

    /// <summary>Минимальный настоящий graph, actor registry и duplex output без native solver/mock health registry.</summary>
    private sealed class Scene : IDisposable
    {
        internal readonly NetworkRuntime Runtime;
        internal readonly List<TransportEvent> Events = [];
        internal ulong Sequence = 1, IntentSerial, ConfigureSerial, EntitySerial;
        internal int Now;

        internal Scene(bool remoteTarget = false, RuntimeOptions? options = null)
        {
            Runtime = new(Epochs, options);
            WorldChange[] facts = [new SectorUpsert(new("s", 1, 1, new([1]))),
                new NodeUpsert(new("home", 1, 1, new("Home", "apc", new(1, 1, 1), "s", true, new("a", "a")))),
                new NodeUpsert(new("remote", 1, 1, new("Remote", "apc", new(10, 1, 1), "s", true, new("b", "b"))))];
            var chunk = new SnapshotChunk("snapshot", 1, facts);
            Send(new ApplyWorld([new SnapshotBegin("snapshot", 0, 2)]));
            Send(new ApplyWorld([chunk]));
            Send(new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)facts.Length, SnapshotChecksum.Compute([chunk], 2))]));
            Send(new ApplyWorld([new CatchUpComplete("snapshot", 0)]));
            Assert.Equal(MirrorSyncState.Ready, Runtime.State);
            Entity(First, new CreateEntity(new(First, EntityKind.Resident, Home, 100, new("First"), "owner", ProfileId: "raider")));
            Entity(Second, new CreateEntity(new(Second, EntityKind.Resident, remoteTarget ? Remote : Home, 100, new("Second"), "rogue")));
        }

        internal RuntimeResult Send(RuntimeCommand command)
        {
            var result = Runtime.Apply(new(Epochs, Sequence++, Now, command));
            Assert.Equal(ApplyStatus.Applied, result.Apply.Status);
            return result;
        }
        internal void Entity(ActorKey actor, EntityAction action, bool pump = true)
        {
            Assert.Equal(AdmissionStatus.Accepted, Send(new SubmitEntityOperation(new(new("entity.fixture", ++EntitySerial), actor, null, action))).Admission!.Status);
            if (pump) Pump();
        }
        internal void Configure(ActorKey actor, params string[] programs)
        {
            Assert.Equal(AdmissionStatus.Accepted, Send(new ConfigureActorCombat(new("combat.configure", ++ConfigureSerial), actor, new(programs))).Admission!.Status);
            Pump();
        }
        internal SubmitCombatIntent Intent(CombatIntent intent, bool pump = true)
        {
            var command = new SubmitCombatIntent(new("combat.intent", ++IntentSerial), intent);
            Assert.Equal(AdmissionStatus.Accepted, Send(command).Admission!.Status);
            if (pump) Pump();
            return command;
        }
        internal EntityAccessSnapshot MovementAccess()
        {
            var nodes = new[] { Home, Remote }.Select(node => new NodeAccessPolicy(node,
                new NetworkKey(Runtime.Graph!.LanIds[node], 1), 1, new([10]))).ToArray();
            return new(new("session", "principal", new([10])), new(nodes),
                new(nodes.Select(policy => new NetworkAccessPolicy(policy.Network!, "key", new([]))).Distinct()), new([]), new([]), new([]));
        }
        internal void Move(bool pump = true)
        {
            var key = new RequestKey("entity.fixture", ++EntitySerial);
            Assert.Equal(AdmissionStatus.Accepted, Send(new SubmitEntityOperation(new(key, First,
                null, new MoveEntity(Remote)), MovementAccess())).Admission!.Status);
            if (pump)
            {
                Pump();
                var completed = Events.OfType<CoreTaskCompleted>().Single(item => item.Key == key);
                Assert.True(completed.Success, completed.Code);
            }
        }
        internal CombatIntentCompleted Outcome(SubmitCombatIntent command) => Events.OfType<CombatIntentCompleted>().Single(item => item.Key == command.Key);
        internal void Pump(int budget = 512)
        {
            for (var pass = 0; pass < 1000; pass++)
            {
                Runtime.Pump(budget);
                Drain();
                if (Runtime.PendingTaskCount == 0 && Runtime.EventCount == 0)
                { Runtime.Pump(budget); Drain(); return; }
            }
            Assert.Fail("FIFO failed to drain under cooperative budget.");
        }
        internal void Drain()
        { while (Runtime.TryDequeueEvent(out var output)) Events.Add(output!); }
        internal void Clock(int now)
        { Now = now; Send(new ApplyWorld([])); Pump(); }
        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>Apply только enqueue; следующий FIFO heal видит уже применённый combat damage.</summary>
    [Fact]
    public void CombatEffectsReachEntityOwnerBeforeTheNextFifoIntent()
    {
        using var scene = new Scene();
        scene.Configure(First, "strike");
        var strike = scene.Intent(new UseCombatProgram(First, "strike", Second), pump: false);
        scene.Entity(Second, new HealEntity(5), pump: false);
        Assert.Equal(100, scene.Runtime.ReadEntity(Second)!.Integrity);
        scene.Pump();
        Assert.True(scene.Outcome(strike).Accepted);
        Assert.Equal(83, scene.Runtime.ReadEntity(Second)!.Integrity);
        Assert.Equal(3, scene.Runtime.ReadEntity(First)!.Trace);
        var outcomes = scene.Events.OfType<CoreTaskCompleted>().Where(item => item.Key == strike.Key || item.Key == new RequestKey("entity.fixture", 3)).ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.True(outcomes[0].TaskSequence < outcomes[1].TaskSequence);
    }

    /// <summary>Profile metadata не создаёт executable и не даёт атаковать без authoritative inventory bootstrap.</summary>
    [Fact]
    public void ProgramsMustBeExplicitlyInstalled()
    {
        using var scene = new Scene();
        var missing = scene.Intent(new UseCombatProgram(First, "strike", Second));
        Assert.False(scene.Outcome(missing).Accepted);
        Assert.Equal("program_unavailable", scene.Outcome(missing).Code);
        Assert.Equal(100, scene.Runtime.ReadEntity(Second)!.Integrity);
        scene.Configure(First, "strike");
        var installed = scene.Intent(new UseCombatProgram(First, "strike", Second));
        Assert.True(scene.Outcome(installed).Accepted);
        Assert.Equal(78, scene.Runtime.ReadEntity(Second)!.Integrity);
    }

    /// <summary>Combat factor применяется один раз; EntityWorld получает IgnoreShield после расчёта урона.</summary>
    [Fact]
    public void ShieldIsNotAppliedTwiceAcrossOwners()
    {
        using var scene = new Scene();
        scene.Entity(Second, new ShieldEntity(50, 100));
        scene.Configure(First, "strike");
        var attack = scene.Intent(new UseCombatProgram(First, "strike", Second));
        Assert.True(scene.Outcome(attack).Accepted);
        Assert.Equal(89, scene.Runtime.ReadEntity(Second)!.Integrity);
    }

    /// <summary>Remote target не получает local damage даже при физической маршрутизируемости graph.</summary>
    [Fact]
    public void RemoteCombatContactFailsClosed()
    {
        using var scene = new Scene(remoteTarget: true);
        scene.Configure(First, "strike");
        var attack = scene.Intent(new UseCombatProgram(First, "strike", Second));
        Assert.False(scene.Outcome(attack).Accepted);
        Assert.Equal("combat_contact_unavailable", scene.Outcome(attack).Code);
        Assert.Equal(100, scene.Runtime.ReadEntity(Second)!.Integrity);
        Assert.Null(scene.Outcome(attack).Encounter);
    }

    /// <summary>Producer replay не повторяет HP mutation, новый body под тем же request id — conflict.</summary>
    [Fact]
    public void CombatReplayUsesFullIdentityAndNeverRepeatsDamage()
    {
        using var scene = new Scene();
        scene.Configure(First, "strike", "needle");
        var attack = scene.Intent(new UseCombatProgram(First, "strike", Second));
        Assert.Equal(AdmissionStatus.Duplicate, scene.Send(attack).Admission!.Status);
        var changed = attack with { Intent = new UseCombatProgram(First, "needle", Second) };
        var conflict = scene.Send(changed).Admission!;
        Assert.Equal(AdmissionStatus.Rejected, conflict.Status);
        Assert.Equal("request_conflict", conflict.Code);
        scene.Pump();
        Assert.Equal(78, scene.Runtime.ReadEntity(Second)!.Integrity);
        Assert.Single(scene.Events.OfType<CombatNativeEffect>(), item => item.Effect.Kind == CombatEffectKind.Damage);
    }

    /// <summary>Одно output место не теряет эффект и не применяет damage заново после resumptions.</summary>
    [Fact]
    public void OutputPressureRetainsExactlyOnePendingCombatEffect()
    {
        using var scene = new Scene(options: new(Entities: new(MaxEvents: 1), Tasks: new(MaximumEvents: 1)));
        scene.Configure(First, "strike");
        var attack = scene.Intent(new UseCombatProgram(First, "strike", Second), pump: false);
        scene.Pump(budget: 1);
        Assert.True(scene.Outcome(attack).Accepted);
        Assert.Equal(78, scene.Runtime.ReadEntity(Second)!.Integrity);
        Assert.Single(scene.Events.OfType<CombatNativeEffect>(), item => item.Effect.Kind == CombatEffectKind.Damage);
    }

    /// <summary>Consume удаляет одну настоящую installed копию до native projection и сохраняет остальные.</summary>
    [Fact]
    public void ConsumableProgramIsRemovedFromCoreLoadout()
    {
        using var scene = new Scene();
        scene.Configure(First, "logic_bomb", "needle");
        var bomb = scene.Intent(new UseCombatProgram(First, "logic_bomb", Second, ExecutableId: "bomb-file"));
        Assert.True(scene.Outcome(bomb).Accepted);
        Assert.DoesNotContain("logic_bomb", scene.Outcome(bomb).InstalledProgramIds);
        Assert.Contains("needle", scene.Outcome(bomb).InstalledProgramIds);
        Assert.Equal(30, scene.Runtime.ReadEntity(Second)!.Integrity);
        var consume = Assert.Single(scene.Events.OfType<CombatNativeEffect>(), item => item.Effect.Kind == CombatEffectKind.ConsumeProgram);
        Assert.Equal("bomb-file", consume.Effect.ExecutableId);
    }

    /// <summary>Delayed cast не переходит на новое поколение удалённого target.</summary>
    [Fact]
    public void TargetGenerationReplacementCancelsDelayedCast()
    {
        using var scene = new Scene();
        scene.Configure(First, "strike");
        var cast = scene.Intent(new UseCombatProgram(First, "strike", Second, Telegraph: true));
        Assert.Equal("casting", scene.Outcome(cast).Code);
        Assert.Equal(100, scene.Runtime.ReadEntity(Second)!.Integrity);
        scene.Entity(Second, new DeleteEntity());
        var replacement = Second with { Generation = 2 };
        scene.Entity(replacement, new CreateEntity(new(replacement, EntityKind.Resident, Home, 100, new("New"), "rogue")));
        scene.Clock(30);
        Assert.Equal(100, scene.Runtime.ReadEntity(replacement)!.Integrity);
        Assert.DoesNotContain(scene.Events.OfType<CombatNativeEffect>(), item => item.Actor == replacement && item.Effect.Kind == CombatEffectKind.Damage);
    }

    /// <summary>Один AP резервируется до handoff; rejected native результат снимает deferral без возврата AP.</summary>
    [Fact]
    public void NativeMoveCostsOneApAndFailedAckReleasesDeferral()
    {
        using var scene = new Scene();
        Assert.True(scene.Outcome(scene.Intent(new AdmitCombatAction(First, Second, 1))).Accepted);
        scene.Move();
        var movement = Assert.Single(scene.Events.OfType<EntityNativeEffect>());
        var deferred = scene.Events.OfType<CombatStateChanged>().Last(item => item.Actor == First && item.Encounter is not null).Encounter!;
        Assert.True(deferred.Deferred);
        Assert.Equal(2, Assert.Single(deferred.Participants, item => item.Actor == First).Ap);
        scene.Send(new FinishEntityEffect(new("native.ack", 1), new(Epochs.Binding, First, movement.EffectId, movement.ResidenceRevision, false)));
        scene.Pump();
        Assert.Equal(Home, scene.Runtime.ReadEntity(First)!.Node);
        var resolved = scene.Events.OfType<CombatStateChanged>().Last(item => item.Actor == First && item.Encounter is not null).Encounter!;
        Assert.False(resolved.Deferred);
        Assert.Equal(2, Assert.Single(resolved.Participants, item => item.Actor == First).Ap);
    }

    /// <summary>Успешный hop переносит оставшийся AP ledger в новый контакт, а не дарит новый полный ход.</summary>
    [Fact]
    public void SuccessfulNativeMovePreservesCarriedApForNewNode()
    {
        using var scene = new Scene();
        scene.Intent(new AdmitCombatAction(First, Second, 1));
        scene.Move();
        var movement = Assert.Single(scene.Events.OfType<EntityNativeEffect>());
        scene.Send(new FinishEntityEffect(new("native.ack", 1), new(Epochs.Binding, First, movement.EffectId, movement.ResidenceRevision, true)));
        scene.Pump();
        Assert.Equal(Remote, scene.Runtime.ReadEntity(First)!.Node);
        Assert.Equal(10, scene.Runtime.ReadEntity(First)!.NextHopDs);
        var third = new ActorKey("third", 1);
        scene.Entity(third, new CreateEntity(new(third, EntityKind.Resident, Remote, 100, new("Third"), "rogue")));
        var joined = scene.Outcome(scene.Intent(new AdmitCombatAction(First, third, 1)));
        Assert.True(joined.Accepted);
        Assert.Equal(2, Assert.Single(joined.Encounter!.Participants, item => item.Actor == First).Ap);
    }

    /// <summary>Неподтверждённое native residence не используется как надёжный local combat contact.</summary>
    [Fact]
    public void ActorWithPendingMoveCannotStartCombatAtOldResidence()
    {
        using var scene = new Scene();
        scene.Configure(First, "strike");
        scene.Move();
        Assert.NotNull(scene.Runtime.ReadEntity(First)!.PendingEffectId);
        var attack = scene.Intent(new UseCombatProgram(First, "strike", Second));
        Assert.False(scene.Outcome(attack).Accepted);
        Assert.Equal("combat_move_pending", scene.Outcome(attack).Code);
        Assert.Equal(100, scene.Runtime.ReadEntity(Second)!.Integrity);
    }

    /// <summary>Рост clock при малом Pump budget не перезапускает refresh sweep с первого actor навсегда.</summary>
    [Fact]
    public void AdvancingClockDoesNotStarveDelayedCastBehindActorRefresh()
    {
        using var scene = new Scene();
        scene.Configure(First, "strike");
        for (var index = 0; index < 12; index++)
        {
            var observer = new ActorKey("observer-" + index, 1);
            scene.Entity(observer, new CreateEntity(new(observer, EntityKind.Resident, Home, 100, new("Observer"), "station")));
            scene.Configure(observer);
        }
        var cast = scene.Intent(new UseCombatProgram(First, "strike", Second, Telegraph: true));
        Assert.NotNull(scene.Outcome(cast).State!.Cast);
        var start = scene.Events.Count;
        var completed = false;
        for (var now = 1; now <= 2000 && !completed; now++)
        {
            scene.Now = now;
            scene.Send(new ApplyWorld([]));
            scene.Runtime.Pump(1);
            scene.Drain();
            completed = scene.Events.Skip(start).OfType<CombatStateChanged>().Any(item => item.Actor == First && item.State is { Cast: null });
        }
        Assert.True(completed, "Cast resolve/expiry must eventually run while clock grows under a bounded refresh budget.");
    }

    /// <summary>Closed wire ADT сохраняет каждый параметр и counters без BYOND float потери.</summary>
    [Fact]
    public void AllCombatWireIntentsRoundTripWithCanonicalCounters()
    {
        var actor = First with { Generation = ulong.MaxValue };
        RuntimeCommand[] commands =
        [
            new ConfigureActorCombat(new("configure", ulong.MaxValue), actor, new(["strike", "logic_bomb"])),
            new SubmitCombatIntent(new("intent", 1), new AdmitCombatAction(actor, Second, 2)),
            new SubmitCombatIntent(new("intent", 2), new JoinCombat(actor, Home, "1")),
            new SubmitCombatIntent(new("intent", 3), new PassCombatTurn(actor, "1:2")),
            new SubmitCombatIntent(new("intent", 4), new UseCombatProgram(actor, "strike", Second, "1:2", true, 0.5, "file")),
            new SubmitCombatIntent(new("intent", 5), new ResolveCombatCast(actor)),
            new SubmitCombatIntent(new("intent", 6), new CancelCombatCast(actor)),
            new SubmitCombatIntent(new("intent", 7), new SpendCombatAp(actor, 2, true, true, "1:2")),
            new SubmitCombatIntent(new("intent", 8), new ResolveCombatAction(actor))
        ];
        foreach (var command in commands)
        {
            var input = new RuntimeInput(Epochs, 1, 0, command);
            var encoded = RuntimeJson.Encode(input);
            var decoded = Assert.IsType<RuntimePacket>(RuntimeJson.Decode(encoded));
            Assert.Equal(command, decoded.Input.Command);
            var text = Encoding.UTF8.GetString(encoded);
            Assert.Contains("\"generation\":\"18446744073709551615\"", text);
            Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(text.Replace(
                "\"generation\":\"18446744073709551615\"", "\"generation\":18446744073709551615", StringComparison.Ordinal))));
        }
    }

    /// <summary>Unknown/duplicate/omitted nullable fields и unsafe numeric shape не запускают reflection/proc dispatch.</summary>
    [Fact]
    public void CombatWireRejectsUnknownDuplicateAndIncompletePayloads()
    {
        var input = new RuntimeInput(Epochs, 1, 0, new SubmitCombatIntent(new("intent", 1), new UseCombatProgram(First, "strike", Second)));
        var json = Encoding.UTF8.GetString(RuntimeJson.Encode(input));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace(
            "\"damage_scale\":1", "\"damage_scale\":1,\"damage_scale\":2", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace(
            "\"damage_scale\":1", "\"damage_scale\":1,\"execute_proc\":\"attack\"", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace("\"token\":null,", "", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Decode(Encoding.UTF8.GetBytes(json.Replace("use_program", "eval_dm", StringComparison.Ordinal))));
        Assert.Throws<ProtocolException>(() => RuntimeJson.Encode(input with
        { Command = new SubmitCombatIntent(new("intent", 1), new UseCombatProgram(First, "strike", Second, DamageScale: double.NaN)) }));
    }
}
