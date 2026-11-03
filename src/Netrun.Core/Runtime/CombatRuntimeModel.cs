using Netrun.Core.Entities;
using Netrun.Core.Entities.Combat;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>Combat intent занимает место в той же FIFO, что entity/module intents; Apply только принимает задачу.</summary>
public sealed record SubmitCombatIntent(RequestKey Key, CombatIntent Intent) : RuntimeCommand;

/// <summary>Trusted inventory bootstrap: точный состав существующих executable, без автоматической выдачи profile loadout.</summary>
public sealed record ConfigureActorCombat(RequestKey Key, ActorKey Actor, ValueList<string> InstalledProgramIds) : RuntimeCommand;

/// <summary>Terminal semantic outcome одной принятой FIFO задачи, не команды для исполнения боя в DM.</summary>
public sealed record CombatIntentCompleted(string BindingEpoch, RequestKey Key, ActorKey Actor,
    bool Accepted, string Code, CombatEncounterView? Encounter, CombatActorStatusView? State,
    ValueList<string> InstalledProgramIds) : TransportEvent(BindingEpoch, Key);

/// <summary>Immutable turn/AP/cooldown/cast read-model; DM только отображает его и передаёт последующие намерения.</summary>
public sealed record CombatStateChanged(string BindingEpoch, RequestKey Key, ActorKey Actor,
    CombatEncounterView? Encounter, CombatActorStatusView? State, string Code) : TransportEvent(BindingEpoch, Key);

/// <summary>
/// Только native projection/feedback notice после Core mutation. Damage/HP/AP/status в DM не пересчитываются;
/// Actor generation, residence и EffectId защищают proxy от recycled identity и повторного применения.
/// </summary>
public sealed record CombatNativeEffect(string BindingEpoch, RequestKey Key, ActorKey Actor, string EffectId,
    ulong EntityRevision, ulong ResidenceRevision, NodeKey Node, CombatEffect Effect) : TransportEvent(BindingEpoch, Key);

/// <summary>Общая shape validation runtime/JSON, независимая от текущего мира и side effects.</summary>
public static class CombatCommandRules
{
    /// <summary>Закрытый allowlist команд, включая полную identity каждого actor/node поколения.</summary>
    public static bool IsValid(RuntimeCommand? command) => command switch
    {
        ConfigureActorCombat configure => TransportFingerprint.ValidKey(configure.Key) && ValidActor(configure.Actor) &&
            configure.InstalledProgramIds is { Count: <= 4096 } programs && programs.All(id => NodeFactRules.IsValidText(id, 128)),
        SubmitCombatIntent submit => TransportFingerprint.ValidKey(submit.Key) && ValidIntent(submit.Intent),
        _ => false
    };

    /// <summary>Shape не авторизует контакт; world owner повторно сверяет residence/availability перед действием.</summary>
    private static bool ValidIntent(CombatIntent? intent) => intent is not null && ValidActor(intent.Actor) && intent switch
    {
        AdmitCombatAction admit => admit.Cost is >= 1 and <= CombatSimulation.MaximumAp && (admit.Target is null || ValidActor(admit.Target)),
        JoinCombat join => ValidNode(join.Node) && OptionalText(join.Token),
        PassCombatTurn pass => OptionalText(pass.Token),
        UseCombatProgram use => NodeFactRules.IsValidText(use.ProgramId, 128) && (use.Target is null || ValidActor(use.Target)) &&
            OptionalText(use.Token) && OptionalText(use.ExecutableId) && double.IsFinite(use.DamageScale) && use.DamageScale is >= 0 and <= 1000,
        ResolveCombatCast or CancelCombatCast or ResolveCombatAction => true,
        SpendCombatAp spend => spend.Cost is >= 1 and <= CombatSimulation.MaximumAp && OptionalText(spend.Token),
        _ => false
    };

    private static bool ValidActor(ActorKey? actor) => actor is not null && actor.Generation > 0 && NodeFactRules.IsValidText(actor.Id, 128);
    private static bool ValidNode(NodeKey? node) => node is not null && node.Generation > 0 && NodeFactRules.IsValidText(node.NodeId, 128);
    private static bool OptionalText(string? text) => text is null || NodeFactRules.IsValidText(text, 128);
}
