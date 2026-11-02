using System.Collections.ObjectModel;

namespace Netrun.Core;

/// <summary>Явный тип payload: null, scalar, массив и объект не смешиваются при передаче между языками.</summary>
public enum NetworkValueKind
{
    /// <summary>Явное null-значение, отличное от отсутствующей C#-ссылки.</summary>
    Null,
    /// <summary>Логическое значение.</summary>
    Boolean,
    /// <summary>Конечное число double; NaN и infinity запрещены.</summary>
    Number,
    /// <summary>Unicode-текст, допускающий управляющие символы как данные.</summary>
    Text,
    /// <summary>Упорядоченный массив, включая пустой.</summary>
    Array,
    /// <summary>Объект с уникальными ordinal-ключами, включая пустой.</summary>
    Object
}

/// <summary>
/// Неизменяемое типизированное дерево payload без зависимости от JSON.
/// Фабрики копируют коллекции и проверяют пределы всего дерева, включая повторные ссылки на один child.
/// </summary>
public abstract class NetworkValue : IEquatable<NetworkValue>
{
    /// <summary>Максимальная глубина дерева; корень имеет глубину 1.</summary>
    public const int MaxDepth = 32;
    /// <summary>Максимум всех значений дерева; ключи объекта не считаются отдельными значениями.</summary>
    public const int MaxTotalValues = 4096;
    /// <summary>Верхняя консервативная оценка JSON-тела payload в байтах, без transport envelope.</summary>
    public const int MaxEstimatedJsonBytes = 256 * 1024;
    /// <summary>Максимум UTF-16 code units текста до проверки общего encoded budget.</summary>
    public const int MaxTextLength = 65_536;
    /// <summary>Максимум элементов одного массива или свойств одного объекта.</summary>
    public const int MaxContainerEntries = 1024;
    /// <summary>Максимум UTF-16 code units одного ключа объекта; пустой ключ допустим.</summary>
    public const int MaxKeyLength = 128;

    /// <summary>Один неизменяемый экземпляр явного null для всех payload.</summary>
    private static readonly NetworkNull NullValue = new();
    /// <summary>Неизменяемый true не требует отдельного объекта на каждое сообщение.</summary>
    private static readonly NetworkBoolean TrueValue = new(true);
    /// <summary>Неизменяемый false не требует отдельного объекта на каждое сообщение.</summary>
    private static readonly NetworkBoolean FalseValue = new(false);

    /// <summary>Закрывает иерархию для внешних mutable-подтипов, которые могли бы обойти инварианты.</summary>
    private protected NetworkValue() { }

    /// <summary>Дискриминатор значения; не выводится из формы пустой коллекции.</summary>
    public abstract NetworkValueKind Kind { get; }

    /// <summary>Явное null-значение; C# null запрещён внутри payload-коллекций.</summary>
    public static NetworkNull Null => NullValue;

    /// <summary>Создаёт логическое значение без изменения его типа или числового приведения.</summary>
    public static NetworkBoolean FromBoolean(bool value) => value ? TrueValue : FalseValue;

    /// <summary>Создаёт конечное double; нечисловые и бесконечные значения отвергаются без вывода данных.</summary>
    public static NetworkNumber FromNumber(double value) => RequireValid(new NetworkNumber(value));

    /// <summary>Создаёт текст, сохраняя пустую строку, пробелы и controls; повреждённый Unicode запрещён.</summary>
    public static NetworkText FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Отсекаем слишком длинный вход до вычисления строкового hash в конструкторе.
        if (!ValidUnicode(value, MaxTextLength))
            throw InvalidValue();
        return RequireValid(new NetworkText(value));
    }

    /// <summary>
    /// Копирует ограниченное перечисление ссылок на неизменяемые children.
    /// Перечисление прекращается на 1025-м элементе; безграничный материализующий ToArray не используется.
    /// </summary>
    public static NetworkArray FromArray(IEnumerable<NetworkValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = new List<NetworkValue>();
        foreach (var item in items)
        {
            if (copy.Count == MaxContainerEntries || item is null)
                throw InvalidValue();
            copy.Add(item);
        }
        return RequireValid(new NetworkArray(copy.ToArray()));
    }

    /// <summary>
    /// Копирует ограниченный объект; ordinal-дубли ключей отвергаются, а не перезаписываются.
    /// Имена и значения не включаются в исключения; исходный словарь после возврата можно изменять независимо.
    /// </summary>
    public static NetworkObject FromObject(IEnumerable<KeyValuePair<string, NetworkValue>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var copy = new Dictionary<string, NetworkValue>(StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            if (copy.Count == MaxContainerEntries || !ValidUnicode(key, MaxKeyLength) || value is null ||
                !copy.TryAdd(key, value))
                throw InvalidValue();
        }
        return RequireValid(new NetworkObject(copy));
    }

    /// <summary>Проверяет типы, Unicode, глубину, суммарное число значений и conservative encoded budget всего дерева.</summary>
    public bool Validate()
    {
        var valuesRemaining = MaxTotalValues;
        var bytesRemaining = MaxEstimatedJsonBytes;
        return ValidateTree(this, 1, ref valuesRemaining, ref bytesRemaining);
    }

    /// <summary>Сравнивает полную структуру: порядок массива значим, порядок ключей объекта не значим.</summary>
    public bool DeepEquals(NetworkValue? other) => Equals(other);

    /// <summary>Типизированное структурное сравнение без сериализации и сравнения диагностических строк.</summary>
    public bool Equals(NetworkValue? other) => ReferenceEquals(this, other) ||
        other is not null && Kind == other.Kind && EqualsCore(other);

    /// <summary>Object equality использует те же типы и структуру, что и DeepEquals.</summary>
    public sealed override bool Equals(object? obj) => obj is NetworkValue value && Equals(value);

    /// <summary>Кэшированный hash неизменяемого дерева; равные объекты не зависят от порядка вставки ключей.</summary>
    public sealed override int GetHashCode() => StructuralHash;

    /// <summary>Безопасная диагностика содержит только тип и число элементов, без текста, чисел и ключей payload.</summary>
    public sealed override string ToString() => this switch
    {
        NetworkArray array => $"NetworkValue(Array, count={array.Items.Count})",
        NetworkObject map => $"NetworkValue(Object, count={map.Properties.Count})",
        _ => $"NetworkValue({Kind})"
    };

    /// <summary>Подтипы один раз вычисляют hash после defensive copy и затем возвращают неизменяемый результат.</summary>
    private protected abstract int StructuralHash { get; }

    /// <summary>Сравнивает данные уже совпавшего дискриминатора; внешние подтипы закрытой иерархии запрещены.</summary>
    private protected abstract bool EqualsCore(NetworkValue other);

    /// <summary>Применяет глобальные ограничения фабрики; не возвращает частично допустимый container.</summary>
    private static T RequireValid<T>(T value) where T : NetworkValue => value.Validate() ? value : throw InvalidValue();

    /// <summary>Структурно проверяет до 4096 значений и 32 уровней; прекращает обход сразу при исчерпании бюджета.</summary>
    private static bool ValidateTree(NetworkValue value, int depth, ref int valuesRemaining, ref int bytesRemaining)
    {
        if (depth > MaxDepth || valuesRemaining-- <= 0)
            return false;
        switch (value)
        {
            case NetworkNull:
                return ConsumeBytes(4, ref bytesRemaining);
            case NetworkBoolean boolean:
                return ConsumeBytes(boolean.Value ? 4 : 5, ref bytesRemaining);
            case NetworkNumber number:
                // 32 байта покрывают знак, round-trip mantissa и exponent любого конечного double.
                return double.IsFinite(number.Value) && ConsumeBytes(32, ref bytesRemaining);
            case NetworkText text:
                return ConsumeString(text.Value, MaxTextLength, ref bytesRemaining);
            case NetworkArray array:
                if (array.Items.Count > MaxContainerEntries ||
                    !ConsumeBytes(2 + Math.Max(0, array.Items.Count - 1), ref bytesRemaining))
                    return false;
                foreach (var item in array.Items)
                    if (item is null || !ValidateTree(item, depth + 1, ref valuesRemaining, ref bytesRemaining))
                        return false;
                return true;
            case NetworkObject map:
                if (map.Properties.Count > MaxContainerEntries ||
                    !ConsumeBytes(2 + Math.Max(0, map.Properties.Count - 1) + map.Properties.Count, ref bytesRemaining))
                    return false;
                foreach (var (key, child) in map.Properties)
                    if (!ConsumeString(key, MaxKeyLength, ref bytesRemaining) || child is null ||
                        !ValidateTree(child, depth + 1, ref valuesRemaining, ref bytesRemaining))
                        return false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Вычитает неотрицательную оценку, не допуская integer overflow или отрицательного остатка.</summary>
    private static bool ConsumeBytes(int count, ref int remaining)
    {
        if (count > remaining)
            return false;
        remaining -= count;
        return true;
    }

    /// <summary>
    /// Оценивает JSON-строку без сериализации: безопасные ASCII буквы/цифры/пробел стоят 1 байт,
    /// остальные UTF-16 единицы консервативно стоят 6 байт Unicode escape; добавляются две кавычки.
    /// </summary>
    private static bool ConsumeString(string value, int maximum, ref int bytesRemaining)
    {
        if (!ValidUnicode(value, maximum))
            return false;
        var estimate = 2;
        foreach (var character in value)
            estimate += character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' ? 1 : 6;
        return ConsumeBytes(estimate, ref bytesRemaining);
    }

    /// <summary>Пустые строки и controls допустимы; строка не должна содержать непарные UTF-16 surrogate.</summary>
    private static bool ValidUnicode(string? value, int maximum)
    {
        if (value is null || value.Length > maximum)
            return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i]))
                    return false;
            }
            else if (char.IsLowSurrogate(value[i]))
                return false;
        }
        return true;
    }

    /// <summary>Единая безопасная ошибка не включает scalar, ключ или другую часть payload.</summary>
    private static ArgumentException InvalidValue() => new("Invalid or excessive network payload.");
}

/// <summary>Единственное явное null-значение; отличается от пустых массива и объекта.</summary>
public sealed class NetworkNull : NetworkValue
{
    /// <summary>Создаётся только singleton-фабрикой.</summary>
    internal NetworkNull() { }
    /// <inheritdoc />
    public override NetworkValueKind Kind => NetworkValueKind.Null;
    /// <inheritdoc />
    private protected override int StructuralHash => (int)NetworkValueKind.Null;
    /// <inheritdoc />
    private protected override bool EqualsCore(NetworkValue other) => true;
}

/// <summary>Неизменяемый boolean; не равен числу 0/1.</summary>
public sealed class NetworkBoolean : NetworkValue
{
    /// <summary>Неизменяемый hash scalar вычисляется при создании.</summary>
    private readonly int _hash;
    /// <summary>Фиксирует значение при создании singleton true/false.</summary>
    internal NetworkBoolean(bool value)
    {
        Value = value;
        _hash = HashCode.Combine(NetworkValueKind.Boolean, value);
    }
    /// <summary>Исходное логическое значение.</summary>
    public bool Value { get; }
    /// <inheritdoc />
    public override NetworkValueKind Kind => NetworkValueKind.Boolean;
    /// <inheritdoc />
    private protected override int StructuralHash => _hash;
    /// <inheritdoc />
    private protected override bool EqualsCore(NetworkValue other) => Value == ((NetworkBoolean)other).Value;
}

/// <summary>Неизменяемое конечное double; положительный и отрицательный ноль семантически равны.</summary>
public sealed class NetworkNumber : NetworkValue
{
    /// <summary>Неизменяемый hash сохраняет одинаковую семантику для +0 и -0.</summary>
    private readonly int _hash;
    /// <summary>Фабрика отдельно проверяет конечность числа и пределы дерева.</summary>
    internal NetworkNumber(double value)
    {
        Value = value;
        _hash = HashCode.Combine(NetworkValueKind.Number, value);
    }
    /// <summary>Числовое значение без округления к целому.</summary>
    public double Value { get; }
    /// <inheritdoc />
    public override NetworkValueKind Kind => NetworkValueKind.Number;
    /// <inheritdoc />
    private protected override int StructuralHash => _hash;
    /// <inheritdoc />
    private protected override bool EqualsCore(NetworkValue other) => Value.Equals(((NetworkNumber)other).Value);
}

/// <summary>Неизменяемый Unicode-текст; не является идентификатором или исполняемым адресом.</summary>
public sealed class NetworkText : NetworkValue
{
    /// <summary>Структурный hash текста не пересчитывает длинную строку при каждом обращении.</summary>
    private readonly int _hash;
    /// <summary>Строка C# неизменяема; её ссылка сохраняется без лишнего копирования содержимого.</summary>
    internal NetworkText(string value)
    {
        Value = value;
        _hash = HashCode.Combine(NetworkValueKind.Text, StringComparer.Ordinal.GetHashCode(value));
    }
    /// <summary>Текст payload; явное чтение доступно адаптеру, но ToString его не раскрывает.</summary>
    public string Value { get; }
    /// <inheritdoc />
    public override NetworkValueKind Kind => NetworkValueKind.Text;
    /// <inheritdoc />
    private protected override int StructuralHash => _hash;
    /// <inheritdoc />
    private protected override bool EqualsCore(NetworkValue other) => string.Equals(Value, ((NetworkText)other).Value, StringComparison.Ordinal);
}

/// <summary>Read-only defensive copy упорядоченных значений; children также неизменяемы.</summary>
public sealed class NetworkArray : NetworkValue
{
    /// <summary>Кэш структурного hash вычисляется один раз по порядку всех children.</summary>
    private readonly int _hash;
    /// <summary>Получает уже скопированный фабрикой массив; владение передаётся закрытому wrapper.</summary>
    internal NetworkArray(NetworkValue[] ownedItems)
    {
        Items = System.Array.AsReadOnly(ownedItems);
        var hash = new HashCode();
        hash.Add(NetworkValueKind.Array);
        hash.Add(Items.Count);
        foreach (var item in Items)
            hash.Add(item.GetHashCode());
        _hash = hash.ToHashCode();
    }
    /// <summary>Упорядоченные children; wrapper не позволяет изменить backing array.</summary>
    public IReadOnlyList<NetworkValue> Items { get; }
    /// <inheritdoc />
    public override NetworkValueKind Kind => NetworkValueKind.Array;
    /// <inheritdoc />
    private protected override int StructuralHash => _hash;
    /// <inheritdoc />
    private protected override bool EqualsCore(NetworkValue other) => Items.SequenceEqual(((NetworkArray)other).Items);
}

/// <summary>Read-only defensive copy объекта с ordinal-ключами; порядок вставки не влияет на семантику.</summary>
public sealed class NetworkObject : NetworkValue
{
    /// <summary>Кэш order-independent hash; коммутативное сложение пар не требует сортировки на каждом вызове.</summary>
    private readonly int _hash;
    /// <summary>Получает уже скопированный словарь с уникальными ключами и закрывает его от внешнего изменения.</summary>
    internal NetworkObject(Dictionary<string, NetworkValue> ownedProperties)
    {
        Properties = new ReadOnlyDictionary<string, NetworkValue>(ownedProperties);
        var pairsHash = 0;
        foreach (var (key, value) in Properties)
            pairsHash = unchecked(pairsHash + HashCode.Combine(StringComparer.Ordinal.GetHashCode(key), value.GetHashCode()));
        _hash = HashCode.Combine(NetworkValueKind.Object, Properties.Count, pairsHash);
    }
    /// <summary>Свойства с уникальными ordinal-ключами; значения не сериализуются неявно.</summary>
    public IReadOnlyDictionary<string, NetworkValue> Properties { get; }
    /// <inheritdoc />
    public override NetworkValueKind Kind => NetworkValueKind.Object;
    /// <inheritdoc />
    private protected override int StructuralHash => _hash;
    /// <inheritdoc />
    private protected override bool EqualsCore(NetworkValue other)
    {
        var properties = ((NetworkObject)other).Properties;
        return Properties.Count == properties.Count && Properties.All(pair =>
            properties.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));
    }
}
