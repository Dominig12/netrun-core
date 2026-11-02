using Netrun.Core;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет форму, immutability, структурную семантику и ограниченные ресурсы payload.</summary>
public sealed class NetworkValueTests
{
    /// <summary>Создаёт объект без синтаксического шума generic KeyValuePair в сценариях теста.</summary>
    private static NetworkObject Object(params (string Key, NetworkValue Value)[] properties) =>
        NetworkValue.FromObject(properties.Select(pair => new KeyValuePair<string, NetworkValue>(pair.Key, pair.Value)));

    /// <summary>Null и обе пустые коллекции сохраняют явную, различимую форму.</summary>
    [Fact]
    public void NullEmptyArrayAndEmptyObjectHaveDifferentKindsAndEquality()
    {
        var array = NetworkValue.FromArray([]);
        var map = Object();
        Assert.Same(NetworkValue.Null, NetworkValue.Null);
        Assert.Equal(NetworkValueKind.Null, NetworkValue.Null.Kind);
        Assert.Equal(NetworkValueKind.Array, array.Kind);
        Assert.Equal(NetworkValueKind.Object, map.Kind);
        Assert.False(NetworkValue.Null.DeepEquals(array));
        Assert.False(array.DeepEquals(map));
        Assert.False(map.DeepEquals(NetworkValue.Null));
        Assert.True(array.Validate());
        Assert.True(map.Validate());
    }

    /// <summary>Логические и числовые значения не приводятся друг к другу, пустой текст остаётся текстом.</summary>
    [Fact]
    public void ScalarTypesAndValuesRemainDistinct()
    {
        Assert.False(NetworkValue.FromBoolean(true).Equals(NetworkValue.FromNumber(1)));
        Assert.False(NetworkValue.FromText("1").Equals(NetworkValue.FromNumber(1)));
        Assert.False(NetworkValue.FromBoolean(true).Equals(NetworkValue.FromBoolean(false)));
        Assert.True(NetworkValue.FromNumber(0).Equals(NetworkValue.FromNumber(-0.0)));
        Assert.Equal(NetworkValue.FromNumber(0).GetHashCode(), NetworkValue.FromNumber(-0.0).GetHashCode());
        Assert.Equal("", NetworkValue.FromText("").Value);
        Assert.Equal(2.5, NetworkValue.FromNumber(2.5).Value);
    }

    /// <summary>Неопределённые и бесконечные числовые значения нельзя спрятать внутри wire-neutral модели.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NumbersMustBeFinite(double value) => Assert.Throws<ArgumentException>(() => NetworkValue.FromNumber(value));

    /// <summary>Порядок массива участвует в equality; object order не участвует, в том числе рекурсивно.</summary>
    [Fact]
    public void StructuralEqualityAndHashPreserveContainerSemantics()
    {
        var first = Object(("a", NetworkValue.FromNumber(1)), ("b", NetworkValue.FromArray([NetworkValue.Null, NetworkValue.FromText("text")])));
        var reordered = Object(("b", NetworkValue.FromArray([NetworkValue.Null, NetworkValue.FromText("text")])), ("a", NetworkValue.FromNumber(1)));
        Assert.True(first.DeepEquals(reordered));
        Assert.Equal(first, reordered);
        Assert.Equal(first.GetHashCode(), reordered.GetHashCode());
        var array = NetworkValue.FromArray([NetworkValue.FromNumber(1), NetworkValue.FromNumber(2)]);
        var swapped = NetworkValue.FromArray([NetworkValue.FromNumber(2), NetworkValue.FromNumber(1)]);
        Assert.False(array.DeepEquals(swapped));
        Assert.False(first.Equals(Object(("A", NetworkValue.FromNumber(1)), ("b", first.Properties["b"]))));
    }

    /// <summary>Изменение исходных коллекций не меняет payload и его кэшированный hash.</summary>
    [Fact]
    public void ContainersDefensivelyCopySourcesAndExposeReadOnlyViews()
    {
        var source = new List<NetworkValue> { NetworkValue.FromText("original") };
        var array = NetworkValue.FromArray(source);
        var properties = new Dictionary<string, NetworkValue> { ["key"] = array };
        var map = NetworkValue.FromObject(properties);
        var hash = map.GetHashCode();
        source[0] = NetworkValue.FromText("changed");
        source.Clear();
        properties.Clear();
        Assert.Equal("original", Assert.IsType<NetworkText>(array.Items[0]).Value);
        Assert.Same(array, map.Properties["key"]);
        Assert.Equal(hash, map.GetHashCode());
        Assert.Throws<NotSupportedException>(() => ((IList<NetworkValue>)array.Items)[0] = NetworkValue.Null);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, NetworkValue>)map.Properties).Clear());
    }

    /// <summary>Объект отвергает дубли без перезаписи, но регистр и пустой ключ имеют обычную JSON-семантику.</summary>
    [Fact]
    public void ObjectKeysUseOrdinalUniquenessAndBoundedUnicode()
    {
        Assert.Throws<ArgumentException>(() => Object(("secret-key", NetworkValue.Null), ("secret-key", NetworkValue.FromNumber(1))));
        Assert.Equal(3, Object(("a", NetworkValue.Null), ("A", NetworkValue.Null), ("", NetworkValue.Null)).Properties.Count);
        Assert.Throws<ArgumentException>(() => Object((new string('a', NetworkValue.MaxKeyLength + 1), NetworkValue.Null)));
        Assert.Single(Object((new string('a', NetworkValue.MaxKeyLength), NetworkValue.Null)).Properties);
    }

    /// <summary>Текст payload допускает controls и валидные surrogate pairs, но отвергает повреждённый UTF-16.</summary>
    [Fact]
    public void TextAllowsDataControlsButRejectsInvalidUnicode()
    {
        const string text = "line\n\t\0\"\\🙂";
        Assert.Equal(text, NetworkValue.FromText(text).Value);
        Assert.True(Object(("\n\0🙂", NetworkValue.FromText(text))).Validate());
        foreach (var invalid in new[] { "\ud800", "\udc00", "a\ud800b", "\ud800\ud800", "\udc00\ud800" })
        {
            Assert.Throws<ArgumentException>(() => NetworkValue.FromText(invalid));
            Assert.Throws<ArgumentException>(() => Object((invalid, NetworkValue.Null)));
        }
    }

    /// <summary>Ограничение текста проверяется до накопления больших деревьев; типичная ASCII-строка 64 KiB допустима.</summary>
    [Fact]
    public void TextLengthAndConservativeEncodedBudgetAreBothEnforced()
    {
        Assert.Equal(NetworkValue.MaxTextLength, NetworkValue.FromText(new string('a', NetworkValue.MaxTextLength)).Value.Length);
        Assert.Throws<ArgumentException>(() => NetworkValue.FromText(new string('a', NetworkValue.MaxTextLength + 1)));
        Assert.Throws<ArgumentException>(() => NetworkValue.FromText(new string('Ж', NetworkValue.MaxTextLength)));
        var text = NetworkValue.FromText(new string('a', NetworkValue.MaxTextLength));
        Assert.True(NetworkValue.FromArray([text, text, text]).Validate());
        Assert.Throws<ArgumentException>(() => NetworkValue.FromArray([text, text, text, text]));
    }

    /// <summary>Перечисление ограничивается 1025-м элементом и не пытается целиком материализовать бесконечный producer.</summary>
    [Fact]
    public void ContainerEnumerationsAreCappedBeforeUnboundedAllocation()
    {
        var arrayReads = 0;
        IEnumerable<NetworkValue> ArraySource()
        {
            while (true)
            {
                arrayReads++;
                yield return NetworkValue.Null;
            }
        }
        Assert.Throws<ArgumentException>(() => NetworkValue.FromArray(ArraySource()));
        Assert.Equal(NetworkValue.MaxContainerEntries + 1, arrayReads);

        var objectReads = 0;
        IEnumerable<KeyValuePair<string, NetworkValue>> ObjectSource()
        {
            while (true)
            {
                objectReads++;
                yield return new KeyValuePair<string, NetworkValue>(objectReads.ToString(), NetworkValue.Null);
            }
        }
        Assert.Throws<ArgumentException>(() => NetworkValue.FromObject(ObjectSource()));
        Assert.Equal(NetworkValue.MaxContainerEntries + 1, objectReads);
    }

    /// <summary>Корень имеет depth 1; 32-й уровень допустим, 33-й не создаётся фабрикой.</summary>
    [Fact]
    public void DepthLimitAppliesToWholeTree()
    {
        NetworkValue value = NetworkValue.Null;
        for (var depth = 1; depth < NetworkValue.MaxDepth; depth++)
            value = NetworkValue.FromArray([value]);
        Assert.True(value.Validate());
        Assert.Throws<ArgumentException>(() => NetworkValue.FromArray([value]));
    }

    /// <summary>Shared children считаются при каждом появлении; лимит защищает encoded дерево, а не только число объектов.</summary>
    [Fact]
    public void TotalValueLimitCountsRepeatedChildOccurrences()
    {
        var full = NetworkValue.FromArray(Enumerable.Repeat<NetworkValue>(NetworkValue.Null, 1024));
        var shortBranch = NetworkValue.FromArray(Enumerable.Repeat<NetworkValue>(NetworkValue.Null, 1019));
        Assert.True(NetworkValue.FromArray([full, full, full, shortBranch]).Validate());
        var excessiveBranch = NetworkValue.FromArray(Enumerable.Repeat<NetworkValue>(NetworkValue.Null, 1020));
        Assert.Throws<ArgumentException>(() => NetworkValue.FromArray([full, full, full, excessiveBranch]));
    }

    /// <summary>C# null не подменяет явный NetworkValue.Null и не проникает в коллекции.</summary>
    [Fact]
    public void MissingReferencesAreRejectedSeparatelyFromExplicitNull()
    {
        Assert.Throws<ArgumentNullException>(() => NetworkValue.FromText(null!));
        Assert.Throws<ArgumentNullException>(() => NetworkValue.FromArray(null!));
        Assert.Throws<ArgumentNullException>(() => NetworkValue.FromObject(null!));
        Assert.Throws<ArgumentException>(() => NetworkValue.FromArray([null!]));
        Assert.Throws<ArgumentException>(() => Object((null!, NetworkValue.Null)));
        Assert.Throws<ArgumentException>(() => Object(("key", null!)));
        Assert.False(NetworkValue.Null.Equals(null));
    }

    /// <summary>Ни диагностика, ни сообщения ожидаемых отказов не раскрывают scalar или ключи payload.</summary>
    [Fact]
    public void DiagnosticsNeverIncludePayloadContents()
    {
        const string secret = "never-log-this-value";
        NetworkValue[] values =
        [
            NetworkValue.FromText(secret), NetworkValue.FromNumber(123456789), NetworkValue.FromBoolean(true),
            NetworkValue.FromArray([NetworkValue.FromText(secret)]), Object((secret, NetworkValue.FromText(secret)))
        ];
        foreach (var value in values)
        {
            Assert.DoesNotContain(secret, value.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("123456789", value.ToString(), StringComparison.Ordinal);
        }
        var error = Assert.Throws<ArgumentException>(() => Object((secret, NetworkValue.Null), (secret, NetworkValue.Null)));
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }
}
