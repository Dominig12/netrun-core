using Netrun.Core.Entities.Combat;

namespace Netrun.Core.Entities.Bots;

/// <summary>Тактический выбор из bot_brain.dm; эффекты исполняет общий CombatSimulation.</summary>
public sealed partial class BotEngine
{
    /// <summary>Не выбирает новую цель во время чужого/deferred хода и удерживает законного противника между AP.</summary>
    private bool CombatStep(Brain brain)
    {
        var encounter = brain.Fact.Context.Encounter;
        var actor = brain.Fact.Actor;
        if (encounter is not null && (encounter.Current != actor.Actor || encounter.Deferred))
        { brain.InCombat = true; brain.State = "ожидание хода"; return true; }
        var foes = brain.Fact.Context.Targets.Where(target => Hostile(brain, target)).ToArray();
        if (foes.Length == 0)
        {
            if (encounter?.Current == actor.Actor)
            { Emit(brain, new BotCombat(new PassCombatTurn(actor.Actor, encounter.DecisionId))); return true; }
            brain.InCombat = false; brain.PendingAttack = null; brain.CombatTarget = null; brain.ControlledTarget = null;
            return false;
        }
        brain.InCombat = true;
        brain.State = "бой";
        var target = foes.FirstOrDefault(foe => foe.Actor.Key == brain.CombatTarget) ??
            foes.FirstOrDefault(foe => foe.Actor.Key == brain.PendingAttack) ??
            foes.MinBy(foe => foe.Actor.Integrity)!;
        brain.CombatTarget = target.Actor.Key;
        if (encounter is null && target.Actor.Kind != CombatActorKind.Fortress)
        {
            Emit(brain, new BotCombat(new AdmitCombatAction(actor.Actor, target.Actor.Key, 1)));
            return true;
        }
        var ratio = actor.Integrity / Math.Max(1, actor.MaxIntegrity);
        if (ratio < brain.Behavior.Caution / 100 && TryFlee(brain)) return true;
        var self = new BotTargetFact(new(actor.Actor, actor.Node, actor.Integrity, actor.MaxIntegrity,
            new(brain.Fact.Context.Executables.Select(file => file.ProgramId)), InitialShieldFactor: actor.ShieldFactor,
            InitialShieldUntilDs: actor.ShieldUntilDs, InitialStunUntilDs: actor.StunnedUntilDs), actor.FactionId, actor.PrincipalId);
        if (ratio < 0.5 && _now >= actor.ShieldUntilDs && UseRole(brain, "shield", self)) return true;
        if (ratio < 0.4 && UseRole(brain, "repair", self)) return true;
        var strikePower = _programs.GetValueOrDefault("strike")?.Power ?? 22;
        if (encounter is not null && target.Actor.Integrity > strikePower && target.Actor.Key != brain.ControlledTarget && UseRole(brain, "stun", target))
            return true;
        if (target.Actor.Kind == CombatActorKind.Ice && target.PendingProgram && UseRole(brain, "stun", target)) return true;
        if (encounter is null && target.Actor.Kind == CombatActorKind.Entity && !target.IsBot)
        {
            if (brain.PendingAttack == target.Actor.Key)
            {
                if (_now < brain.AttackAt) return true;
                brain.PendingAttack = null;
            }
            else
            {
                if (Emit(brain, new BotWarning(target.Actor.Key, "attack_telegraph", Later(20))))
                { brain.PendingAttack = target.Actor.Key; brain.AttackAt = Later(20); }
                return true;
            }
        }
        if (!UseRole(brain, "attack", target) && encounter?.Current == actor.Actor && !encounter.Deferred)
            Emit(brain, new BotCombat(new PassCombatTurn(actor.Actor, encounter.DecisionId)));
        return true;
    }

    /// <summary>Действительные атакующие обходят temperament, но не contact/generation validation.</summary>
    private bool Hostile(Brain brain, BotTargetFact target)
    {
        if (target.Actor.Key == brain.Fact.Actor.Actor || !target.Actor.Valid || !target.ContactAllowed || !target.Online ||
            target.IgnoreUntilDs > _now) return false;
        if (brain.Enemies.Get(target.Actor.Key) > _now) return true;
        if (target.Actor.Kind == CombatActorKind.Ice)
            return target.IceAttackingSelf || target.IceIntruder && Willing(brain, target);
        var hostile = target.FactionHostile;
        if (brain.Profile.Planner == "guard" && brain.Fact.Actor.Node == (brain.Post ?? brain.Fact.Context.Home) &&
            target.DefenceThreat && target.AccessLevel < 2 &&
            (target.PrincipalId is null || target.PrincipalId != brain.Fact.Actor.PrincipalId))
            hostile = true;
        return hostile && Willing(brain, target);
    }

    /// <summary>Агрессия — детерминированное отношение силы, не новая случайность на каждом think.</summary>
    private static bool Willing(Brain brain, BotTargetFact target) => brain.Behavior.Aggression >= 100 ||
        brain.Behavior.Aggression > 0 && brain.Fact.Actor.Integrity * brain.Behavior.Aggression >=
        target.Actor.Integrity * (100 - brain.Behavior.Aggression);

    /// <summary>Эффективность на AP и статусные hooks взяты из общих полиморфных CombatProgram определений.</summary>
    private bool UseRole(Brain brain, string role, BotTargetFact target)
    {
        var actor = brain.Fact.Actor;
        var encounter = brain.Fact.Context.Encounter;
        if (encounter is null && _now < brain.Fact.Context.NextProgramDs) return false;
        if (role == "stun" && target.Actor.Kind == CombatActorKind.Fortress &&
            (target.FortressModuleKind != "turret" || !target.Online)) return false;
        var participant = encounter?.Participants.FirstOrDefault(entry => entry.Actor == actor.Actor);
        var bestValue = -1d;
        BotExecutable? bestFile = null;
        CombatProgram? bestProgram = null;
        var effectiveLimit = role == "repair" ? target.Actor.MaxIntegrity - target.Actor.Integrity : target.Actor.Integrity;
        foreach (var file in brain.Fact.Context.Executables)
        {
            if (!_programs.TryGetValue(file.ProgramId, out var program) || program.Role != role) continue;
            if (encounter is null ? _now < file.ReadyAtDs :
                file.CooldownTurns > 0 || participant is null || program.ApCost > participant.Ap ||
                participant.Cooldowns.Any(cooldown => cooldown.ProgramId == program.Id && cooldown.RemainingTurns > 0)) continue;
            var shielded = target.Actor.InitialShieldUntilDs > _now;
            var context = new CombatProgramContext(actor.Actor, target.Actor, 1, encounter is not null,
                target.StunImmune, shielded ? target.Actor.InitialShieldFactor : 1, shielded, _now);
            if (!program.CanApply(context)) continue;
            var value = program.BotValue(effectiveLimit, encounter is not null);
            if (value > bestValue) { bestValue = value; bestFile = file; bestProgram = program; }
        }
        return bestFile is not null && bestProgram is not null && Emit(brain,
            new BotCombat(new UseCombatProgram(actor.Actor, bestProgram.Id, target.Actor.Key, encounter?.DecisionId,
                ExecutableId: bestFile.ExecutableId)));
    }

    /// <summary>Отступление проходит только в непосредственно доступный узел без враждебного ICE.</summary>
    private bool TryFlee(Brain brain)
    {
        if (_now < brain.Fact.Actor.NextHopDs || !brain.Adjacent.TryGetValue(brain.Fact.Actor.Node, out var neighbors)) return false;
        var candidates = neighbors.ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            var picked = Random(brain, index, candidates.Length);
            (candidates[index], candidates[picked]) = (candidates[picked], candidates[index]);
            var node = Node(brain, candidates[index]);
            if (node is null || !node.DirectEntry || node.HostileIce || Blacklisted(brain, node.Key)) continue;
            if (Emit(brain, new BotNavigate(node.Key, true)))
            { brain.FleeFrom = brain.Fact.Actor.Node; brain.State = "отступление"; return true; }
        }
        return false;
    }
}
