namespace Netrun.Core.Communication;

/// <summary>
/// Последовательный bounded catalog. Upsert/Remove изменяют O(touched) данные;
/// snapshot materialize откладывается до чтения и объединяет серию native updates.
/// </summary>
public sealed class CommunicationPortCatalog
{
    /// <summary>Только актуальный port каждого логического ID, без unbounded поколений.</summary>
    private Dictionary<string, CommunicationPortFact> _ports = new(StringComparer.Ordinal);
    /// <summary>Последняя immutable копия; null означает pending materialization.</summary>
    private CommunicationPortSnapshot? _snapshot;
    /// <summary>Ограничение существующих портов.</summary>
    private readonly int _maxPorts;
    /// <summary>Ограничение configured out-degree одного native port.</summary>
    private readonly int _maxLinksPerPort;
    /// <summary>Ограничение filter set одного native port.</summary>
    private readonly int _maxFrequencies;

    /// <summary>Монотонная версия current native facts, независимо от graph version.</summary>
    public ulong Revision { get; private set; }
    /// <summary>Количество текущих logical port IDs.</summary>
    public int Count => _ports.Count;
    /// <summary>Стабильный reader, создаваемый один раз на серию изменений.</summary>
    public CommunicationPortSnapshot Snapshot => _snapshot ??= new CommunicationPortSnapshot(Revision,
        _ports.Values.ToDictionary(port => port.Owner));

    /// <summary>Фиксирует память catalog; lists в raw facts дополнительно ограничиваются до построения reader.</summary>
    public CommunicationPortCatalog(int maxPorts = int.MaxValue, int maxLinksPerPort = 128, int maxFrequencies = 128)
    {
        if (maxPorts < 1 || maxLinksPerPort is < 0 or > 4096 || maxFrequencies is < 0 or > 4096)
            throw new CommunicationException("invalid_communication_catalog_options");
        _maxPorts = maxPorts;
        _maxLinksPerPort = maxLinksPerPort;
        _maxFrequencies = maxFrequencies;
    }

    /// <summary>
    /// Чистая проверка raw wire-факта с default пределами 128 links/128 frequencies.
    /// Не меняет catalog, Revision или порядок входных set элементов; подходит для общей ServiceFact validation.
    /// </summary>
    public static bool IsValid(CommunicationPortFact? fact) => HasValidShape(fact, 128, 128);

    /// <summary>Публикует один native факт; старое поколение не заменяет новое, порядок set элементов не меняет Revision.</summary>
    public bool Upsert(CommunicationPortFact fact)
    {
        var canonical = Normalize(fact);
        if (_ports.TryGetValue(canonical.Owner.NodeId, out var previous))
        {
            if (previous.Owner.Generation > canonical.Owner.Generation || previous == canonical)
                return false;
        }
        else if (_ports.Count >= _maxPorts)
            throw new CommunicationException("communication_port_capacity");
        AdvanceRevision();
        _ports[canonical.Owner.NodeId] = canonical;
        return true;
    }

    /// <summary>Отзывает только совпадающее поколение; поздний remove прежнего owner не затрагивает замену.</summary>
    public bool Remove(NodeKey owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!_ports.TryGetValue(owner.NodeId, out var current) || current.Owner != owner)
            return false;
        AdvanceRevision();
        _ports.Remove(owner.NodeId);
        return true;
    }

    /// <summary>Атомарно заменяет полный bounded набор, не удерживая retired IDs; duplicate logical IDs запрещены.</summary>
    public void Replace(IEnumerable<CommunicationPortFact> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var staged = new Dictionary<string, CommunicationPortFact>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            var canonical = Normalize(port);
            if (staged.Count >= _maxPorts)
                throw new CommunicationException("communication_port_capacity");
            if (!staged.TryAdd(canonical.Owner.NodeId, canonical))
                throw new CommunicationException("communication_duplicate_port");
        }
        if (_ports.Count == staged.Count && staged.All(pair => _ports.TryGetValue(pair.Key, out var previous) && previous == pair.Value))
            return;
        AdvanceRevision();
        _ports = staged;
    }

    /// <summary>Проверяет native факт и канонизирует bounded sets, не вычисляя topology или reachability.</summary>
    private CommunicationPortFact Normalize(CommunicationPortFact? fact)
    {
        if (!HasValidShape(fact, _maxLinksPerPort, _maxFrequencies))
            throw new CommunicationException("invalid_communication_port");
        var validated = fact!;
        return validated with
        {
            ListeningFrequencies = new ValueList<int>(validated.ListeningFrequencies.Order(), _maxFrequencies),
            ConfiguredDirectedLinks = new ValueList<NodeKey>(validated.ConfiguredDirectedLinks.OrderBy(key => key.NodeId, StringComparer.Ordinal)
                .ThenBy(key => key.Generation), _maxLinksPerPort)
        };
    }

    /// <summary>Проверяет bounded set uniqueness без сортировки, нормализации или публикации.</summary>
    private static bool HasValidShape(CommunicationPortFact? fact, int maxLinks, int maxFrequencies)
    {
        if (fact is null || !ValidKey(fact.Owner) || !Enum.IsDefined(fact.Kind) || fact.NativeZ is < 0 or > 4096 ||
            fact.ListeningFrequencies is null || fact.ListeningFrequencies.Count > maxFrequencies ||
            fact.ConfiguredDirectedLinks is null || fact.ConfiguredDirectedLinks.Count > maxLinks)
            return false;
        var frequencies = new HashSet<int>();
        foreach (var frequency in fact.ListeningFrequencies)
            if (frequency <= 0 || !frequencies.Add(frequency))
                return false;
        var links = new HashSet<NodeKey>();
        foreach (var link in fact.ConfiguredDirectedLinks)
            if (!ValidKey(link) || !links.Add(link))
                return false;
        return true;
    }

    /// <summary>NodeKey остаётся opaque identity с корректным Unicode и положительным generation.</summary>
    private static bool ValidKey(NodeKey? key) => key is not null && key.Generation > 0 && NodeFactRules.IsValidText(key.NodeId, 128);

    /// <summary>Не допускает revision wrap; invalidation reader происходит только после полной успешной проверки.</summary>
    private void AdvanceRevision()
    {
        if (Revision == ulong.MaxValue)
            throw new CommunicationException("communication_revision_exhausted");
        Revision++;
        _snapshot = null;
    }
}
