using Netrun.Core;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет межъязыковой canonical checksum независимо от JSON и жизненного цикла mirror.</summary>
public sealed class SnapshotChecksumTests
{
    /// <summary>SHA-256 одного prefix NETRUN-SNAPSHOT-1\0, независимо вычисленный Python hashlib.</summary>
    private const string EmptyDigest = "74298a4eed545a6d11cb260a1d2e798cd47a30838b7eee37f7f18965de4bb68b";

    /// <summary>
    /// Эталон 108 canonical байтов, вручную записанных hex по спецификации, затем проверенных Python hashlib.
    /// UTF-8 адрес «А» занимает два байта d090, а не одну UTF-16 единицу.
    /// </summary>
    private const string GoldenDigest = "8df3cedc4227df0ae71339722af0cde256de66ef9a1061b81da3f48711f2b27f";

    /// <summary>Создаёт короткий upsert эталона с различимыми координатами и версиями.</summary>
    private static NodeUpsert Upsert() =>
        new(new NodeFact("n1", 1, 2, new NodeState("А", "apc", new Position(10, 20, 1), "s1", true)));

    /// <summary>Создаёт tombstone эталона, различимый от upsert по тегу, поколению и версии.</summary>
    private static NodeRemove Remove() => new("n2", 3, 4);

    /// <summary>Пустой snapshot также привязан к версии canonical формата через фиксированный prefix.</summary>
    [Fact]
    public void EmptySnapshotMatchesIndependentPrefixDigest() =>
        Assert.Equal(EmptyDigest, SnapshotChecksum.Compute([]));

    /// <summary>Фиксированный digest обнаруживает смену endian, длины UTF-8, порядка полей или тега записи.</summary>
    [Fact]
    public void UnicodeAndTombstoneMatchIndependentCanonicalGolden()
    {
        var chunk = new SnapshotChunk("snapshot", 1, [Upsert(), Remove()]);
        Assert.Equal(GoldenDigest, SnapshotChecksum.Compute([chunk]));
    }

    /// <summary>Границы chunks входят в hash, поэтому склейка сохраняющих порядок записей меняет digest.</summary>
    [Fact]
    public void ChunkBoundariesAndRecordOrderArePartOfDigest()
    {
        var combined = new SnapshotChunk("snapshot", 1, [Upsert(), Remove()]);
        var split = new[] { new SnapshotChunk("snapshot", 1, [Upsert()]), new SnapshotChunk("snapshot", 2, [Remove()]) };
        var reordered = new SnapshotChunk("snapshot", 1, [Remove(), Upsert()]);
        Assert.NotEqual(SnapshotChecksum.Compute([combined]), SnapshotChecksum.Compute(split));
        Assert.NotEqual(SnapshotChecksum.Compute([combined]), SnapshotChecksum.Compute([reordered]));
    }

    /// <summary>ID относится к заголовку сессии и не меняет checksum одинаковых chunks разных снимков.</summary>
    [Fact]
    public void SnapshotIdentityDoesNotChangeCanonicalDigest()
    {
        var first = new SnapshotChunk("first-snapshot", 1, [Upsert()]);
        Assert.Equal(SnapshotChecksum.Compute([first]), SnapshotChecksum.Compute([first with { SnapshotId = "another-snapshot" }]));
    }

    /// <summary>Один accumulator не принимает chunks разных снимков, хотя ID не хэшируется.</summary>
    [Fact]
    public void MixingSnapshotIdentitiesIsRejected()
    {
        Assert.Throws<ArgumentException>(() => SnapshotChecksum.Compute([
            new SnapshotChunk("first", 1, [Upsert()]), new SnapshotChunk("second", 2, [Remove()])]));
    }

    /// <summary>Индекс chunk начинается с единицы; пропущенные и переполненные последовательности отвергаются.</summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(2UL)]
    [InlineData(ulong.MaxValue)]
    public void FirstChunkMustHaveIndexOne(ulong index) =>
        Assert.Throws<ArgumentException>(() => SnapshotChecksum.Compute([new SnapshotChunk("snapshot", index, [])]));

    /// <summary>Повтор индекса не превращается в повторный hashing того же fragment.</summary>
    [Fact]
    public void DuplicateChunkIndexIsRejected()
    {
        var chunk = new SnapshotChunk("snapshot", 1, [Upsert()]);
        Assert.Throws<ArgumentException>(() => SnapshotChecksum.Compute([chunk, chunk]));
    }

    /// <summary>Лимит одного chunk проверяется независимо от общего размера snapshot.</summary>
    [Fact]
    public void ChunkRecordLimitIsEnforced()
    {
        var maximum = new SnapshotChunk("snapshot", 1, Enumerable.Repeat<WorldChange>(Remove(), 128).ToArray());
        Assert.Equal(64, SnapshotChecksum.Compute([maximum]).Length);
        var excessive = maximum with { Changes = Enumerable.Repeat<WorldChange>(Remove(), 129).ToArray() };
        Assert.Throws<ArgumentException>(() => SnapshotChecksum.Compute([excessive]));
    }

    /// <summary>Некорректный доменный вход не доходит до UTF-8 encoding или hashing.</summary>
    [Fact]
    public void InvalidChunksAndFactsAreRejectedWithSafeDiagnostics()
    {
        var invalid = new SnapshotChunk[]
        {
            null!,
            new("snapshot", 1, null!),
            new(" ", 1, []),
            new("\ud800", 1, []),
            new("snapshot", 1, [null!]),
            new("snapshot", 1, [new SnapshotBegin("nested-control", 0)]),
            new("snapshot", 1, [new NodeUpsert(null!)]),
            new("snapshot", 1, [new NodeRemove("n1", 0, 1)]),
            new("snapshot", 1, [new NodeRemove("n1", 1, 0)]),
            new("snapshot", 1, [Upsert() with { Node = Upsert().Node with { State = null! } }]),
            new("snapshot", 1, [Upsert() with { Node = Upsert().Node with { State = Upsert().Node.State with { Address = "\ud800" } } }]),
            new("snapshot", 1, [Upsert() with { Node = Upsert().Node with { State = Upsert().Node.State with { Position = new(0, 1, 1) } } }])
        };
        foreach (var chunk in invalid)
        {
            var error = Assert.Throws<ArgumentException>(() => SnapshotChecksum.Compute([chunk]));
            Assert.StartsWith("Invalid snapshot chunks.", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("nested-control", error.Message, StringComparison.Ordinal);
        }
        Assert.Throws<ArgumentNullException>(() => SnapshotChecksum.Compute(null!));
    }

    /// <summary>GetDigest не сбрасывает поток: последующий chunk продолжает prefix и предыдущие записи.</summary>
    [Fact]
    public void ReadingDigestDoesNotResetAccumulator()
    {
        var first = new SnapshotChunk("snapshot", 1, [Upsert()]);
        var second = new SnapshotChunk("snapshot", 2, [Remove()]);
        using var accumulator = new SnapshotChecksumAccumulator();
        Assert.Equal(EmptyDigest, accumulator.GetDigest());
        Assert.True(accumulator.TryAppend(first));
        var intermediate = accumulator.GetDigest();
        Assert.Equal(intermediate, accumulator.GetDigest());
        Assert.Equal(SnapshotChecksum.Compute([first]), intermediate);
        Assert.True(accumulator.TryAppend(second));
        Assert.Equal(SnapshotChecksum.Compute([first, second]), accumulator.GetDigest());
        Assert.Equal(2UL, accumulator.ChunkCount);
        Assert.Equal(2UL, accumulator.RecordCount);
    }

    /// <summary>Поздняя ошибка записи не должна менять hash даже после валидного начала того же chunk.</summary>
    [Fact]
    public void RejectedAppendDoesNotChangeHashCountsOrSnapshotIdentity()
    {
        using var accumulator = new SnapshotChecksumAccumulator();
        var invalidFirst = new SnapshotChunk("wrong-snapshot", 1, [Upsert(), new NodeRemove("n2", 0, 4)]);
        Assert.False(accumulator.TryAppend(invalidFirst));
        Assert.Equal(EmptyDigest, accumulator.GetDigest());
        Assert.Equal(0UL, accumulator.ChunkCount);
        Assert.Equal(0UL, accumulator.RecordCount);

        var first = new SnapshotChunk("snapshot", 1, [Upsert()]);
        var second = new SnapshotChunk("snapshot", 2, [Remove()]);
        Assert.True(accumulator.TryAppend(first));
        var before = accumulator.GetDigest();
        Assert.False(accumulator.TryAppend(second with { Changes = [Remove(), new NodeRemove("n3", 1, 0)] }));
        Assert.Equal(before, accumulator.GetDigest());
        Assert.Equal(1UL, accumulator.ChunkCount);
        Assert.Equal(1UL, accumulator.RecordCount);
        Assert.True(accumulator.TryAppend(second));
        Assert.Equal(SnapshotChecksum.Compute([first, second]), accumulator.GetDigest());
    }

    /// <summary>Освобождение идемпотентно; использование закрытого cryptographic state явно запрещено.</summary>
    [Fact]
    public void DisposedAccumulatorCannotBeReadOrExtended()
    {
        var accumulator = new SnapshotChecksumAccumulator();
        accumulator.Dispose();
        accumulator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => accumulator.GetDigest());
        Assert.Throws<ObjectDisposedException>(() => accumulator.TryAppend(new("snapshot", 1, [])));
    }
}
