using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет, что BotEngine подключён к рантайму: команды идут через общую FIFO, мышление в Pump, решения на выход.</summary>
public sealed class RuntimeBotTests
{
    private static readonly Epochs Epochs = new("world", "engine", "binding");
    private static readonly NodeKey Home = new("home", 1);
    private static readonly ActorKey Actor = new("actor", 1);
    private static readonly BotKey Bot = new("brain", 1);

    /// <summary>Полный node-факт снапшота станции для настоящего topology builder.</summary>
    private static NodeFact WorldNode(string id, int x, string type = "server") =>
        new(id, 1, 1, new(id, type, new(x, 1, 1), "s", true, new("home", "home")));

    /// <summary>Исходный снапшот мира: sector, APC и два сервера.</summary>
    private static WorldChange[] WorldFacts() =>
    [
        new SectorUpsert(new("s", 1, 1, new ValueList<int>([1]))),
        new NodeUpsert(WorldNode("apc", 1, "apc")), new NodeUpsert(WorldNode("a", 2)), new NodeUpsert(WorldNode("b", 3))
    ];

    /// <summary>Read-model EntityWorld, который DM кладёт в факт бота; HP/residence остаются у ядра сущностей.</summary>
    private static EntityView View(ActorKey? actor = null, NodeKey? node = null, ulong revision = 1) =>
        new(actor ?? Actor, revision, revision, EntityKind.Bot, node ?? Home, 100, 100, new("Bot"),
            "owner", "principal", "custom", true, 0, 0, 1, 0, true, null, null);

    /// <summary>ACL/маршрутные факты узла уже вычислены Core owner; DM не задаёт готовое решение brain.</summary>
    private static BotNodeFact BotNode(string id = "home", string? network = "lan", string type = "server") =>
        new(new(id, 1), network, type)
        {
            AccessLevel = 2,
            NetworkLevel = 1,
            DirectEntry = id == "home",
            CanTraverse = true,
            CanPlanVisit = true,
            CanRoute = true,
            RouteCost = id == "home" ? 0 : 1,
            Commands = new(["scan", "toggle", "fort_repair"])
        };

    /// <summary>Минимальный факт бота со штатным профилем и одним домашним узлом.</summary>
    private static BotFact Fact(ulong revision = 1) =>
        new(Bot, revision, View(), "custom", new() { Home = Home, Nodes = new([BotNode()]) });

    private static RuntimeInput Input(ulong sequence, RuntimeCommand command) => new(Epochs, sequence, 0, command);

    /// <summary>Публикует снапшот четырьмя настоящими sync frames и возвращает первый свободный sequence.</summary>
    private static ulong Publish(NetworkRuntime runtime)
    {
        var chunk = new SnapshotChunk("snapshot", 1, WorldFacts());
        RuntimeInput[] inputs =
        [
            Input(1, new ApplyWorld([new SnapshotBegin("snapshot", 0, 2)])),
            Input(2, new ApplyWorld([chunk])),
            Input(3, new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)chunk.Changes.Count, SnapshotChecksum.Compute([chunk], 2))])),
            Input(4, new ApplyWorld([new CatchUpComplete("snapshot", 0)]))
        ];
        foreach (var input in inputs)
            Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        return 5;
    }

    /// <summary>Факт восприятия и приказ владельца приводят к одному native-решению brain, выданному наружу рантаймом.</summary>
    [Fact]
    public void BotFactAndOrderDriveACoreDecisionThroughTheRuntime()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, new PublishBotFact(new("bot-fact", 1), Fact()))).Admission!.Status);
        Assert.Equal(AdmissionStatus.Accepted,
            runtime.Apply(Input(sequence++, new SubmitBotOrder(new("bot-order", 1), Bot, new BotOrder("command", Home, "toggle")))).Admission!.Status);

        BotNativeDecision? decision = null;
        for (var attempt = 0; attempt < 64 && decision is null; attempt++)
        {
            runtime.Pump();
            while (decision is null && runtime.TryDequeueEvent(out var output))
                if (output is BotNativeDecision found) decision = found;
        }

        Assert.NotNull(decision);
        Assert.Equal(Bot, decision!.Effect.Bot);
        var command = Assert.IsType<BotCommand>(decision.Effect.Action);
        Assert.Equal("toggle", command.CommandId);

        var result = new BotEffectResult(decision.Effect.Bot, decision.Effect.Actor,
            decision.Effect.ActionSerial, decision.Effect.ExpectedRevision, "completed");
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence, new FinishBotEffect(new("bot-finish", 1), result))).Admission!.Status);
    }

    /// <summary>Повтор того же PublishBotFact на прежнем sequence распознаётся replay-леджером как дубликат.</summary>
    [Fact]
    public void PublishBotFactReplayIsDeduplicated()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var publish = Input(sequence, new PublishBotFact(new("bot-fact", 1), Fact()));
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(publish).Admission!.Status);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(publish).Apply.Status);
    }

    /// <summary>Без ботов четвёртый автоматический lane — no-op: публикация и удаление не ломают world-only прогон.</summary>
    [Fact]
    public void RemovingTheOnlyBotLeavesRuntimeHealthy()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new PublishBotFact(new("bot-fact", 1), Fact())));
        for (var i = 0; i < 4; i++) runtime.Pump();
        Assert.Equal(1, runtime.BotCount);
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, new RemoveBot(new("bot-remove", 1), Bot))).Admission!.Status);
        for (var i = 0; i < 4; i++) runtime.Pump();
        Assert.Equal(0, runtime.BotCount);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
    }
}
