using Netrun.Core.Plugins;
using Netrun.Core.Routing;
using Netrun.Core.Topology;

namespace Netrun.Samples.WeightedRouting;

/// <summary>Минимальный загружаемый плагин: увеличивает объявленные задержки вдвое.</summary>
public sealed class WeightedRoutingPlugin : INetrunPlugin
{
    /// <inheritdoc />
    public PluginMetadata Metadata { get; } = new("example.weighted-routing", "Weighted Routing", "1.0.0");

    /// <inheritdoc />
    public void Configure(INetrunPluginRegistry registry) => registry.SetRouteCostPolicy(new WeightedCost());

    /// <summary>Неизменная чистая политика не использует часы, ввод-вывод и состояние DM.</summary>
    private sealed class WeightedCost : IRouteCostPolicy
    {
        /// <inheritdoc />
        public ulong Revision => 1;
        /// <inheritdoc />
        public double PhysicalCost(PhysicalEdge edge) => edge.LatencyDs * 2;
        /// <inheritdoc />
        public double RelayCost(GraphSnapshot graph) => graph.RelayLatencyDs * 2;
    }
}
