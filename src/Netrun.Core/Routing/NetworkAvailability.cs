namespace Netrun.Core.Routing;

/// <summary>
/// Bounded overlay текущих поколений, обновляемый только владельцем runtime.
/// UpdateFact/Remove затрагивают один ID; старые поколения не накапливаются в отдельной истории.
/// </summary>
public sealed class NetworkAvailability : INetworkAvailability
{
    /// <summary>Ровно одно текущее поколение и скалярная доступность на логический ID.</summary>
    private Dictionary<string, (ulong Generation, NodeAvailability State)> _nodes = new(StringComparer.Ordinal);
    /// <summary>Предел существующих ID; настройка не изменяется после construction.</summary>
    private readonly int _maxNodes;

    /// <inheritdoc />
    public ulong Revision { get; private set; }
    /// <inheritdoc />
    public bool BackboneEnabled { get; private set; }
    /// <summary>Количество существующих ID, без retained исторических поколений.</summary>
    public int Count => _nodes.Count;

    /// <summary>Создаёт независимый overlay; чтение исходного enumerable ограничено maxNodes.</summary>
    public NetworkAvailability(IEnumerable<NodeFact>? nodes = null, bool backboneEnabled = true, int maxNodes = 10_000)
    {
        if (maxNodes < 1)
            throw new RoutingException("invalid_availability_options");
        _maxNodes = maxNodes;
        BackboneEnabled = backboneEnabled;
        if (nodes is not null)
            Reset(nodes);
    }

    /// <inheritdoc />
    public NodeAvailability Get(NodeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _nodes.TryGetValue(key.NodeId, out var current) && current.Generation == key.Generation ? current.State : default;
    }

    /// <summary>
    /// Обновляет один текущий native-факт за O(1); запись прежнего поколения не заменяет более новое.
    /// Повтор эквивалентной доступности не меняет Revision. Tombstone ordering обеспечивает upstream mirror.
    /// </summary>
    public bool UpdateFact(NodeFact fact)
    {
        var state = Validate(fact);
        if (_nodes.TryGetValue(fact.NodeId, out var previous))
        {
            if (fact.Generation < previous.Generation || fact.Generation == previous.Generation && state == previous.State)
                return false;
        }
        else if (_nodes.Count >= _maxNodes)
            throw new RoutingException("availability_capacity");
        AdvanceRevision();
        _nodes[fact.NodeId] = (fact.Generation, state);
        return true;
    }

    /// <summary>Удаляет только совпадающее поколение; позднее удаление старого экземпляра не отключает замену.</summary>
    public bool Remove(NodeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_nodes.TryGetValue(key.NodeId, out var current) || current.Generation != key.Generation)
            return false;
        AdvanceRevision();
        _nodes.Remove(key.NodeId);
        return true;
    }

    /// <summary>Меняет только доступность quantum backbone; кабельный граф и bindings остаются прежними.</summary>
    public bool SetBackbone(bool enabled)
    {
        if (BackboneEnabled == enabled)
            return false;
        AdvanceRevision();
        BackboneEnabled = enabled;
        return true;
    }

    /// <summary>Атомарно заменяет overlay полным bounded набором после публикации нового snapshot, не сохраняя старые ID.</summary>
    public void Reset(IEnumerable<NodeFact> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var staged = new Dictionary<string, (ulong Generation, NodeAvailability State)>(StringComparer.Ordinal);
        foreach (var fact in nodes)
        {
            var state = Validate(fact);
            if (staged.Count >= _maxNodes)
                throw new RoutingException("availability_capacity");
            if (!staged.TryAdd(fact.NodeId, (fact.Generation, state)))
                throw new RoutingException("availability_duplicate_node");
        }
        if (_nodes.Count == staged.Count && staged.All(pair => _nodes.TryGetValue(pair.Key, out var state) && state == pair.Value))
            return;
        AdvanceRevision();
        _nodes = staged;
    }

    /// <summary>Проверяет identity и копирует только доступность; позиции, игровые права и payload здесь не хранятся.</summary>
    private static NodeAvailability Validate(NodeFact? fact)
    {
        if (fact?.State is null || !NodeFactRules.IsValidText(fact.NodeId, 128) || fact.Generation == 0)
            throw new RoutingException("invalid_availability_fact");
        return new NodeAvailability(fact.State.Online, fact.State.Network?.RelayOperational ?? true);
    }

    /// <summary>Проверяет исчерпание до mutation, чтобы Revision никогда не оборачивалась и не переиспользовала cache key.</summary>
    private void AdvanceRevision()
    {
        if (Revision == ulong.MaxValue)
            throw new RoutingException("availability_revision_exhausted");
        Revision++;
    }
}
