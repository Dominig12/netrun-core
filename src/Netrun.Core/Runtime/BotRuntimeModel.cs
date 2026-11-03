using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Storage;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>Raw credentials and identities; computed brain perception belongs exclusively to Core.</summary>
public sealed record BotConfiguration(BotKey Bot, ActorKey Actor, string ProgramId,
    EntityAccessSnapshot Access, NodeKey? Home = null, ActorKey? Owner = null, StoreKey? Inventory = null,
    BotBehavior? Behavior = null, bool ConsoleAvailable = true, bool GoalsEnabled = true);

/// <summary>Configure an exact bot generation; runtime reads its domain state from Core owners.</summary>
public sealed record ConfigureBot(RequestKey Key, BotConfiguration Configuration) : RuntimeCommand;

/// <summary>Compatibility type for the unshipped draft; externally supplied derived perception is rejected.</summary>
public sealed record PublishBotFact(RequestKey Key, BotFact Fact) : RuntimeCommand;

/// <summary>Remove an exact brain generation without deleting or recreating its actor/inventory.</summary>
public sealed record RemoveBot(RequestKey Key, BotKey Bot) : RuntimeCommand;

/// <summary>Core authenticates the issuing actor against the configured owner.</summary>
public sealed record SubmitBotOrder(RequestKey Key, BotKey Bot, BotOrder Order, ActorKey? Issuer = null) : RuntimeCommand;

/// <summary>Trusted combat fact; runtime revalidates both exact actor generations.</summary>
public sealed record RememberBotAttacker(RequestKey Key, BotKey Bot, ActorKey Attacker) : RuntimeCommand;

/// <summary>Wake is a shared FIFO intent, never an inline think.</summary>
public sealed record WakeBot(RequestKey Key, BotKey Bot) : RuntimeCommand;

/// <summary>Only an issued native decision accepts an external outcome; Core domain effects resolve internally.</summary>
public sealed record FinishBotEffect(RequestKey Key, BotEffectResult Result) : RuntimeCommand;

/// <summary>Read an immutable brain projection without changing goals.</summary>
public sealed record QueryBot(RequestKey Key, BotKey Bot) : RuntimeCommand;

/// <summary>Semantic task result; admission, domain completion and native outcome remain distinct.</summary>
public sealed record BotCommandCompleted(string BindingEpoch, RequestKey Key, BotKey Bot,
    bool Accepted, string Code, BotView? View) : TransportEvent(BindingEpoch, Key);

/// <summary>Allowlisted native presentation only; navigation and combat never execute in DM through this event.</summary>
public sealed record BotNativeDecision(string BindingEpoch, RequestKey Key, BotEffect Effect) : TransportEvent(BindingEpoch, Key);

/// <summary>Shape validation performs no authorization, world reads or planning.</summary>
public static class BotCommandRules
{
    public static bool IsValid(RuntimeCommand? command) => command switch
    {
        ConfigureBot configure => TransportFingerprint.ValidKey(configure.Key) && ValidConfiguration(configure.Configuration),
        RemoveBot remove => TransportFingerprint.ValidKey(remove.Key) && ValidBot(remove.Bot),
        SubmitBotOrder order => TransportFingerprint.ValidKey(order.Key) && ValidBot(order.Bot) &&
            ValidOrder(order.Order) && (order.Issuer is null || ValidActor(order.Issuer)),
        RememberBotAttacker attacker => TransportFingerprint.ValidKey(attacker.Key) && ValidBot(attacker.Bot) && ValidActor(attacker.Attacker),
        WakeBot wake => TransportFingerprint.ValidKey(wake.Key) && ValidBot(wake.Bot),
        FinishBotEffect finish => TransportFingerprint.ValidKey(finish.Key) && ValidResult(finish.Result),
        QueryBot query => TransportFingerprint.ValidKey(query.Key) && ValidBot(query.Bot),
        _ => false
    };

    public static bool ValidConfiguration(BotConfiguration? configuration) => configuration is not null &&
        ValidBot(configuration.Bot) && ValidActor(configuration.Actor) &&
        NodeFactRules.IsValidText(configuration.ProgramId, 128) && EntityAccessAuthority.IsValidSnapshot(configuration.Access) &&
        (configuration.Home is null || ValidNode(configuration.Home)) &&
        (configuration.Owner is null || ValidActor(configuration.Owner)) &&
        (configuration.Inventory is null || configuration.Inventory.Generation > 0 && NodeFactRules.IsValidText(configuration.Inventory.Id, 128)) &&
        (configuration.Behavior is null || configuration.Behavior.Strategy is "profile" or "patrol" or "guard" or "collect" &&
            double.IsFinite(configuration.Behavior.Aggression) && configuration.Behavior.Aggression is >= 0 and <= 100 &&
            double.IsFinite(configuration.Behavior.Caution) && configuration.Behavior.Caution is >= 0 and <= 100);

    private static bool ValidOrder(BotOrder? order) => order is not null &&
        order.Kind is "recall" or "terminate" or "guard" or "collect" or "patrol" or "command" or "hack" &&
        (order.Target is null || ValidNode(order.Target)) &&
        (order.CommandId is null || NodeFactRules.IsValidText(order.CommandId, 128));

    private static bool ValidResult(BotEffectResult? result) => result is not null && ValidBot(result.Bot) &&
        ValidActor(result.Actor) && result.ActionSerial > 0 && result.ExpectedRevision > 0 &&
        result.Phase is "queued" or "invoked" or "completed" or "rejected" or "cancelled" or "uncertain" &&
        (result.Response is null || NodeFactRules.IsValidText(result.Response, 4096));

    private static bool ValidBot(BotKey? key) => key is not null && key.Generation > 0 && NodeFactRules.IsValidText(key.Id, 128);
    private static bool ValidActor(ActorKey? actor) => actor is not null && actor.Generation > 0 && NodeFactRules.IsValidText(actor.Id, 128);
    private static bool ValidNode(NodeKey? node) => node is not null && node.Generation > 0 && NodeFactRules.IsValidText(node.NodeId, 128);
}
