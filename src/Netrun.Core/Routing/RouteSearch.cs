using Netrun.Core.Topology;

namespace Netrun.Core.Routing;

/// <summary>
/// Общий weighted Dijkstra для point-to-point (NetworkRouter) и полного дерева (SourceRouteTreeProvider):
/// одна forwarding/relay/relax политика на оба провайдера, чтобы их поведение не расходилось.
/// Политики доступности/стоимостей должны быть чистыми и стабильными в пределах одного обхода;
/// пролог валидации, построение результата и кеш остаются за вызывающим провайдером.
/// </summary>
internal static class RouteSearch
{
    /// <summary>Приоритет Dijkstra: положительная стоимость, затем ordinal адрес для воспроизводимого выбора.</summary>
    internal readonly record struct QueuePriority(double Cost, string Address) : IComparable<QueuePriority>
    {
        /// <summary>Сравнение не зависит от порядка Dictionary и одинаково для всех nodes очереди.</summary>
        public int CompareTo(QueuePriority other)
        {
            var comparison = Cost.CompareTo(other.Cost);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(Address, other.Address);
        }
    }

    /// <summary>Не допускает NaN, Infinity или неположительную стоимость в приоритеты и итоговые скаляры.</summary>
    internal static double PositiveCost(double cost)
    {
        if (!double.IsFinite(cost) || cost <= 0)
            throw new RoutingException("invalid_route_cost");
        return cost;
    }

    /// <summary>Единая формула выборки доступности: overlay при наличии, иначе неизменяемые флаги самого графа.</summary>
    internal static NodeAvailability Availability(GraphSnapshot graph, INetworkAvailability? availability, NodeKey key)
    {
        var state = graph.Nodes[key].State;
        return availability?.Get(key) ?? new NodeAvailability(state.Online, state.Network?.RelayOperational ?? true);
    }

    /// <summary>
    /// Выполняет weighted Dijkstra от уже проверенного online источника, заполняя <paramref name="entries"/>
    /// достижимыми поколениями (predecessor, накопленная стоимость, признак quantum-пути). Один uniform
    /// quantum bucket расширяется только от самого дешёвого допустимого relay. Если задан
    /// <paramref name="destination"/>, обход останавливается сразу после его settle и возвращает true;
    /// без цели строится всё дерево и возвращается false.
    /// </summary>
    internal static bool Run(GraphSnapshot graph, NodeKey source, RouteKind mode, NodeKey? destination,
        INetworkAvailability? availability, IRouteCostPolicy? costs, int maxQueueEntries,
        Dictionary<NodeKey, SourceRouteEntry> entries)
    {
        var sampled = new Dictionary<NodeKey, NodeAvailability>();
        var settled = new HashSet<NodeKey>();
        var queue = new PriorityQueue<NodeKey, QueuePriority>();
        entries[source] = new SourceRouteEntry(null, 0, false);
        queue.Enqueue(source, new QueuePriority(0, graph.Nodes[source].State.Address));
        var relaysExpanded = false;
        var backboneEnabled = availability?.BackboneEnabled ?? true;
        while (queue.TryDequeue(out var current, out var priority))
        {
            if (settled.Contains(current) || priority.Cost != entries[current].LatencyDs)
                continue;
            settled.Add(current);
            if (destination is not null && current == destination)
                return true;
            var currentState = State(current);
            if (!currentState.Present || !currentState.Online)
                continue;
            foreach (var edge in graph.Adjacency[current])
            {
                var neighbor = edge.From == current ? edge.To : edge.From;
                if (settled.Contains(neighbor))
                    continue;
                var state = State(neighbor);
                if (!state.Present || mode == RouteKind.Machine && !state.Online)
                    continue;
                Relax(current, neighbor, priority.Cost, costs?.PhysicalCost(edge) ?? edge.LatencyDs, false);
            }
            if (relaysExpanded || !backboneEnabled || !IsRelay(current) || !currentState.RelayOperational)
                continue;
            relaysExpanded = true;
            var relayCost = PositiveCost(costs?.RelayCost(graph) ?? graph.RelayLatencyDs);
            foreach (var relay in graph.RelayBucket)
            {
                if (settled.Contains(relay))
                    continue;
                var state = State(relay);
                if (!state.Present || !state.RelayOperational || mode == RouteKind.Machine && !state.Online)
                    continue;
                Relax(current, relay, priority.Cost, relayCost, true);
            }
        }
        return false;

        // Один snapshot доступности на NodeKey в пределах обхода, включая negative samples.
        NodeAvailability State(NodeKey key)
        {
            if (!sampled.TryGetValue(key, out var value))
            {
                value = Availability(graph, availability, key);
                sampled.Add(key, value);
            }
            return value;
        }

        // Null-position relay не является членом quantum bucket даже при сохранённом native-флаге.
        bool IsRelay(NodeKey key) => graph.Nodes[key].State.Position is not null &&
            graph.Nodes[key].State.Network?.IsNtnetRelay == true;

        // Равная стоимость не заменяет predecessor или Quantum: первый выбор фиксирован priority-адресом.
        void Relax(NodeKey from, NodeKey to, double currentCost, double edgeCost, bool quantum)
        {
            var total = currentCost + PositiveCost(edgeCost);
            if (!double.IsFinite(total))
                throw new RoutingException("invalid_route_cost");
            if (entries.TryGetValue(to, out var prior) && prior.LatencyDs <= total)
                return;
            if (queue.Count >= maxQueueEntries)
                throw new RoutingException("route_queue_capacity");
            entries[to] = new SourceRouteEntry(from, total, quantum || entries[from].Quantum);
            queue.Enqueue(to, new QueuePriority(total, graph.Nodes[to].State.Address));
        }
    }
}
