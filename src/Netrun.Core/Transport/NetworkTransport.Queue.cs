namespace Netrun.Core.Transport;

/// <summary>Indexed scheduler транспорта: каждый active владеет не более чем одним heap slot.</summary>
public sealed partial class NetworkTransport
{
    /// <summary>Минимальная indexed heap по due и первоначальному serial без tombstones отменённых таймеров.</summary>
    private sealed class IndexedQueue
    {
        /// <summary>Собственные ссылки только на реально запланированные active.</summary>
        private readonly List<PendingRequest> _items = [];

        /// <summary>Самый ранний due либо null без выделения коллекции.</summary>
        internal PendingRequest? Peek() => _items.Count == 0 ? null : _items[0];

        /// <summary>Добавляет ровно один slot и восстанавливает heap за O(log active).</summary>
        internal void Add(PendingRequest item)
        {
            item.HeapIndex = _items.Count;
            _items.Add(item);
            Up(item.HeapIndex);
        }

        /// <summary>Удаляет exact item за O(log active), освобождая ссылку немедленно.</summary>
        internal void Remove(PendingRequest item)
        {
            var index = item.HeapIndex;
            var last = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            item.HeapIndex = -1;
            if (index == _items.Count) return;
            _items[index] = last;
            last.HeapIndex = index;
            if (index > 0 && Before(last, _items[(index - 1) / 2])) Up(index);
            else Down(index);
        }

        /// <summary>Поднимает меньший due/serial к корню.</summary>
        private void Up(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (!Before(_items[index], _items[parent])) return;
                Swap(index, parent);
                index = parent;
            }
        }

        /// <summary>Опускает заменённый slot до восстановления обоих heap-инвариантов.</summary>
        private void Down(int index)
        {
            while (index * 2 + 1 < _items.Count)
            {
                var child = index * 2 + 1;
                if (child + 1 < _items.Count && Before(_items[child + 1], _items[child])) child++;
                if (!Before(_items[child], _items[index])) return;
                Swap(index, child);
                index = child;
            }
        }

        /// <summary>Перестановка одновременно обновляет обратные индексы обоих владельцев.</summary>
        private void Swap(int first, int second)
        {
            (_items[first], _items[second]) = (_items[second], _items[first]);
            _items[first].HeapIndex = first;
            _items[second].HeapIndex = second;
        }

        /// <summary>Первоначальный serial задаёт deterministic равенство timestamp.</summary>
        private static bool Before(PendingRequest first, PendingRequest second) => first.ScheduledAt < second.ScheduledAt ||
            first.ScheduledAt == second.ScheduledAt && first.Serial < second.Serial;
    }

    /// <summary>Удаляет только реальный slot; unscheduled ordered waiters не оставляют stale references.</summary>
    private void RemoveTimer(PendingRequest item)
    {
        if (item.HeapIndex >= 0) _queues[item.HeapLane].Remove(item);
    }

    /// <summary>Выбирает единственный следующий due/deadline; ordered followers без deadline не занимают heap.</summary>
    private void Schedule(PendingRequest item)
    {
        RemoveTimer(item);
        var due = double.PositiveInfinity;
        if (item.Failure is not null) due = SimulationTimeDs;
        else if (item.State == RequestState.ResponseInTransit || item.State == RequestState.Queued &&
            (item.OrderedNode is null || ReferenceEquals(item.OrderedNode.List!.First, item.OrderedNode))) due = item.Due;
        if (item.Request.DeadlineDs is { } deadline) due = Math.Min(due, deadline);
        if (double.IsPositiveInfinity(due)) return;
        item.ScheduledAt = due;
        item.HeapLane = item.State == RequestState.Queued && item.Request.CoalescingKey is not null && item.Failure is null ? 1 : 0;
        _queues[item.HeapLane].Add(item);
    }

    /// <summary>Weighted выбор готового lane не допускает голодания background при непрерывном ordinary ingress.</summary>
    private PendingRequest? NextDue()
    {
        var ordinary = _queues[0].Peek();
        var background = _queues[1].Peek();
        if (ordinary?.ScheduledAt > SimulationTimeDs) ordinary = null;
        if (background?.ScheduledAt > SimulationTimeDs) background = null;
        if (background is not null && (ordinary is null || _ordinaryStreak >= _options.OrdinaryWeight))
        {
            _ordinaryStreak = 0;
            return background;
        }
        if (ordinary is not null)
        {
            _ordinaryStreak = Math.Min(_options.OrdinaryWeight, _ordinaryStreak + 1);
            return ordinary;
        }
        return null;
    }
}
