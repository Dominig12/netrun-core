using Netrun.Core.Topology;

namespace Netrun.Core.Routing;

/// <summary>
/// Адаптирует общий кеш полных source trees к pair API и проверке достижимости.
/// Discovery читает O(1) entry после одного поиска на graph/source/mode/availability.
/// </summary>
public sealed class SourceTreeRouteProvider : IRouteProvider, IRouteReachabilityProvider
{
    /// <summary>Общий ограниченный кеш деревьев, который может одновременно обслуживать projection.</summary>
    private readonly SourceRouteTreeProvider _trees;
    /// <summary>Тот же overlay, что у деревьев; уточняет StaleEndpoint для отдельного pair ответа.</summary>
    private readonly INetworkAvailability? _availability;

    /// <summary>
    /// Принимает готовый кеш; при использовании overlay его экземпляр должен совпадать с зависимостью trees.
    /// Без overlay проверка поколений отдельного pair ответа опирается на immutable graph.
    /// </summary>
    public SourceTreeRouteProvider(SourceRouteTreeProvider trees, INetworkAvailability? availability = null)
    {
        ArgumentNullException.ThrowIfNull(trees);
        _trees = trees;
        _availability = availability;
    }

    /// <inheritdoc />
    public bool IsReachable(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var tree = _trees.Build(graph, source, mode);
        return tree.Status == RouteStatus.Found && tree.Entries.ContainsKey(destination);
    }

    /// <inheritdoc />
    public RouteResult FindRoute(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var tree = _trees.Build(graph, source, mode);
        var present = graph.Nodes.ContainsKey(destination) && (_availability?.Get(destination).Present ?? true);
        if (_availability is not null && _availability.Revision != tree.AvailabilityVersion)
            throw new RoutingException("route_state_changed");
        if (!present) return Failed(RouteStatus.StaleEndpoint);
        if (tree.Status != RouteStatus.Found) return Failed(tree.Status);
        return tree.Entries.TryGetValue(destination, out var entry)
            ? new RouteResult(RouteStatus.Found, tree.GetPath(destination), entry.LatencyDs,
                tree.GraphVersion, tree.AvailabilityVersion)
            : Failed(RouteStatus.Unreachable);

        // Отрицательный ответ не материализует путь и сохраняет фактические версии поиска.
        RouteResult Failed(RouteStatus status) => new(status, [], 0, tree.GraphVersion, tree.AvailabilityVersion);
    }
}
