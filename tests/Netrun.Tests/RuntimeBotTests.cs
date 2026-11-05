using Netrun.Core;
using Netrun.Core.Access;
using Netrun.Core.Entities;
using Netrun.Core.Entities.Bots;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет, что BotEngine подключён к рантайму через ConfigureBot: Core строит восприятие из
/// собственной сущности бота и графа, мышление идёт в Pump, а решения выходят наружу общей FIFO.</summary>
public sealed class RuntimeBotTests
{
    private static readonly Epochs Epochs = new("world", "engine", "binding");
    private static readonly ActorKey Actor = new("actor", 1);
    private static readonly BotKey Bot = new("brain", 1);
    /// <summary>Настоящий узел опубликованного графа, на котором живёт сущность бота.</summary>
    private static readonly NodeKey Spot = new("a", 1);

    /// <summary>Полный node-факт снапшота станции для настоящего topology builder.</summary>
    private static NodeFact WorldNode(string id, int x, string type = "server") =>
        new(id, 1, 1, new(id, type, new(x, 1, 1), "s", true, new("home", "home")));

    /// <summary>Исходный снапшот мира: sector, APC и два сервера.</summary>
    private static WorldChange[] WorldFacts() =>
    [
        new SectorUpsert(new("s", 1, 1, new ValueList<int>([1]))),
        new NodeUpsert(WorldNode("apc", 1, "apc")), new NodeUpsert(WorldNode("a", 2)), new NodeUpsert(WorldNode("b", 3))
    ];

    /// <summary>Минимальный валидный access snapshot: identity принципала без выданных узловых прав.</summary>
    private static EntityAccessSnapshot Access() =>
        new(new EntityPrincipalFacts("session", "principal", new([])), new([]), new([]), new([]), new([]), new([]));

    /// <summary>Сырые идентичности и credentials бота; восприятие Core вычисляет сам из сущности и графа.</summary>
    private static BotConfiguration Config() => new(Bot, Actor, "custom", Access(), Home: Spot);

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

    /// <summary>Создаёт автономную сущность-бота в EntityWorld ядра и прогоняет её в live-состояние.</summary>
    private static ulong CreateBot(NetworkRuntime runtime, ulong sequence)
    {
        var operation = new EntityOperation(new RequestKey("entity.fixture", 1), Actor, null,
            new CreateEntity(new EntityDefinition(Actor, EntityKind.Bot, Spot, 100, new("Bot"), "owner",
                ProfileId: "custom", Autonomous: true)));
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, new SubmitEntityOperation(operation))).Admission!.Status);
        for (var i = 0; i < 4; i++) runtime.Pump();
        Assert.Equal(EntityKind.Bot, runtime.ReadEntity(Actor)!.Kind);
        return sequence;
    }

    /// <summary>Конфигурация бота и приказ владельца приводят к одному native-решению brain, выданному наружу рантаймом.</summary>
    [Fact]
    public void ConfiguredBotAndOrderDriveACoreDecisionThroughTheRuntime()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        sequence = CreateBot(runtime, sequence);
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, new ConfigureBot(new("bot-config", 1), Config()))).Admission!.Status);
        // Приказ terminate — presentation-решение (dissolve), не зависящее от device-command восприятия узла.
        Assert.Equal(AdmissionStatus.Accepted,
            runtime.Apply(Input(sequence++, new SubmitBotOrder(new("bot-order", 1), Bot, new BotOrder("terminate")))).Admission!.Status);

        BotNativeDecision? decision = null;
        for (var attempt = 0; attempt < 64 && decision is null; attempt++)
        {
            runtime.Pump();
            while (decision is null && runtime.TryDequeueEvent(out var output))
                if (output is BotNativeDecision { Effect.Action: BotDissolve } found) decision = found;
        }

        Assert.NotNull(decision);
        Assert.Equal(Bot, decision!.Effect.Bot);
        var dissolve = Assert.IsType<BotDissolve>(decision.Effect.Action);
        Assert.Equal("terminated", dissolve.Code);

        var result = new BotEffectResult(decision.Effect.Bot, decision.Effect.Actor,
            decision.Effect.ActionSerial, decision.Effect.ExpectedRevision, "completed");
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence, new FinishBotEffect(new("bot-finish", 1), result))).Admission!.Status);
    }

    /// <summary>Повтор того же ConfigureBot на прежнем sequence распознаётся replay-леджером как дубликат.</summary>
    [Fact]
    public void ConfigureBotReplayIsDeduplicated()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        sequence = CreateBot(runtime, sequence);
        var configure = Input(sequence, new ConfigureBot(new("bot-config", 1), Config()));
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(configure).Admission!.Status);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(configure).Apply.Status);
    }

    /// <summary>Удаление единственного бота оставляет рантайм исправным: четвёртый автоматический lane — no-op.</summary>
    [Fact]
    public void RemovingTheOnlyBotLeavesRuntimeHealthy()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        sequence = CreateBot(runtime, sequence);
        runtime.Apply(Input(sequence++, new ConfigureBot(new("bot-config", 1), Config())));
        for (var i = 0; i < 8; i++) runtime.Pump();
        Assert.Equal(1, runtime.BotCount);
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, new RemoveBot(new("bot-remove", 1), Bot))).Admission!.Status);
        for (var i = 0; i < 8; i++) runtime.Pump();
        Assert.Equal(0, runtime.BotCount);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
    }
}
