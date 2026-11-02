using System.Collections.ObjectModel;

namespace Netrun.Core.Topology;

/// <summary>Правило изменения уже установленного физического подключения.</summary>
public enum BindingPolicy
{
    /// <summary>Сохраняет валидный APC внутри прежнего сектора и base area.</summary>
    Stable,
    /// <summary>Явный адаптивный режим: каждый Build заново выбирает ближайший подходящий APC.</summary>
    Nearest
}

/// <summary>Смысл физического неориентированного ребра; quantum bucket хранится отдельно.</summary>
public enum PhysicalLinkKind
{
    /// <summary>Один кабель endpoint, включая реальный relay, к его APC.</summary>
    Endpoint,
    /// <summary>Кабель между APC одного физического сектора.</summary>
    Apc
}

/// <summary>Неизменяемые правила и ограничения публикации; Build не выполняется при каждом чтении маршрута.</summary>
/// <param name="ApcNeighbors">Число ближайших соседей каждого APC, 1..128.</param>
/// <param name="LinkTilesPerDs">Число тайлов на одну децисекунду кабельной задержки.</param>
/// <param name="RelayLatencyDs">Стоимость виртуального перехода между реальными relay, в децисекундах.</param>
/// <param name="BindingPolicy">Stable сохраняет игровой контракт; Nearest является явным изменением политики.</param>
/// <param name="MaxNodes">Максимум зарегистрированных узлов одной публикации.</param>
/// <param name="MaxPhysicalEdges">Максимум материализованных кабелей; quantum clique в лимит не входит.</param>
public sealed record TopologyOptions(int ApcNeighbors = 3, int LinkTilesPerDs = 15, int RelayLatencyDs = 3,
    BindingPolicy BindingPolicy = BindingPolicy.Stable, int MaxNodes = 10_000, int MaxPhysicalEdges = 100_000);

/// <summary>Безопасный отказ построения: не содержит native-данных или содержимого входных записей.</summary>
/// <param name="code">Стабильная причина отказа для диагностики.</param>
public sealed class TopologyException(string code) : Exception(code)
{
    /// <summary>Код нарушения фактов, ограничений или порядка публикации.</summary>
    public string Code { get; } = code;
}

/// <summary>Канонический неориентированный кабель; From/To упорядочены по сетевым адресам ordinal.</summary>
/// <param name="From">Первый физический экземпляр.</param>
/// <param name="To">Второй физический экземпляр.</param>
/// <param name="Kind">Привязка endpoint либо связь APC.</param>
/// <param name="Distance">Chebyshev-расстояние с индексом этажа вместо числового Z.</param>
/// <param name="LatencyDs">Положительная задержка в целых децисекундах.</param>
public sealed record PhysicalEdge(NodeKey From, NodeKey To, PhysicalLinkKind Kind, int Distance, int LatencyDs);

/// <summary>
/// Целая неизменяемая публикация графа. Не проверяет текущие права или доступность:
/// потребитель применяет availability overlay поверх структурных кабелей и relay bucket.
/// </summary>
public sealed class GraphSnapshot
{
    /// <summary>Локальная монотонная версия целой успешной публикации.</summary>
    public ulong Version { get; }
    /// <summary>Граница атомарно применённых world inputs, использованных при Build.</summary>
    public ulong InputSequence { get; }
    /// <summary>Собственные индексы неизменяемых фактов, включая offline и null-position узлы.</summary>
    public IReadOnlyDictionary<NodeKey, NodeFact> Nodes { get; }
    /// <summary>Однозначное соответствие адреса физическому поколению; сравнение ordinal.</summary>
    public IReadOnlyDictionary<string, NodeKey> AddressIndex { get; }
    /// <summary>Кабели в детерминированном порядке адресов без materialized quantum clique.</summary>
    public IReadOnlyList<PhysicalEdge> PhysicalEdges { get; }
    /// <summary>Физические incident edges каждого узла, отсортированные по адресу соседа.</summary>
    public IReadOnlyDictionary<NodeKey, IReadOnlyList<PhysicalEdge>> Adjacency { get; }
    /// <summary>Реальные расположенные NTnet relays в address order, независимо от online.</summary>
    public IReadOnlyList<NodeKey> RelayBucket { get; }
    /// <summary>Только существующие endpoint → APC привязки с обоими поколениями.</summary>
    public IReadOnlyDictionary<NodeKey, NodeKey> Bindings { get; }
    /// <summary>Производные LAN ID расположенных узлов; принадлежность LAN не создаёт дополнительных рёбер.</summary>
    public IReadOnlyDictionary<NodeKey, string> LanIds { get; }
    /// <summary>Единая стоимость implicit quantum перехода, в децисекундах.</summary>
    public int RelayLatencyDs { get; }

    /// <summary>Забирает независимые копии индексов и списков; mutable-массивы наружу не выдаются.</summary>
    internal GraphSnapshot(ulong version, ulong inputSequence, Dictionary<NodeKey, NodeFact> nodes,
        Dictionary<string, NodeKey> addresses, IReadOnlyList<PhysicalEdge> edges,
        Dictionary<NodeKey, List<PhysicalEdge>> adjacency, IReadOnlyList<NodeKey> relays,
        Dictionary<NodeKey, NodeKey> bindings, Dictionary<NodeKey, string> lans, int relayLatencyDs)
    {
        Version = version;
        InputSequence = inputSequence;
        Nodes = new ReadOnlyDictionary<NodeKey, NodeFact>(new Dictionary<NodeKey, NodeFact>(nodes));
        AddressIndex = new ReadOnlyDictionary<string, NodeKey>(new Dictionary<string, NodeKey>(addresses, StringComparer.Ordinal));
        PhysicalEdges = Array.AsReadOnly(edges.ToArray());
        Adjacency = new ReadOnlyDictionary<NodeKey, IReadOnlyList<PhysicalEdge>>(adjacency.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<PhysicalEdge>)Array.AsReadOnly(pair.Value.ToArray())));
        RelayBucket = Array.AsReadOnly(relays.ToArray());
        Bindings = new ReadOnlyDictionary<NodeKey, NodeKey>(new Dictionary<NodeKey, NodeKey>(bindings));
        LanIds = new ReadOnlyDictionary<NodeKey, string>(new Dictionary<NodeKey, string>(lans));
        RelayLatencyDs = relayLatencyDs;
    }
}
