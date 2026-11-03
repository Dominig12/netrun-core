using Netrun.Core;
using Netrun.Core.Plugins;
using Netrun.Core.Routing;
using Netrun.Core.Runtime;
using Netrun.Core.Topology;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет подключение плагинов к runtime: состав, диагностику и влияние политики стоимостей на реальные маршруты.</summary>
public sealed class PluginTests
{
    /// <summary>Общий синтетический binding для каждого независимого владельца.</summary>
    private static readonly Epochs Epochs = new("world", "engine", "binding");

    /// <summary>Полный node-факт с native placement для настоящего topology builder.</summary>
    private static NodeFact Node(string id, int x, string type = "server") =>
        new(id, 1, 1, new(id, type, new(x, 1, 1), "s", true, new("home", "home")));

    /// <summary>Реальный allowlisted service contract; физический owner остаётся у mirror.</summary>
    private static ServiceFact Service(string id, string owner) =>
        new(id, 1, 1, new(owner, 1), "test.status", "tag", true,
            new ValueList<OperationContract>([new("read", OperationSafety.ReadOnly)]));

    /// <summary>Исходный world snapshot: sector, APC и два сервера, два сервиса.</summary>
    private static WorldChange[] Facts() =>
    [
        new SectorUpsert(new("s", 1, 1, new ValueList<int>([1]))),
        new NodeUpsert(Node("apc", 1, "apc")), new NodeUpsert(Node("a", 2)), new NodeUpsert(Node("b", 3)),
        new ServiceUpsert(Service("source", "a")), new ServiceUpsert(Service("destination", "b"))
    ];

    /// <summary>Фиксирует полный логический frame; время между командами по умолчанию не растёт.</summary>
    private static RuntimeInput Input(ulong sequence, RuntimeCommand command, int time = 0) => new(Epochs, sequence, time, command);

    /// <summary>Публикует snapshot четырьмя настоящими sync frames и возвращает первый свободный sequence.</summary>
    private static ulong Publish(NetworkRuntime runtime)
    {
        var chunk = new SnapshotChunk("snapshot", 1, Facts());
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

    /// <summary>Политика стоимостей плагина применяется настоящим router: задержка маршрута масштабируется множителем.</summary>
    [Fact]
    public void RouteCostPluginScalesLatencyThroughTheRuntime()
    {
        using var baseline = new NetworkRuntime(Epochs);
        var baseSequence = Publish(baseline);
        Assert.Equal(2, baseline.Apply(Input(baseSequence, new QueryRoute(new("a", 1), new("b", 1)))).Route!.LatencyDs);

        using var runtime = new NetworkRuntime(Epochs, new RuntimeOptions(Plugins: [new WeightedRoutingPlugin(3)]));
        var sequence = Publish(runtime);
        var route = runtime.Apply(Input(sequence, new QueryRoute(new("a", 1), new("b", 1)))).Route!;
        Assert.Equal(RouteStatus.Found, route.Status);
        Assert.Equal(6, route.LatencyDs);
    }

    /// <summary>Загруженные плагины доступны для диагностики; без плагинов список пуст.</summary>
    [Fact]
    public void LoadedPluginMetadataIsExposedForDiagnostics()
    {
        using var runtime = new NetworkRuntime(Epochs, new RuntimeOptions(Plugins: [new WeightedRoutingPlugin(2)]));
        Assert.Equal("example.weighted-routing", Assert.Single(runtime.Plugins).Id);
        using var bare = new NetworkRuntime(Epochs);
        Assert.Empty(bare.Plugins);
    }

    /// <summary>Две политики стоимостей в наборе — ошибка состава, обнаруживаемая при создании runtime.</summary>
    [Fact]
    public void TwoRouteCostPoliciesAreRejectedAtComposition()
    {
        var error = Assert.Throws<PluginException>(() =>
            new NetworkRuntime(Epochs, new RuntimeOptions(Plugins: [new WeightedRoutingPlugin(2), new SecondCostPlugin()])));
        Assert.Equal("duplicate_route_cost_policy", error.Code);
    }

    /// <summary>Повтор идентификатора плагина отвергается до обработки любого входа.</summary>
    [Fact]
    public void DuplicatePluginIdIsRejectedAtComposition()
    {
        var error = Assert.Throws<PluginException>(() =>
            new NetworkRuntime(Epochs, new RuntimeOptions(Plugins: [new WeightedRoutingPlugin(2), new WeightedRoutingPlugin(3)])));
        Assert.Equal("plugin_duplicate_id", error.Code);
    }

    /// <summary>Недопустимые метаданные (идентификатор вне разрешённого алфавита) отвергаются при composition.</summary>
    [Fact]
    public void InvalidPluginMetadataIsRejectedAtComposition()
    {
        var error = Assert.Throws<PluginException>(() =>
            new NetworkRuntime(Epochs, new RuntimeOptions(Plugins: [new InvalidMetadataPlugin()])));
        Assert.Equal("plugin_metadata_invalid", error.Code);
    }

    /// <summary>Пустая политика недопустима; ошибка возникает при вызове плагином во время composition.</summary>
    [Fact]
    public void NullRouteCostPolicyIsRejected() =>
        Assert.Throws<ArgumentNullException>(() =>
            new NetworkRuntime(Epochs, new RuntimeOptions(Plugins: [new NullPolicyPlugin()])));

    /// <summary>Ошибка внутри Configure прекращает создание runtime, не оставляя частично собранного состояния.</summary>
    [Fact]
    public void PluginConfigurationFailurePropagates() =>
        Assert.Throws<InvalidOperationException>(() =>
            new NetworkRuntime(Epochs, new RuntimeOptions(Plugins: [new ThrowingPlugin()])));

    /// <summary>Пример плагина: масштабирует все веса маршрутизации постоянным положительным множителем.</summary>
    private sealed class WeightedRoutingPlugin(double factor) : INetrunPlugin
    {
        /// <inheritdoc />
        public PluginMetadata Metadata { get; } = new("example.weighted-routing", "Weighted Routing", "1.0.0");
        /// <inheritdoc />
        public void Configure(INetrunPluginRegistry registry) => registry.SetRouteCostPolicy(new ScaledRouteCostPolicy(factor));
    }

    /// <summary>Второй плагин, также претендующий на политику стоимостей, с другим идентификатором.</summary>
    private sealed class SecondCostPlugin : INetrunPlugin
    {
        /// <inheritdoc />
        public PluginMetadata Metadata { get; } = new("example.second-cost", "Second Cost", "1.0.0");
        /// <inheritdoc />
        public void Configure(INetrunPluginRegistry registry) => registry.SetRouteCostPolicy(new ScaledRouteCostPolicy(2));
    }

    /// <summary>Плагин с недопустимым идентификатором (пробелы и верхний регистр).</summary>
    private sealed class InvalidMetadataPlugin : INetrunPlugin
    {
        /// <inheritdoc />
        public PluginMetadata Metadata { get; } = new("Invalid Id", "Invalid", "1.0.0");
        /// <inheritdoc />
        public void Configure(INetrunPluginRegistry registry) { }
    }

    /// <summary>Плагин, ошибочно пытающийся установить пустую политику стоимостей.</summary>
    private sealed class NullPolicyPlugin : INetrunPlugin
    {
        /// <inheritdoc />
        public PluginMetadata Metadata { get; } = new("example.null", "Null Policy", "1.0.0");
        /// <inheritdoc />
        public void Configure(INetrunPluginRegistry registry) => registry.SetRouteCostPolicy(null!);
    }

    /// <summary>Плагин, завершающий Configure ошибкой.</summary>
    private sealed class ThrowingPlugin : INetrunPlugin
    {
        /// <inheritdoc />
        public PluginMetadata Metadata { get; } = new("example.throwing", "Throwing", "1.0.0");
        /// <inheritdoc />
        public void Configure(INetrunPluginRegistry registry) => throw new InvalidOperationException("configure failed");
    }

    /// <summary>Чистая политика: умножает объявленные веса на постоянный множитель; ревизия постоянна.</summary>
    private sealed class ScaledRouteCostPolicy(double factor) : IRouteCostPolicy
    {
        /// <inheritdoc />
        public ulong Revision => 1;
        /// <inheritdoc />
        public double PhysicalCost(PhysicalEdge edge) => edge.LatencyDs * factor;
        /// <inheritdoc />
        public double RelayCost(GraphSnapshot graph) => graph.RelayLatencyDs * factor;
    }
}
