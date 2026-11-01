using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет minor 1, типы управления снимком и закрытую node-only вложенность.</summary>
public sealed class SyncProtocolTests
{
    /// <summary>Checksum является wire-данными: кодек проверяет форму, а содержимое снимка сверяет Core.</summary>
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Минимальное допустимое узловое изменение для chunks и журнала catch-up.</summary>
    private const string Remove = """{"kind":"node_remove","node_id":"n","generation":"1","node_revision":"2"}""";

    /// <summary>Полное состояние проверяет, что sync использует прежний контракт node_upsert без ослабления схемы.</summary>
    private const string Upsert = """
        {"kind":"node_upsert","node_id":"u","generation":"3","node_revision":"4",
        "state":{"address":"а","node_type":"server","position":{"x":1,"y":2,"z":1},"sector_id":"s","online":false}}
        """;

    /// <summary>Локальное расширение Core не должно автоматически расширять allowlist wire-кодека.</summary>
    private sealed record UnsupportedChange : WorldChange;

    /// <summary>Предоставляет все пять wire-контрактов; внутри коллекций порядок и виды записей различаются.</summary>
    public static IEnumerable<object[]> ControlEntries()
    {
        yield return ["""{"kind":"snapshot_begin","snapshot_id":"snapshot-a","journal_sequence":"0"}"""];
        yield return [$$"""{"kind":"snapshot_chunk","snapshot_id":"snapshot-a","chunk_index":"1","changes":[{{Remove}},{{Upsert}}]}"""];
        yield return [$$"""{"kind":"snapshot_commit","snapshot_id":"snapshot-a","chunk_count":"0","record_count":"0","sha256":"{{Hash}}"}"""];
        yield return [$$"""{"kind":"catch_up_delta","snapshot_id":"snapshot-a","journal_sequence":"0","changes":[{{Upsert}},{{Remove}}]}"""];
        yield return ["""{"kind":"catch_up_complete","snapshot_id":"snapshot-a","through_journal_sequence":"0"}"""];
    }

    /// <summary>Каждый новый kind сохраняет поля и порядок; Encode сам выбирает minor 1.</summary>
    [Theory]
    [MemberData(nameof(ControlEntries))]
    public void ControlsRoundTripAsMinorOne(string entry)
    {
        var decoded = Decode(entry);
        var control = Assert.Single(decoded.Entries);
        switch (control)
        {
            case SnapshotBegin begin:
                Assert.Equal("snapshot-a", begin.SnapshotId);
                Assert.Equal(0UL, begin.JournalSequence);
                break;
            case SnapshotChunk chunk:
                Assert.Equal("snapshot-a", chunk.SnapshotId);
                Assert.Equal(1UL, chunk.ChunkIndex);
                Assert.Equal(new NodeRemove("n", 1, 2), chunk.Changes[0]);
                Assert.Equal("u", Assert.IsType<NodeUpsert>(chunk.Changes[1]).Node.NodeId);
                break;
            case SnapshotCommit commit:
                Assert.Equal(new SnapshotCommit("snapshot-a", 0, 0, Hash), commit);
                break;
            case CatchUpDelta delta:
                Assert.Equal("snapshot-a", delta.SnapshotId);
                Assert.Equal(0UL, delta.JournalSequence);
                Assert.Equal("u", Assert.IsType<NodeUpsert>(delta.Changes[0]).Node.NodeId);
                Assert.Equal(new NodeRemove("n", 1, 2), delta.Changes[1]);
                break;
            case CatchUpComplete complete:
                Assert.Equal(new CatchUpComplete("snapshot-a", 0), complete);
                break;
            default:
                Assert.Fail("The control must decode as its allowlisted domain type.");
                break;
        }

        var encoded = BatchJson.Encode(decoded);
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(1, json.RootElement.GetProperty("protocol_minor").GetInt32());
        Assert.Equal(encoded, BatchJson.Encode(BatchJson.Decode(encoded)));
    }

    /// <summary>Minor 0 не может незаметно принять управляющее расширение.</summary>
    [Theory]
    [MemberData(nameof(ControlEntries))]
    public void MinorZeroRejectsEverySyncKind(string entry) =>
        Assert.Equal("protocol_version", Assert.Throws<ProtocolException>(() => Decode(entry, minor: 0)).Code);

    /// <summary>Обязательный snapshot_id и точный набор полей проверяются одинаково у каждого control.</summary>
    [Theory]
    [MemberData(nameof(ControlEntries))]
    public void ControlsRejectMissingDuplicateUnknownAndInvalidSnapshotIdentity(string entry)
    {
        AssertSchema(entry.Replace("\"snapshot_id\":\"snapshot-a\",", "", StringComparison.Ordinal));
        AssertSchema(entry.Replace("\"snapshot_id\":\"snapshot-a\"", "\"snapshot_id\":null", StringComparison.Ordinal));
        AssertSchema(entry.Replace("snapshot-a", new string('x', 129), StringComparison.Ordinal));
        AssertSchema(entry.Replace("snapshot-a", "   ", StringComparison.Ordinal));
        AssertSchema(entry.Replace("snapshot-a", "bad\\u0000id", StringComparison.Ordinal));
        AssertSchema(entry.Replace("\"snapshot_id\":\"snapshot-a\"",
            "\"snapshot_id\":\"snapshot-a\",\"snapshot_id\":\"other\"", StringComparison.Ordinal));
        AssertSchema(entry[..^1] + ",\"unknown\":1}");
    }

    /// <summary>Новые счётчики допускают только канонический строковый ноль, когда это разрешено конкретным полем.</summary>
    [Theory]
    [InlineData("journal_sequence", "snapshot_begin")]
    [InlineData("journal_sequence", "catch_up_delta")]
    [InlineData("through_journal_sequence", "catch_up_complete")]
    [InlineData("chunk_count", "snapshot_commit")]
    [InlineData("record_count", "snapshot_commit")]
    public void SyncCountersAllowZeroButRejectNoncanonicalOrNonstringValues(string property, string kind)
    {
        var entry = ControlEntries().Select(data => (string)data[0]).Single(json => json.Contains($"\"kind\":\"{kind}\"", StringComparison.Ordinal));
        foreach (var value in new[] { "00", "01", "+1", "-1", "1.0", "1e2", " 1", "18446744073709551616" })
            AssertSchema(entry.Replace($"\"{property}\":\"0\"", $"\"{property}\":\"{value}\"", StringComparison.Ordinal));
        foreach (var value in new[] { "0", "null", "true", "{}", "[]" })
            AssertSchema(entry.Replace($"\"{property}\":\"0\"", $"\"{property}\":{value}", StringComparison.Ordinal));
        var maximum = entry.Replace($"\"{property}\":\"0\"", $"\"{property}\":\"18446744073709551615\"", StringComparison.Ordinal);
        Assert.Single(Decode(maximum).Entries);
    }

    /// <summary>Индекс chunk начинается с единицы и остаётся каноническим ulong без округления.</summary>
    [Theory]
    [InlineData("\"0\"")]
    [InlineData("\"01\"")]
    [InlineData("\"18446744073709551616\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void ChunkIndexMustBePositiveCanonicalString(string value) =>
        AssertSchema($$"""{"kind":"snapshot_chunk","snapshot_id":"s","chunk_index":{{value}},"changes":[]}""");

    /// <summary>Checksum строго lowercase hex фиксированной длины; null, верхний регистр и другой алфавит не исправляются.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void CommitRejectsMalformedChecksum(string hash) =>
        AssertSchema($$"""{"kind":"snapshot_commit","snapshot_id":"s","chunk_count":"0","record_count":"0","sha256":"{{hash}}"}""");

    /// <summary>Checksum обязан быть строкой, даже если альтернативное JSON-значение само по себе синтаксически допустимо.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void CommitChecksumMustBeString(string jsonValue) =>
        AssertSchema($$"""{"kind":"snapshot_commit","snapshot_id":"s","chunk_count":"0","record_count":"0","sha256":{{jsonValue}}}""");

    /// <summary>Вложенные changes ограничены 128 узловыми записями; вложенный control и неизвестный kind всегда запрещены.</summary>
    [Theory]
    [InlineData("snapshot_chunk", "chunk_index", "1")]
    [InlineData("catch_up_delta", "journal_sequence", "0")]
    public void NestedChangesAreBoundedNodeOnlyArrays(string kind, string counter, string value)
    {
        // Конструирует оболочку для одного и того же набора отрицательных проверок chunk и delta.
        string Entry(string changes) => $$"""{"kind":"{{kind}}","snapshot_id":"s","{{counter}}":"{{value}}","changes":{{changes}}}""";
        Assert.Single(Decode(Entry("[]")).Entries);
        Assert.Single(Decode(Entry("[" + string.Join(',', Enumerable.Repeat(Remove, 128)) + "]")).Entries);
        AssertSchema(Entry("[" + string.Join(',', Enumerable.Repeat(Remove, 129)) + "]"));
        foreach (var invalid in new[] { "null", "{}", "[null]", "[1]", "[" + ControlEntries().First()[0] + "]" })
            AssertSchema(Entry(invalid));
        Assert.Equal("entry_kind", Assert.Throws<ProtocolException>(() =>
            Decode(Entry("[{\"kind\":\"unknown\"}]"))).Code);
        AssertSchema(Entry("[" + Remove.Replace("\"generation\":\"1\"", "\"generation\":\"0\"", StringComparison.Ordinal) + "]"));
    }

    /// <summary>Minor 1 принимает прежние node-записи, а их последующий Encode остаётся совместимым с minor 0.</summary>
    [Fact]
    public void NodeOnlyFramesRetainMinorZeroEncoding()
    {
        var batch = Decode(Remove);
        using var json = JsonDocument.Parse(BatchJson.Encode(batch));
        Assert.Equal(0, json.RootElement.GetProperty("protocol_minor").GetInt32());
        Assert.Equal(new NodeRemove("n", 1, 2), Assert.Single(batch.Entries));
    }

    /// <summary>Кодек сохраняет outer порядок и лимит, оставляя правило одного control за пакет сессии Core.</summary>
    [Fact]
    public void OuterMixedEntriesRemainOrderedAndBounded()
    {
        var begin = (string)ControlEntries().First()[0];
        var decoded = Decode(begin + "," + Remove);
        Assert.IsType<SnapshotBegin>(decoded.Entries[0]);
        Assert.IsType<NodeRemove>(decoded.Entries[1]);
        Assert.Equal(128, Decode(string.Join(',', Enumerable.Repeat(begin, 128))).Entries.Count);
        Assert.Throws<ProtocolException>(() => Decode(string.Join(',', Enumerable.Repeat(begin, 129))));
    }

    /// <summary>Локально созданная модель проходит ту же allowlist: null, recursive sync, плохой hash и нулевой chunk запрещены.</summary>
    [Fact]
    public void EncodeValidatesSyncModelsWithoutDependingOnWireParsing()
    {
        var epochs = new Epochs("w", "e", "b");
        WorldChange[] invalid =
        [
            new UnsupportedChange(),
            new SnapshotBegin(null!, 0),
            new SnapshotChunk("s", 0, []),
            new SnapshotChunk("s", 1, null!),
            new SnapshotChunk("s", 1, [new SnapshotBegin("s", 0)]),
            new SnapshotChunk("s", 1, Enumerable.Repeat<WorldChange>(new NodeRemove("n", 1, 1), 129).ToArray()),
            new SnapshotCommit("s", 0, 0, null!),
            new SnapshotCommit("s", 0, 0, Hash.ToUpperInvariant()),
            new CatchUpDelta("s", 0, null!),
            new CatchUpDelta("s", 0, [new CatchUpComplete("s", 0)]),
            new CatchUpDelta("s", 0, [null!]),
            new CatchUpComplete("", 0)
        ];
        foreach (var control in invalid)
            Assert.Equal("schema_invalid", Assert.Throws<ProtocolException>(() =>
                BatchJson.Encode(new InputBatch(epochs, 1, 0, [control]))).Code);
    }

    /// <summary>Несколько больших chunks не могут накопить неограниченное JSON-тело до проверки размера.</summary>
    [Fact]
    public void EncodeRejectsAggregateBodyAboveFrameLimit()
    {
        var node = new NodeUpsert(new NodeFact("n", 1, 1,
            new NodeState(new string('а', 256), "server", new Position(1, 2, 1), "s", true)));
        var chunk = new SnapshotChunk("s", 1, Enumerable.Repeat<WorldChange>(node, 128).ToArray());
        var batch = new InputBatch(new Epochs("w", "e", "b"), 1, 0, Enumerable.Repeat<WorldChange>(chunk, 128).ToArray());
        Assert.Equal("frame_size", Assert.Throws<ProtocolException>(() => BatchJson.Encode(batch)).Code);
    }

    /// <summary>Создаёт независимый envelope с выбранным minor; entry может содержать несколько outer записей.</summary>
    private static InputBatch Decode(string entry, int minor = 1) => BatchJson.Decode(Encoding.UTF8.GetBytes($$"""
        {"protocol_major":1,"protocol_minor":{{minor}},"world_epoch":"w","engine_epoch":"e","binding_epoch":"b",
        "sequence":"1","simulation_time_ds":0,"entries":[{{entry}}]}
        """));

    /// <summary>Требует безопасного отказа схемы вместо необработанного исключения или частичного разбора записи.</summary>
    private static void AssertSchema(string entry) =>
        Assert.Equal("schema_invalid", Assert.Throws<ProtocolException>(() => Decode(entry)).Code);
}
