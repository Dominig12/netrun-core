using System.Collections;

namespace Netrun.Core;

/// <summary>Неизменяемая ограниченная последовательность со сравнением по содержимому.</summary>
/// <typeparam name="T">Неизменяемый элемент доменного контракта.</typeparam>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    /// <summary>Собственная копия; наружу не выдаётся даже как массив.</summary>
    private readonly T[] _items;

    /// <summary>Internal cooperative owners may transfer a fully populated array without a second traversal.</summary>
    private ValueList(T[] items) => _items = items;

    /// <summary>Transfers exclusive array ownership; caller must discard every mutable alias after this call.</summary>
    internal static ValueList<T> TakeOwnership(T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(items);
    }

    /// <summary>Копирует максимум maximum элементов, отказывая до неограниченного перечисления.</summary>
    public ValueList(IEnumerable<T> items, int maximum = 4096)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        var copy = new List<T>();
        foreach (var item in items)
        {
            if (copy.Count >= maximum)
                throw new ArgumentException("Collection limit exceeded.", nameof(items));
            copy.Add(item);
        }
        _items = copy.ToArray();
    }

    /// <summary>Количество элементов собственной копии.</summary>
    public int Count => _items.Length;
    /// <summary>Элемент по индексу без предоставления mutable-ссылки на коллекцию.</summary>
    public T this[int index] => _items[index];
    /// <summary>Перечисляет стабильное содержимое без копирования.</summary>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    /// <summary>Перечисление через стандартный негeneric интерфейс.</summary>
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    /// <summary>Порядок элементов является частью доменной идентичности.</summary>
    public bool Equals(ValueList<T>? other) => other is not null && _items.SequenceEqual(other._items);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
            hash.Add(item);
        return hash.ToHashCode();
    }
}
