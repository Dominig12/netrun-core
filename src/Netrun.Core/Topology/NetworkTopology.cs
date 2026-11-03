namespace Netrun.Core.Topology;

/// <summary>
/// Последовательный владелец публикации топологии. Build — явная O(N) граница обновления,
/// а чтение GraphSnapshot не перестраивает станцию. Ошибка сохраняет предыдущий snapshot и bindings.
/// </summary>
public sealed class NetworkTopology
{
    /// <summary>Контекст предыдущей привязки; временная доступность не участвует в её сохранении.</summary>
    private sealed record BindingMemory(NodeKey? Anchor, string? BaseArea, string? Sector);

    /// <summary>Индексы APC одного физического сектора, включая native-area subsets.</summary>
    private sealed record SectorIndex(SpatialPoint[] Points, SpatialIndex Spatial, Dictionary<string, SpatialIndex> Native);

    /// <summary>Кандидат APC cable с полным distance/address ordering.</summary>
    private sealed record CableCandidate(SpatialPoint From, SpatialPoint To, int Distance);

    /// <summary>Фиксированные policy и пределы работы, проверенные при construction.</summary>
    private readonly TopologyOptions _options;
    /// <summary>История только существующих поколений; удалённые узлы не удерживаются.</summary>
    private Dictionary<NodeKey, BindingMemory> _bindingMemory = [];
    /// <summary>Индексы APC именно Current publication; заменяются вместе с graph только после успешного Build.</summary>
    private Dictionary<string, SectorIndex> _anchorIndices = new(StringComparer.Ordinal);
    /// <summary>Ordered Z → floor lookup той же публикации; point query не сканирует список этажей.</summary>
    private Dictionary<string, Dictionary<int, int>> _anchorFloors = new(StringComparer.Ordinal);
    /// <summary>Запрет повторного входа; не предоставляет поддержки нескольких concurrent writers.</summary>
    private bool _building;

    /// <summary>Последняя успешная неизменяемая публикация, либо null до первого Build.</summary>
    public GraphSnapshot? Current { get; private set; }

    /// <summary>Фиксирует правила builder без фоновой работы или обращения к DM.</summary>
    /// <param name="options">Immutable конфигурация; null выбирает Stable и исходные сетевые стоимости.</param>
    /// <exception cref="TopologyException">invalid_topology_options при недопустимых thresholds или enum.</exception>
    public NetworkTopology(TopologyOptions? options = null)
    {
        _options = options ?? new TopologyOptions();
        if (_options.ApcNeighbors is < 1 or > 128 || _options.LinkTilesPerDs < 1 ||
            _options.RelayLatencyDs < 1 || _options.MaxNodes < 1 || _options.MaxPhysicalEdges < 0 ||
            _options.MaxParallelism is < 1 or > 32 ||
            !Enum.IsDefined(_options.BindingPolicy))
            throw new TopologyException("invalid_topology_options");
    }

    /// <summary>
    /// Строит граф из сырых фактов с точным spatial search; не принимает предварительно вычисленные рёбра.
    /// Все входные коллекции должны оставаться стабильными до возврата; результат их не заимствует.
    /// </summary>
    /// <param name="nodes">Текущий существующий mirror по логическому NodeId.</param>
    /// <param name="sectors">Полные ordered Z stacks; расположенный узел обязан ссылаться на известный сектор.</param>
    /// <param name="inputSequence">Cumulative input boundary; регресс относительно Current запрещён.</param>
    /// <returns>Целая новая публикация и её новая локальная версия.</returns>
    /// <exception cref="TopologyException">Безопасный отказ фактов, лимитов или причинности; Current не изменяется.</exception>
    public GraphSnapshot Build(IReadOnlyDictionary<string, NodeFact> nodes,
        IReadOnlyDictionary<string, SectorFact> sectors, ulong inputSequence)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(sectors);
        if (_building)
            throw new TopologyException("topology_reentrant");
        if (Current is { } previous && inputSequence < previous.InputSequence)
            throw new TopologyException("topology_input_regression");
        if (Current?.Version == ulong.MaxValue)
            throw new TopologyException("topology_version_exhausted");
        _building = true;
        try { return BuildCore(nodes, sectors, inputSequence); }
        finally { _building = false; }
    }

    /// <summary>
    /// Выбирает structural APC для произвольной точки через retained kd-tree, без полного обхода узлов.
    /// Native supplied area имеет приоритет; offline APC остаётся допустимым anchor, а routing проверяет доступность отдельно.
    /// Caller обязан передать именно текущую пригодную publication; runtime дополнительно исключает dirty/failed graph.
    /// </summary>
    /// <param name="graph">Identity последнего успешного Current, а не копия с тем же номером.</param>
    /// <param name="position">Физическая точка консоли без собственного netrun node.</param>
    /// <param name="sectorId">Точный связанный sector; числовые Z не пересортировываются.</param>
    /// <param name="baseAreaId">Native base area точки либо null для общего nearest APC сектора.</param>
    /// <returns>Точный APC generation либо null, если в этом секторе нет расположенных APC.</returns>
    /// <exception cref="TopologyException">Неверная identity, metadata либо положение вне объявленного ordered sector.</exception>
    public NodeKey? ResolveAnchor(GraphSnapshot graph, Position position, string sectorId, string? baseAreaId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (_building) throw new TopologyException("topology_reentrant");
        if (!ReferenceEquals(graph, Current)) throw new TopologyException("anchor_graph_stale");
        if (!IsValidAnchorPoint(position, sectorId, baseAreaId)) throw new TopologyException("anchor_invalid_query");
        if (!_anchorFloors.TryGetValue(sectorId, out var levels)) throw new TopologyException("sector_unknown");
        if (!levels.ContainsKey(position.Z)) throw new TopologyException("sector_position");
        return FindAnchor(position, sectorId, baseAreaId, _anchorIndices, _anchorFloors);
    }

    /// <summary>Общие bounds typed point request до fingerprint/JSON записи; не читает Current и не выполняет поиск.</summary>
    public static bool IsValidAnchorPoint(Position? position, string? sectorId, string? baseAreaId = null) =>
        position is { X: >= 1 and <= 32767, Y: >= 1 and <= 32767, Z: >= 1 and <= 4096 } &&
        NodeFactRules.IsValidText(sectorId, 128) && (baseAreaId is null || NodeFactRules.IsValidText(baseAreaId, 128));

    /// <summary>Собирает все derived данные локально и меняет состояние владельца только после успешного snapshot construction.</summary>
    private GraphSnapshot BuildCore(IReadOnlyDictionary<string, NodeFact> inputNodes,
        IReadOnlyDictionary<string, SectorFact> sectors, ulong inputSequence)
    {
        if (inputNodes.Count > _options.MaxNodes)
            throw new TopologyException("topology_node_capacity");
        var floors = BuildFloors(sectors);
        var nodes = new Dictionary<NodeKey, NodeFact>();
        var addresses = new Dictionary<string, NodeKey>(StringComparer.Ordinal);
        foreach (var (id, fact) in inputNodes)
        {
            ValidateNode(id, fact, floors);
            var key = new NodeKey(fact.NodeId, fact.Generation);
            if (!nodes.TryAdd(key, fact))
                throw new TopologyException("topology_duplicate_node");
            if (!addresses.TryAdd(fact.State.Address, key))
                throw new TopologyException("topology_duplicate_address");
            if (nodes.Count > _options.MaxNodes)
                throw new TopologyException("topology_node_capacity");
        }
        var ordered = nodes.Keys.OrderBy(key => nodes[key].State.Address, StringComparer.Ordinal).ToArray();
        var indices = BuildIndices(nodes, ordered, floors);
        var bindings = new Dictionary<NodeKey, NodeKey>();
        var memory = new Dictionary<NodeKey, BindingMemory>();
        var edges = new List<PhysicalEdge>();
        var edgeKeys = new HashSet<(NodeKey From, NodeKey To)>();
        foreach (var key in ordered)
        {
            var fact = nodes[key];
            var state = fact.State;
            if (state.NodeType == "apc")
                continue;
            NodeKey? anchor = null;
            if (state.Position is not null)
            {
                var area = state.Network?.BaseAreaId;
                if (_options.BindingPolicy == BindingPolicy.Stable)
                {
                    if (_bindingMemory.TryGetValue(key, out var previous))
                    {
                        if (previous.BaseArea == area && previous.Sector == state.SectorId &&
                            ValidAnchor(previous.Anchor, state.SectorId!, nodes))
                            anchor = previous.Anchor;
                    }
                    else if (ValidAnchor(state.Network?.InitialAnchor, state.SectorId!, nodes))
                        anchor = state.Network!.InitialAnchor;
                }
                anchor ??= FindAnchor(fact, indices, floors);
            }
            memory[key] = new BindingMemory(anchor, state.Network?.BaseAreaId, state.SectorId);
            if (anchor is null)
                continue;
            bindings.Add(key, anchor);
            AddEdge(key, anchor, PhysicalLinkKind.Endpoint, Distance(state, nodes[anchor].State, floors));
        }
        // Each sector owns a separate mutable spatial component index and disjoint APCs.
        // Workers never modify graph publication or a shared edge collection.
        var sectorsToConnect = indices.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
        var sectorEdges = new List<CableCandidate>[sectorsToConnect.Length];
        void ConnectSector(int index)
        {
            var candidates = new List<CableCandidate>();
            ConnectApcs(sectorsToConnect[index], candidates.Add);
            sectorEdges[index] = candidates;
        }
        if (_options.MaxParallelism > 1 && sectorsToConnect.Length > 1 &&
            sectorsToConnect.Sum(sector => sector.Points.Length) >= 32)
            Parallel.For(0, sectorsToConnect.Length,
                new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelism }, ConnectSector);
        else
            for (var i = 0; i < sectorsToConnect.Length; i++) ConnectSector(i);
        // Apply to the graph on the owner thread, preserving the old sector/edge order.
        foreach (var candidates in sectorEdges)
            foreach (var candidate in candidates)
                AddApcEdge(candidate);

        edges.Sort((first, second) => CompareEdges(first, second, nodes));
        var adjacency = nodes.Keys.ToDictionary(key => key, _ => new List<PhysicalEdge>());
        foreach (var edge in edges)
        {
            adjacency[edge.From].Add(edge);
            adjacency[edge.To].Add(edge);
        }
        foreach (var (key, incident) in adjacency)
            incident.Sort((first, second) => StringComparer.Ordinal.Compare(
                nodes[Other(first, key)].State.Address, nodes[Other(second, key)].State.Address));
        var relays = ordered.Where(key => nodes[key].State.Position is not null &&
            nodes[key].State.Network?.IsNtnetRelay == true).ToArray();
        var lans = BuildLans(nodes, ordered, bindings);
        var snapshot = new GraphSnapshot((Current?.Version ?? 0) + 1, inputSequence, nodes, addresses,
            edges, adjacency, relays, bindings, lans, _options.RelayLatencyDs);
        _bindingMemory = memory;
        _anchorIndices = indices;
        _anchorFloors = floors;
        Current = snapshot;
        return snapshot;

        // Получает каноническую пару APC из mesh/bridge construction, не меняя её смысл.
        void AddApcEdge(CableCandidate candidate) => AddEdge(candidate.From.Key, candidate.To.Key,
            PhysicalLinkKind.Apc, candidate.Distance);

        // Ограничивает materialized кабели до добавления; canonical endpoints определяются адресами, не ID.
        void AddEdge(NodeKey first, NodeKey second, PhysicalLinkKind kind, int distance)
        {
            if (StringComparer.Ordinal.Compare(nodes[first].State.Address, nodes[second].State.Address) > 0)
                (first, second) = (second, first);
            if (!edgeKeys.Add((first, second)))
                return;
            if (edges.Count >= _options.MaxPhysicalEdges)
                throw new TopologyException("topology_edge_capacity");
            var latency = checked((int)Math.Max(1L, ((long)distance + _options.LinkTilesPerDs - 1) / _options.LinkTilesPerDs));
            edges.Add(new PhysicalEdge(first, second, kind, distance, latency));
        }
    }

    /// <summary>Проверяет определения секторов и строит Z → floor index без числовой пересортировки уровней.</summary>
    private static Dictionary<string, Dictionary<int, int>> BuildFloors(IReadOnlyDictionary<string, SectorFact> sectors)
    {
        if (sectors.Count > 4096)
            throw new TopologyException("topology_sector_capacity");
        var result = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var seenLevels = new HashSet<int>();
        foreach (var (id, sector) in sectors)
        {
            if (sector is null || id != sector.SectorId || !NodeFactRules.IsValidText(id, 256) ||
                sector.Generation == 0 || sector.Revision == 0 || sector.Levels is null || sector.Levels.Count is < 1 or > 4096)
                throw new TopologyException("topology_invalid_sector");
            var levels = new Dictionary<int, int>();
            for (var i = 0; i < sector.Levels.Count; i++)
            {
                var z = sector.Levels[i];
                if (z is < 1 or > 4096 || !levels.TryAdd(z, i) || !seenLevels.Add(z))
                    throw new TopologyException("topology_invalid_sector");
            }
            result.Add(id, levels);
        }
        return result;
    }

    /// <summary>Проверяет ровно факты builder, включая физическую связность, без угадывания отсутствующего сектора.</summary>
    private static void ValidateNode(string id, NodeFact? fact, Dictionary<string, Dictionary<int, int>> floors)
    {
        if (fact?.State is not { } state || id != fact.NodeId || !NodeFactRules.IsValidText(id, 128) ||
            fact.Generation == 0 || fact.Revision == 0 || !NodeFactRules.IsValidText(state.Address, 256) ||
            !NodeFactRules.IsValidText(state.NodeType, 256))
            throw new TopologyException("topology_invalid_node");
        if (state.Network is { } network)
        {
            if (network.BaseAreaId is { } area && !NodeFactRules.IsValidText(area, 256) ||
                network.SuppliedAreaId is { } supplied && !NodeFactRules.IsValidText(supplied, 256) ||
                network.Department is not ("security" or "command" or "engineering" or "medical" or "science" or "cargo" or "general") ||
                network.InitialAnchor is { } seed && (!NodeFactRules.IsValidText(seed.NodeId, 128) || seed.Generation == 0))
                throw new TopologyException("topology_invalid_node");
        }
        if (state.Position is not { } position)
            return;
        if (position.X is < 1 or > 32767 || position.Y is < 1 or > 32767 || position.Z is < 1 or > 4096 ||
            !NodeFactRules.IsValidText(state.SectorId, 256))
            throw new TopologyException("topology_invalid_node");
        if (!floors.TryGetValue(state.SectorId!, out var levels))
            throw new TopologyException("topology_missing_sector");
        if (!levels.ContainsKey(position.Z))
            throw new TopologyException("topology_sector_position");
    }

    /// <summary>Индексирует расположенные APC по сектору и питаемой base area; offline APC сохраняются.</summary>
    private Dictionary<string, SectorIndex> BuildIndices(Dictionary<NodeKey, NodeFact> nodes,
        IReadOnlyList<NodeKey> ordered, Dictionary<string, Dictionary<int, int>> floors)
    {
        var groups = new Dictionary<string, List<SpatialPoint>>(StringComparer.Ordinal);
        foreach (var key in ordered)
        {
            var state = nodes[key].State;
            if (state.NodeType != "apc" || state.Position is not { } position)
                continue;
            var sector = state.SectorId!;
            if (!groups.TryGetValue(sector, out var points))
                groups.Add(sector, points = []);
            points.Add(new SpatialPoint(points.Count, key, state.Address, position.X, position.Y, floors[sector][position.Z]));
        }
        // Sector indexes have disjoint workspaces. Compute them on parallel cores,
        // then publish in the original deterministic insertion order.
        var sectors = groups.ToArray();
        var built = new SectorIndex[sectors.Length];
        void BuildSector(int index)
        {
            var points = sectors[index].Value;
            var native = new Dictionary<string, SpatialIndex>(StringComparer.Ordinal);
            foreach (var group in points.GroupBy(point => nodes[point.Key].State.Network?.SuppliedAreaId ??
                nodes[point.Key].State.Network?.BaseAreaId, StringComparer.Ordinal))
                if (group.Key is { } area)
                    native.Add(area, new SpatialIndex(group));
            built[index] = new SectorIndex(points.ToArray(), new SpatialIndex(points), native);
        }
        if (_options.MaxParallelism > 1 && sectors.Length > 1 && sectors.Sum(group => group.Value.Count) >= 32)
            Parallel.For(0, sectors.Length, new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelism }, BuildSector);
        else
            for (var i = 0; i < sectors.Length; i++) BuildSector(i);
        var result = new Dictionary<string, SectorIndex>(StringComparer.Ordinal);
        for (var i = 0; i < sectors.Length; i++)
            result.Add(sectors[i].Key, built[i]);
        return result;
    }

    /// <summary>Ищет ближайший native-area APC, иначе ближайший APC того же сектора, с точным address tie-break.</summary>
    private static NodeKey? FindAnchor(NodeFact node, Dictionary<string, SectorIndex> indices,
        Dictionary<string, Dictionary<int, int>> floors) => FindAnchor(node.State.Position!, node.State.SectorId!,
            node.State.Network?.BaseAreaId, indices, floors);

    /// <summary>Общая точная nearest policy builder и point API; ни temporary node facts, ни registry scan не требуются.</summary>
    private static NodeKey? FindAnchor(Position position, string sectorId, string? area, Dictionary<string, SectorIndex> indices,
        Dictionary<string, Dictionary<int, int>> floors)
    {
        if (!indices.TryGetValue(sectorId, out var sector))
            return null;
        var spatial = area is not null && sector.Native.TryGetValue(area, out var native)
            ? native : sector.Spatial;
        return spatial.Nearest(position.X, position.Y, floors[sectorId][position.Z], 1)[0].Key;
    }

    /// <summary>Проверяет существование именно прежнего поколения APC в том же физическом секторе, независимо от online.</summary>
    private static bool ValidAnchor(NodeKey? anchor, string sector, Dictionary<NodeKey, NodeFact> nodes) =>
        anchor is not null && nodes.TryGetValue(anchor, out var fact) && fact.State.NodeType == "apc" &&
        fact.State.Position is not null && fact.State.SectorId == sector;

    /// <summary>Собирает K-nearest mesh и точные минимальные межкомпонентные мосты indexed Boruvka без quadratic памяти.</summary>
    private void ConnectApcs(SectorIndex sector, Action<CableCandidate> add)
    {
        var points = sector.Points;
        if (points.Length < 2)
            return;
        var components = new DisjointSet(points.Length);
        var mesh = new Dictionary<(int, int), CableCandidate>();
        foreach (var point in points)
            foreach (var neighbor in sector.Spatial.Nearest(point.X, point.Y, point.Floor,
                Math.Min(_options.ApcNeighbors, points.Length - 1), excludedIndex: point.Index))
            {
                var candidate = Candidate(point, neighbor);
                if (mesh.TryAdd((candidate.From.Index, candidate.To.Index), candidate) && mesh.Count > _options.MaxPhysicalEdges)
                    throw new TopologyException("topology_edge_capacity");
            }
        var componentCount = points.Length;
        foreach (var candidate in mesh.Values.OrderBy(candidate => candidate, CandidateComparer))
        {
            add(candidate);
            if (components.Union(candidate.From.Index, candidate.To.Index))
                componentCount--;
        }
        while (componentCount > 1)
        {
            sector.Spatial.RefreshComponents(components);
            var bestByComponent = new Dictionary<int, CableCandidate>();
            foreach (var point in points)
            {
                var root = components.Find(point.Index);
                var nearest = sector.Spatial.Nearest(point.X, point.Y, point.Floor, 1,
                    excludedComponent: root, components: components);
                if (nearest.Count == 0)
                    throw new TopologyException("topology_disconnected_index");
                var candidate = Candidate(point, nearest[0]);
                if (!bestByComponent.TryGetValue(root, out var previous) || CandidateComparer.Compare(candidate, previous) < 0)
                    bestByComponent[root] = candidate;
            }
            foreach (var candidate in bestByComponent.Values.OrderBy(candidate => candidate, CandidateComparer))
                if (components.Union(candidate.From.Index, candidate.To.Index))
                {
                    add(candidate);
                    componentCount--;
                }
        }
    }

    /// <summary>Полный порядок делает MST уникальным: indexed Boruvka выбирает те же bridges, что exhaustive Prim.</summary>
    private static readonly IComparer<CableCandidate> CandidateComparer = Comparer<CableCandidate>.Create((first, second) =>
    {
        var comparison = first.Distance.CompareTo(second.Distance);
        if (comparison == 0)
            comparison = StringComparer.Ordinal.Compare(first.From.Address, second.From.Address);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(first.To.Address, second.To.Address);
    });

    /// <summary>Канонизирует APC pair и один раз вычисляет его геометрическое расстояние.</summary>
    private static CableCandidate Candidate(SpatialPoint first, SpatialPoint second)
    {
        if (StringComparer.Ordinal.Compare(first.Address, second.Address) > 0)
            (first, second) = (second, first);
        return new CableCandidate(first, second, SpatialIndex.Distance(first.X, first.Y, first.Floor, second));
    }

    /// <summary>Расстояние двух расположенных endpoints одного сектора в метрике исходного ordered stack.</summary>
    private static int Distance(NodeState first, NodeState second, Dictionary<string, Dictionary<int, int>> floors)
    {
        var left = first.Position!;
        var right = second.Position!;
        return Math.Max(Math.Max(Math.Abs(left.X - right.X), Math.Abs(left.Y - right.Y)),
            Math.Abs(floors[first.SectorId!][left.Z] - floors[second.SectorId!][right.Z]));
    }

    /// <summary>LAN APC определяется отделом/сектором; остальные наследуют anchor либо получают безопасный fallback.</summary>
    private static Dictionary<NodeKey, string> BuildLans(Dictionary<NodeKey, NodeFact> nodes,
        IReadOnlyList<NodeKey> ordered, Dictionary<NodeKey, NodeKey> bindings)
    {
        var result = new Dictionary<NodeKey, string>();
        foreach (var key in ordered)
        {
            var state = nodes[key].State;
            if (state.Position is not null && state.NodeType == "apc")
                result.Add(key, $"dept_{state.SectorId}:{state.Network?.Department ?? "general"}");
        }
        foreach (var key in ordered)
        {
            var state = nodes[key].State;
            if (state.Position is not { } position || state.NodeType == "apc")
                continue;
            if (bindings.TryGetValue(key, out var anchor))
                result.Add(key, result[anchor]);
            else if (state.Network is null || state.Network.IsStationLevel || state.Network.IsMiningLevel)
                result.Add(key, $"pub:{state.SectorId}");
            else
                result.Add(key, $"ext{position.Z}");
        }
        return result;
    }

    /// <summary>Возвращает другой конец incident edge без поиска по графу.</summary>
    private static NodeKey Other(PhysicalEdge edge, NodeKey key) => edge.From == key ? edge.To : edge.From;

    /// <summary>Фиксирует стабильный порядок публикации независимо от порядка Dictionary/union-find.</summary>
    private static int CompareEdges(PhysicalEdge first, PhysicalEdge second, Dictionary<NodeKey, NodeFact> nodes)
    {
        var comparison = StringComparer.Ordinal.Compare(nodes[first.From].State.Address, nodes[second.From].State.Address);
        if (comparison == 0)
            comparison = StringComparer.Ordinal.Compare(nodes[first.To].State.Address, nodes[second.To].State.Address);
        return comparison != 0 ? comparison : first.Kind.CompareTo(second.Kind);
    }
}
