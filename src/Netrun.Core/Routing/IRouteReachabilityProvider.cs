using Netrun.Core.Topology;

namespace Netrun.Core.Routing;

/// <summary>
/// Проверяет достижимость без материализации пути. Подходит для множества кандидатов
/// одного discovery запроса, когда нужны только допустимые поколения получателей.
/// </summary>
public interface IRouteReachabilityProvider
{
    /// <summary>Возвращает наличие допустимого пути в текущих graph и availability, включая разрешённый self-route.</summary>
    bool IsReachable(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine);
}
