namespace Netrun.Core.Topology;

/// <summary>Расположенный APC с локальным индексом и координатой этажа внутри одного сектора.</summary>
/// <param name="Index">Индекс address-sorted массива APC этого сектора.</param>
/// <param name="Key">Идентичность physical generation.</param>
/// <param name="Address">Ordinal tie-break.</param>
/// <param name="X">Физический X тайла.</param>
/// <param name="Y">Физический Y тайла.</param>
/// <param name="Floor">Индекс исходного ordered Z stack.</param>
internal sealed record SpatialPoint(int Index, NodeKey Key, string Address, int X, int Y, int Floor);

/// <summary>Точный balanced kd-tree для Chebyshev nearest и межкомпонентного поиска; без quadratic edge table.</summary>
internal sealed class SpatialIndex
{
    /// <summary>Одна точка, bounding box поддерева и optional uniform component для Boruvka pruning.</summary>
    private sealed class TreeNode
    {
        /// <summary>APC в медиане выбранной оси.</summary>
        internal required SpatialPoint Point;
        /// <summary>Левая половина независимого дерева.</summary>
        internal TreeNode? Left;
        /// <summary>Правая половина независимого дерева.</summary>
        internal TreeNode? Right;
        /// <summary>Нижняя граница X всех точек поддерева.</summary>
        internal int MinX;
        /// <summary>Верхняя граница X всех точек поддерева.</summary>
        internal int MaxX;
        /// <summary>Нижняя граница Y всех точек поддерева.</summary>
        internal int MinY;
        /// <summary>Верхняя граница Y всех точек поддерева.</summary>
        internal int MaxY;
        /// <summary>Нижняя граница индекса этажа.</summary>
        internal int MinFloor;
        /// <summary>Верхняя граница индекса этажа.</summary>
        internal int MaxFloor;
        /// <summary>Минимальный адрес поддерева позволяет безопасно отсечь проигрывающие равные расстояния.</summary>
        internal required string MinAddress;
        /// <summary>Root компоненты, если поддерево однородно; -1 означает смесь.</summary>
        internal int UniformComponent = -1;
    }

    /// <summary>Корень собственного balanced дерева; исходный список при построении не изменяется.</summary>
    private readonly TreeNode? _root;

    /// <summary>Один раз строит дерево; сортировка диапазонов не удерживает mutable input arrays.</summary>
    internal SpatialIndex(IEnumerable<SpatialPoint> points)
    {
        var copy = points.ToArray();
        _root = Build(copy, 0, copy.Length, 0);
    }

    /// <summary>Возвращает до count ближайших подходящих APC в полном порядке distance/address.</summary>
    internal IReadOnlyList<SpatialPoint> Nearest(int x, int y, int floor, int count,
        int excludedIndex = -1, int? excludedComponent = null, DisjointSet? components = null)
    {
        var best = new List<(SpatialPoint Point, int Distance)>(count);
        Visit(_root);
        return best.Select(candidate => candidate.Point).ToArray();

        // Сначала посещает ближайший bounding box; равные расстояния не отсекаются из-за address tie-break.
        void Visit(TreeNode? node)
        {
            if (node is null || excludedComponent is { } excluded && node.UniformComponent == excluded)
                return;
            if (best.Count == count)
            {
                var lower = LowerBound(node, x, y, floor);
                if (lower > best[^1].Distance || lower == best[^1].Distance &&
                    StringComparer.Ordinal.Compare(node.MinAddress, best[^1].Point.Address) >= 0)
                    return;
            }
            var point = node.Point;
            if (point.Index != excludedIndex &&
                (excludedComponent is null || components!.Find(point.Index) != excludedComponent.Value))
            {
                var distance = Distance(x, y, floor, point);
                var index = 0;
                while (index < best.Count && (best[index].Distance < distance ||
                    best[index].Distance == distance && StringComparer.Ordinal.Compare(best[index].Point.Address, point.Address) < 0))
                    index++;
                if (index < count)
                {
                    best.Insert(index, (point, distance));
                    if (best.Count > count)
                        best.RemoveAt(best.Count - 1);
                }
            }
            if (node.Left is not null && node.Right is not null &&
                LowerBound(node.Right, x, y, floor) < LowerBound(node.Left, x, y, floor))
            {
                Visit(node.Right);
                Visit(node.Left);
            }
            else
            {
                Visit(node.Left);
                Visit(node.Right);
            }
        }
    }

    /// <summary>Пересчитывает component summaries раз в раунд Boruvka, чтобы пропускать уже объединённые поддеревья.</summary>
    internal void RefreshComponents(DisjointSet components) => Refresh(_root, components);

    /// <summary>Обновляет uniform root снизу вверх за линейное время относительно APC сектора.</summary>
    private static int Refresh(TreeNode? node, DisjointSet components)
    {
        if (node is null)
            return -1;
        var root = components.Find(node.Point.Index);
        var left = Refresh(node.Left, components);
        var right = Refresh(node.Right, components);
        node.UniformComponent = (node.Left is null || left == root) && (node.Right is null || right == root) ? root : -1;
        return node.UniformComponent;
    }

    /// <summary>Выбирает медиану оси и сохраняет bounds; глубина дерева логарифмическая даже при совпадающих координатах.</summary>
    private static TreeNode? Build(SpatialPoint[] points, int offset, int length, int depth)
    {
        if (length == 0)
            return null;
        var axis = depth % 3;
        Array.Sort(points, offset, length, Comparer<SpatialPoint>.Create((a, b) =>
        {
            var comparison = Coordinate(a, axis).CompareTo(Coordinate(b, axis));
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Address, b.Address);
        }));
        var middle = offset + length / 2;
        var point = points[middle];
        var node = new TreeNode
        {
            Point = point,
            MinAddress = point.Address,
            Left = Build(points, offset, length / 2, depth + 1),
            Right = Build(points, middle + 1, length - length / 2 - 1, depth + 1),
            MinX = point.X,
            MaxX = point.X,
            MinY = point.Y,
            MaxY = point.Y,
            MinFloor = point.Floor,
            MaxFloor = point.Floor
        };
        Include(node, node.Left);
        Include(node, node.Right);
        return node;
    }

    /// <summary>Читает одну из трёх индексируемых координат без временных массивов.</summary>
    private static int Coordinate(SpatialPoint point, int axis) => axis switch { 0 => point.X, 1 => point.Y, _ => point.Floor };

    /// <summary>Расширяет bounding box родителя bounds одного дочернего поддерева.</summary>
    private static void Include(TreeNode parent, TreeNode? child)
    {
        if (child is null)
            return;
        parent.MinX = Math.Min(parent.MinX, child.MinX);
        parent.MaxX = Math.Max(parent.MaxX, child.MaxX);
        parent.MinY = Math.Min(parent.MinY, child.MinY);
        parent.MaxY = Math.Max(parent.MaxY, child.MaxY);
        parent.MinFloor = Math.Min(parent.MinFloor, child.MinFloor);
        parent.MaxFloor = Math.Max(parent.MaxFloor, child.MaxFloor);
        if (StringComparer.Ordinal.Compare(child.MinAddress, parent.MinAddress) < 0)
            parent.MinAddress = child.MinAddress;
    }

    /// <summary>Нижняя Chebyshev-граница расстояния до всего поддерева, включая исходный порядок этажей.</summary>
    private static int LowerBound(TreeNode node, int x, int y, int floor) =>
        Math.Max(Math.Max(Math.Max(node.MinX - x, x - node.MaxX), Math.Max(node.MinY - y, y - node.MaxY)),
            Math.Max(0, Math.Max(node.MinFloor - floor, floor - node.MaxFloor)));

    /// <summary>Точное расстояние до точки внутри одного связанного сектора.</summary>
    internal static int Distance(int x, int y, int floor, SpatialPoint point) =>
        Math.Max(Math.Max(Math.Abs(x - point.X), Math.Abs(y - point.Y)), Math.Abs(floor - point.Floor));
}

/// <summary>Union-find с path compression и union by size; память линейна по числу APC.</summary>
internal sealed class DisjointSet
{
    /// <summary>Parent indices, не ссылки на native nodes.</summary>
    private readonly int[] _parents;
    /// <summary>Размеры root-компонент для ограничения глубины.</summary>
    private readonly int[] _sizes;
    /// <summary>Создаёт отдельную компоненту на каждый APC.</summary>
    internal DisjointSet(int count)
    {
        _parents = Enumerable.Range(0, count).ToArray();
        _sizes = Enumerable.Repeat(1, count).ToArray();
    }
    /// <summary>Возвращает root и сжимает путь в собственном массиве.</summary>
    internal int Find(int index)
    {
        while (_parents[index] != index)
        {
            _parents[index] = _parents[_parents[index]];
            index = _parents[index];
        }
        return index;
    }
    /// <summary>Соединяет различные компоненты; false означает уже существующую связность.</summary>
    internal bool Union(int first, int second)
    {
        first = Find(first);
        second = Find(second);
        if (first == second)
            return false;
        if (_sizes[first] < _sizes[second])
            (first, second) = (second, first);
        _parents[second] = first;
        _sizes[first] += _sizes[second];
        return true;
    }
}
