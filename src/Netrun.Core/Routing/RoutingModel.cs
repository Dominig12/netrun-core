using Netrun.Core.Topology;

namespace Netrun.Core.Routing;

/// <summary>Разные требования к конечной доступности без изменения физического графа.</summary>
public enum RouteKind
{
    /// <summary>Отправитель, получатель и каждый транзитный узел обязаны быть online.</summary>
    Machine,
    /// <summary>Конечный offline узел может быть обнаружен; forwarding через него запрещён.</summary>
    Discovery
}

/// <summary>Явный результат, не использующий Infinity как сетевую стоимость отказа.</summary>
public enum RouteStatus
{
    /// <summary>Найден допустимый путь, включая self-route с нулевой задержкой.</summary>
    Found,
    /// <summary>Оба поколения существуют в графе, но политика доступности не разрешает путь.</summary>
    Unreachable,
    /// <summary>Хотя бы одно адресуемое поколение отсутствует в этой публикации.</summary>
    StaleEndpoint
}

/// <summary>Неизменяемая выборка availability одного поколения без выделения объекта на каждое чтение.</summary>
/// <param name="Online">Полная native + временная доступность узла.</param>
/// <param name="RelayOperational">Native operational relay независимо от временного offline_until.</param>
/// <param name="Present">False означает удалённое/заменённое поколение, которое Discovery не должен видеть как offline endpoint.</param>
public readonly record struct NodeAvailability(bool Online, bool RelayOperational = true, bool Present = true);

/// <summary>Последовательный availability overlay; чтение не выполняет I/O и не изменяет состояние.</summary>
public interface INetworkAvailability
{
    /// <summary>Монотонная версия всего содержимого overlay и backbone setting.</summary>
    ulong Revision { get; }
    /// <summary>Общий допуск implicit quantum переходов; физические кабели не затрагиваются.</summary>
    bool BackboneEnabled { get; }
    /// <summary>Возвращает availability точного поколения; неизвестные и заменённые поколения недоступны.</summary>
    NodeAvailability Get(NodeKey key);
}

/// <summary>Типизированные положительные сетевые стоимости; версия меняется при любом изменении результата.</summary>
public interface IRouteCostPolicy
{
    /// <summary>Версия содержимого стоимости; в пределах одного FindRoute должна оставаться стабильной.</summary>
    ulong Revision { get; }
    /// <summary>Стоимость физического кабеля в децисекундах; конечная и строго положительная.</summary>
    double PhysicalCost(PhysicalEdge edge);
    /// <summary>Единая стоимость перехода между любыми relay bucket members, в децисекундах.</summary>
    double RelayCost(GraphSnapshot graph);
}

/// <summary>Read-only граница маршрутизации опубликованного графа с явной политикой конечного узла.</summary>
public interface IRouteProvider
{
    /// <summary>Находит кратчайший допустимый путь без перестройки топологии или изменения игровых фактов.</summary>
    RouteResult FindRoute(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine);
}

/// <summary>Лимиты LRU и временной памяти поиска, независимые от числа обращений.</summary>
/// <param name="CacheCapacity">Максимум положительных и отрицательных записей вместе; ноль отключает LRU.</param>
/// <param name="MaxNodes">Максимальное число узлов переданного GraphSnapshot.</param>
/// <param name="MaxQueueEntries">Максимум одновременно ожидающих PriorityQueue candidates.</param>
public sealed record RouterOptions(int CacheCapacity = 1024, int MaxNodes = int.MaxValue, int MaxQueueEntries = int.MaxValue);

/// <summary>Ошибка конфигурации, стоимости или ограничений поиска с безопасной диагностикой.</summary>
/// <param name="code">Стабильный код причины без входного содержимого.</param>
public sealed class RoutingException(string code) : Exception(code)
{
    /// <summary>Код отказа, пригодный для журнала и ответа вызывающему runtime.</summary>
    public string Code { get; } = code;
}

/// <summary>Неизменяемый request-owned результат; cached consumers не могут изменить путь другого запроса.</summary>
public sealed class RouteResult
{
    /// <summary>Found, отсутствие пути либо устаревшее поколение.</summary>
    public RouteStatus Status { get; }
    /// <summary>Собственная read-only копия пути; при отказе пуста.</summary>
    public IReadOnlyList<NodeKey> Path { get; }
    /// <summary>Конечная стоимость в децисекундах; при отказе равна нулю и интерпретируется только вместе со Status.</summary>
    public double LatencyDs { get; }
    /// <summary>Версия GraphSnapshot, на которой рассчитан путь.</summary>
    public ulong GraphVersion { get; }
    /// <summary>Версия availability overlay; ноль означает использование только immutable graph flags.</summary>
    public ulong AvailabilityVersion { get; }

    /// <summary>
    /// Создаёт immutable ответ внешнего IRouteProvider с необязательным локальным пределом. Проверяет целые поколения,
    /// конечную неотрицательную стоимость и согласованность Status с наличием пути.
    /// </summary>
    /// <param name="status">Известная категория результата.</param>
    /// <param name="path">Стабильный enumerable; чтение прекращается на первом элементе сверх лимита.</param>
    /// <param name="latencyDs">Стоимость Found в децисекундах; при отказе требуется ноль.</param>
    /// <param name="graphVersion">Версия исходного graph.</param>
    /// <param name="availabilityVersion">Версия использованного overlay.</param>
    /// <param name="maxPathLength">Положительный локальный предел копии; default int.MaxValue не задаёт application quota.</param>
    public static RouteResult Create(RouteStatus status, IEnumerable<NodeKey> path, double latencyDs,
        ulong graphVersion, ulong availabilityVersion, int maxPathLength = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(status) || !double.IsFinite(latencyDs) || latencyDs < 0 || maxPathLength < 1)
            throw new ArgumentException("Invalid route result.");
        var copy = new List<NodeKey>();
        foreach (var key in path)
        {
            if (copy.Count >= maxPathLength || key is null || key.Generation == 0 || !NodeFactRules.IsValidText(key.NodeId, 128))
                throw new ArgumentException("Invalid route path.", nameof(path));
            copy.Add(key);
        }
        if (status == RouteStatus.Found ? copy.Count == 0 : copy.Count != 0 || latencyDs != 0)
            throw new ArgumentException("Invalid route result.");
        return new RouteResult(status, copy, latencyDs, graphVersion, availabilityVersion);
    }

    /// <summary>Копирует путь и фиксирует версии источников; экземпляр допускает безопасное совместное кеширование.</summary>
    internal RouteResult(RouteStatus status, IEnumerable<NodeKey> path, double latencyDs, ulong graphVersion, ulong availabilityVersion)
    {
        Status = status;
        Path = Array.AsReadOnly(path.ToArray());
        LatencyDs = latencyDs;
        GraphVersion = graphVersion;
        AvailabilityVersion = availabilityVersion;
    }
}
