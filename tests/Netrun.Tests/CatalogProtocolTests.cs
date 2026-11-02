using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Контрактные проверки полных фактов minor 2 и обратной совместимости исходных схем.</summary>
public sealed class CatalogProtocolTests
{
    /// <summary>Полное положение обычного узла в minor 2 обязано явно содержать nullable network.</summary>
    private const string Node = """
        {"kind":"node_upsert","node_id":"node-1","generation":"1","node_revision":"2","state":{
        "address":"APC-1","node_type":"apc","position":{"x":10,"y":20,"z":3},"sector_id":"sector-1","online":true,"network":null}}
        """;

    /// <summary>Native-порядок этажей намеренно отличается от числовой сортировки.</summary>
    private const string Sector = """{"kind":"sector_upsert","sector_id":"sector-1","generation":"1","sector_revision":"2","levels":[3,1,2]}""";

    /// <summary>Service instance, family и node owner — независимые идентичности.</summary>
    private const string Service = """
        {"kind":"service_upsert","service_id":"service-1","generation":"1","service_revision":"2",
        "owner":{"node_id":"node-1","generation":"4"},"family":"atmos.device","tag":null,"online":true,
        "operations":[{"operation_id":"status","safety":"read_only","delivery":"request_reply","ordered":false}]}
        """;

    /// <summary>Все поля network обязательны; null допускается только в документированных ссылках.</summary>
    private const string Network = """
        {"base_area_id":"area-1","supplied_area_id":null,"department":"engineering","is_ntnet_relay":true,
        "relay_operational":false,"is_station_level":true,"is_mining_level":false,"initial_anchor":{"node_id":"anchor-1","generation":"3"}}
        """;

    /// <summary>Набор всех новых facts и nullable geometry для независимого round-trip каждого kind.</summary>
    public static IEnumerable<object[]> ExtendedEntries()
    {
        yield return [Sector];
        yield return ["""{"kind":"sector_remove","sector_id":"sector-1","generation":"1","sector_revision":"3"}"""];
        yield return [Service];
        yield return ["""{"kind":"service_remove","service_id":"service-1","generation":"1","service_revision":"3"}"""];
        yield return [Node.Replace("\"network\":null", "\"network\":" + Network, StringComparison.Ordinal)];
        yield return [Node.Replace("{\"x\":10,\"y\":20,\"z\":3}", "null", StringComparison.Ordinal)
            .Replace("\"sector_id\":\"sector-1\"", "\"sector_id\":null", StringComparison.Ordinal)];
    }

    /// <summary>Каждый новый kind и отсутствующая позиция проходят общий доменный validator и сохраняются при Encode.</summary>
    [Theory]
    [MemberData(nameof(ExtendedEntries))]
    public void ExtendedFactsRoundTripWithMinorTwo(string entry)
    {
        var decoded = Decode(entry);
        Assert.True(FactValidation.IsValid(Assert.Single(decoded.Entries)));
        var encoded = BatchJson.Encode(decoded);
        Assert.Equal(2, Minor(encoded));
        var restored = BatchJson.Decode(encoded);
        Assert.Equal(decoded.Entries, restored.Entries);
        Assert.Equal(encoded, BatchJson.Encode(restored));
    }

    /// <summary>Новые catalog kinds сохраняют строгость схемы на корневом уровне записи.</summary>
    [Fact]
    public void CatalogFactsRejectMissingDuplicateAndUnknownRootFields()
    {
        foreach (var entry in ExtendedEntries().Take(4).Select(data => (string)data[0]))
        {
            var property = entry.Contains("service_id", StringComparison.Ordinal) ? "service_id" : "sector_id";
            var id = property == "service_id" ? "service-1" : "sector-1";
            var field = "\"" + property + "\":\"" + id + "\"";
            AssertSchema(entry.Replace(field + ",", "", StringComparison.Ordinal));
            AssertSchema(entry.Replace(field, field + "," + field, StringComparison.Ordinal));
            AssertSchema(entry[..^1] + ",\"unknown\":1}");
        }
    }

    /// <summary>Один extended факт поднимает версию всей оболочки, включая явный network:null на соседнем старом узле.</summary>
    [Fact]
    public void ExtendedBatchWritesNetworkFieldForEveryNode()
    {
        var decoded = Decode(Node + "," + Sector);
        var encoded = BatchJson.Encode(decoded);
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(2, Minor(encoded));
        Assert.Equal(JsonValueKind.Null,
            json.RootElement.GetProperty("entries")[0].GetProperty("state").GetProperty("network").ValueKind);
        Assert.Equal(decoded.Entries, BatchJson.Decode(encoded).Entries);
    }

    /// <summary>Minor 2 не делает network или nullable geometry необязательным patch-полем.</summary>
    [Fact]
    public void MinorTwoRequiresExactNodeStateShapeAndConsistentNullableGeometry()
    {
        AssertSchema(Node.Replace(",\"network\":null", "", StringComparison.Ordinal));
        AssertSchema(Node.Replace("{\"x\":10,\"y\":20,\"z\":3}", "null", StringComparison.Ordinal));
        AssertSchema(Node.Replace("\"sector_id\":\"sector-1\"", "\"sector_id\":null", StringComparison.Ordinal));
        AssertSchema(Node.Replace("\"network\":null", "\"network\":true", StringComparison.Ordinal));
        AssertSchema(Node.Replace("\"network\":null", "\"network\":{}", StringComparison.Ordinal));
    }

    /// <summary>Пустые metadata-ссылки представлены null, тогда как flags и department сохраняются явно.</summary>
    [Fact]
    public void NetworkFactsPreserveNullableReferencesFlagsAndAnchor()
    {
        var batch = Decode(Node.Replace("\"network\":null", "\"network\":" + Network, StringComparison.Ordinal));
        var metadata = Assert.IsType<NodeUpsert>(Assert.Single(batch.Entries)).Node.State.Network!;
        Assert.Equal("area-1", metadata.BaseAreaId);
        Assert.Null(metadata.SuppliedAreaId);
        Assert.Equal("engineering", metadata.Department);
        Assert.True(metadata.IsNtnetRelay);
        Assert.False(metadata.RelayOperational);
        Assert.True(metadata.IsStationLevel);
        Assert.False(metadata.IsMiningLevel);
        Assert.Equal(new NodeKey("anchor-1", 3), metadata.InitialAnchor);
        var nullable = Network.Replace("\"area-1\"", "null", StringComparison.Ordinal)
            .Replace("{\"node_id\":\"anchor-1\",\"generation\":\"3\"}", "null", StringComparison.Ordinal);
        var nullMetadata = Assert.IsType<NodeUpsert>(Assert.Single(Decode(Node.Replace(
            "\"network\":null", "\"network\":" + nullable, StringComparison.Ordinal)).Entries)).Node.State.Network!;
        Assert.Null(nullMetadata.BaseAreaId);
        Assert.Null(nullMetadata.InitialAnchor);
    }

    /// <summary>Неправильные native metadata отвергаются до публикации факта и без приведения типов.</summary>
    [Theory]
    [InlineData("\"department\":\"engineering\"", "\"department\":\"unknown\"")]
    [InlineData("\"is_ntnet_relay\":true", "\"is_ntnet_relay\":1")]
    [InlineData("\"relay_operational\":false", "\"relay_operational\":null")]
    [InlineData("\"is_station_level\":true", "\"is_station_level\":\"true\"")]
    [InlineData("\"is_mining_level\":false", "\"is_mining_level\":[]")]
    [InlineData("\"base_area_id\":\"area-1\"", "\"base_area_id\":\" \"")]
    [InlineData("\"generation\":\"3\"", "\"generation\":\"0\"")]
    [InlineData("\"generation\":\"3\"", "\"generation\":3")]
    [InlineData("\"generation\":\"3\"", "\"generation\":\"3\",\"extra\":1")]
    public void InvalidNetworkMetadataIsRejected(string find, string replacement) =>
        AssertSchema(Node.Replace("\"network\":null", "\"network\":" + Network.Replace(find, replacement, StringComparison.Ordinal), StringComparison.Ordinal));

    /// <summary>Дубли, лишние поля и пропуски metadata не превращаются в silently defaulted flags.</summary>
    [Fact]
    public void NetworkMetadataRequiresAllAndOnlyKnownFields()
    {
        var missing = Network.Replace("\"supplied_area_id\":null,", "", StringComparison.Ordinal);
        var duplicate = Network.Replace("\"is_mining_level\":false", "\"is_mining_level\":false,\"is_mining_level\":false", StringComparison.Ordinal);
        foreach (var network in new[] { missing, duplicate, Network[..^1] + ",\"unknown\":1}" })
            AssertSchema(Node.Replace("\"network\":null", "\"network\":" + network, StringComparison.Ordinal));
        var excessive = Network.Replace("area-1", new string('x', 129), StringComparison.Ordinal);
        AssertSchema(Node.Replace("\"network\":null", "\"network\":" + excessive, StringComparison.Ordinal));
        var invalidUnicode = Network.Replace("area-1", "\\ud800", StringComparison.Ordinal);
        AssertSchema(Node.Replace("\"network\":null", "\"network\":" + invalidUnicode, StringComparison.Ordinal));
    }

    /// <summary>Порядок этажей не сортируется codec, а уникальность, диапазон и лимит проверяются полностью.</summary>
    [Fact]
    public void SectorLevelsPreserveNativeOrderAndRejectInvalidCollections()
    {
        var sector = Assert.IsType<SectorUpsert>(Assert.Single(Decode(Sector).Entries)).Sector;
        Assert.Equal(new[] { 3, 1, 2 }, sector.Levels);
        foreach (var levels in new[] { "[]", "null", "{}", "[1,1]", "[0]", "[4097]", "[\"1\"]", "[true]" })
            AssertSchema(Sector.Replace("[3,1,2]", levels, StringComparison.Ordinal));
        var maximum = "[" + string.Join(',', Enumerable.Range(1, 4096)) + "]";
        Assert.Equal(4096, Assert.IsType<SectorUpsert>(Assert.Single(Decode(Sector.Replace(
            "[3,1,2]", maximum, StringComparison.Ordinal)).Entries)).Sector.Levels.Count);
        var excessive = "[" + string.Join(',', Enumerable.Range(1, 4097)) + "]";
        AssertSchema(Sector.Replace("[3,1,2]", excessive, StringComparison.Ordinal));
    }

    /// <summary>Все enum-значения имеют точные wire-строки без Enum.Parse и числовых алиасов.</summary>
    [Theory]
    [InlineData(OperationSafety.ReadOnly, DeliverySemantics.DeliveryOnly, "read_only", "delivery_only")]
    [InlineData(OperationSafety.ReadOnly, DeliverySemantics.RequestReply, "read_only", "request_reply")]
    [InlineData(OperationSafety.Idempotent, DeliverySemantics.DeliveryOnly, "idempotent", "delivery_only")]
    [InlineData(OperationSafety.Idempotent, DeliverySemantics.RequestReply, "idempotent", "request_reply")]
    [InlineData(OperationSafety.Mutating, DeliverySemantics.DeliveryOnly, "mutating", "delivery_only")]
    [InlineData(OperationSafety.Mutating, DeliverySemantics.RequestReply, "mutating", "request_reply")]
    public void OperationContractsMapEveryAllowlistedEnum(OperationSafety safety, DeliverySemantics delivery, string safetyText, string deliveryText)
    {
        var entry = Service.Replace("read_only", safetyText, StringComparison.Ordinal).Replace("request_reply", deliveryText, StringComparison.Ordinal);
        var operation = Assert.Single(Assert.IsType<ServiceUpsert>(Assert.Single(Decode(entry).Entries)).Service.Operations);
        Assert.Equal(safety, operation.Safety);
        Assert.Equal(delivery, operation.Delivery);
        Assert.False(operation.Ordered);
        var encoded = Encoding.UTF8.GetString(BatchJson.Encode(Decode(entry)));
        Assert.Contains("\"safety\":\"" + safetyText + "\"", encoded, StringComparison.Ordinal);
        Assert.Contains("\"delivery\":\"" + deliveryText + "\"", encoded, StringComparison.Ordinal);
    }

    /// <summary>Operation allowlist ограничен 64 уникальными полными записями; неизвестные enum и поля запрещены.</summary>
    [Fact]
    public void ServiceOperationsRequireUniqueBoundedCompleteContracts()
    {
        const string operation = """{"operation_id":"status","safety":"read_only","delivery":"request_reply","ordered":false}""";
        foreach (var invalid in new[]
        {
            "[]", "null", "{}", "[null]", "[" + operation + "," + operation + "]",
            "[" + operation.Replace("read_only", "ReadOnly", StringComparison.Ordinal) + "]",
            "[" + operation.Replace("request_reply", "invalid", StringComparison.Ordinal) + "]",
            "[" + operation.Replace("\"ordered\":false", "\"ordered\":0", StringComparison.Ordinal) + "]",
            "[" + operation.Replace("\"safety\":\"read_only\",", "", StringComparison.Ordinal) + "]",
            "[" + operation[..^1] + ",\"extra\":1}]"
        })
            AssertSchema(Service.Replace("[" + operation + "]", invalid, StringComparison.Ordinal));
        var sixtyFour = "[" + string.Join(',', Enumerable.Range(0, 64).Select(i => operation.Replace("status", "operation" + i, StringComparison.Ordinal))) + "]";
        Assert.Equal(64, Assert.IsType<ServiceUpsert>(Assert.Single(Decode(Service.Replace("[" + operation + "]", sixtyFour, StringComparison.Ordinal)).Entries)).Service.Operations.Count);
        var excessive = "[" + string.Join(',', Enumerable.Range(0, 65).Select(i => operation.Replace("status", "operation" + i, StringComparison.Ordinal))) + "]";
        AssertSchema(Service.Replace("[" + operation + "]", excessive, StringComparison.Ordinal));
    }

    /// <summary>Все новые identity counters остаются positive ulong strings; owner не допускает числовое поколение.</summary>
    [Fact]
    public void CatalogAndOwnerIdentitiesRejectNoncanonicalCounters()
    {
        foreach (var invalid in new[] { "\"0\"", "\"01\"", "1", "null", "\"18446744073709551616\"" })
        {
            AssertSchema(Sector.Replace("\"sector_revision\":\"2\"", "\"sector_revision\":" + invalid, StringComparison.Ordinal));
            AssertSchema(Service.Replace("\"service_revision\":\"2\"", "\"service_revision\":" + invalid, StringComparison.Ordinal));
            AssertSchema(Service.Replace("\"generation\":\"4\"", "\"generation\":" + invalid, StringComparison.Ordinal));
        }
        var maximum = Service.Replace("\"generation\":\"4\"", "\"generation\":\"18446744073709551615\"", StringComparison.Ordinal);
        Assert.Equal(ulong.MaxValue, Assert.IsType<ServiceUpsert>(Assert.Single(Decode(maximum).Entries)).Service.Owner.Generation);
        foreach (var invalidOwner in new[] { "null", "{}", "{\"node_id\":\"node-1\",\"generation\":\"1\",\"extra\":1}" })
            AssertSchema(Service.Replace("{\"node_id\":\"node-1\",\"generation\":\"4\"}", invalidOwner, StringComparison.Ordinal));
    }

    /// <summary>Схемы 0/1 не принимают новые facts, network или nullable geometry; старые bytes не расширяются неявно.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OldMinorsKeepClosedSchemas(int minor)
    {
        foreach (var entry in new[] { Sector, Service,
            """{"kind":"sector_remove","sector_id":"s","generation":"1","sector_revision":"1"}""",
            """{"kind":"service_remove","service_id":"s","generation":"1","service_revision":"1"}""" })
            Assert.Equal("protocol_version", Assert.Throws<ProtocolException>(() => Decode(entry, minor)).Code);
        Assert.Throws<ProtocolException>(() => Decode(Node, minor));
        var legacy = Node.Replace(",\"network\":null", "", StringComparison.Ordinal);
        var encoded = BatchJson.Encode(Decode(legacy, minor));
        Assert.Equal(0, Minor(encoded));
        Assert.DoesNotContain("network", Encoding.UTF8.GetString(encoded), StringComparison.Ordinal);
    }

    /// <summary>Версия checksum явно обязательна в minor 2 и отсутствует в исходном minor 1.</summary>
    [Fact]
    public void SnapshotBeginChecksumVersionUsesExactVersionedFields()
    {
        const string begin = """{"kind":"snapshot_begin","snapshot_id":"s","journal_sequence":"0","checksum_version":2}""";
        var decoded = Decode(begin);
        Assert.Equal(2, Assert.IsType<SnapshotBegin>(Assert.Single(decoded.Entries)).ChecksumVersion);
        Assert.Equal(2, Minor(BatchJson.Encode(decoded)));
        AssertSchema(begin.Replace(",\"checksum_version\":2", "", StringComparison.Ordinal));
        foreach (var invalid in new[] { "0", "3", "null", "\"2\"", "true" })
            AssertSchema(begin.Replace("\"checksum_version\":2", "\"checksum_version\":" + invalid, StringComparison.Ordinal));
        Assert.Throws<ProtocolException>(() => Decode(begin, 1));
        var old = Decode(begin.Replace(",\"checksum_version\":2", "", StringComparison.Ordinal), 1);
        Assert.Equal(1, Assert.IsType<SnapshotBegin>(Assert.Single(old.Entries)).ChecksumVersion);
        Assert.Equal(1, Minor(BatchJson.Encode(old)));
        Assert.DoesNotContain("checksum_version", Encoding.UTF8.GetString(BatchJson.Encode(old)), StringComparison.Ordinal);
        var forced = BatchJson.Encode(Decode(begin.Replace(":2}", ":1}", StringComparison.Ordinal) + "," + Sector));
        Assert.Contains("\"checksum_version\":1", Encoding.UTF8.GetString(forced), StringComparison.Ordinal);
    }

    /// <summary>В chunk и catch-up расширяются только facts; рекурсивные controls остаются запрещёнными.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedCatalogFactsSelectMinorTwoWithoutAllowingNestedControls(bool catchUp)
    {
        var facts = Decode(Sector + "," + Service + "," + Node).Entries;
        WorldChange control = catchUp ? new CatchUpDelta("s", 1, facts) : new SnapshotChunk("s", 1, facts);
        var batch = new InputBatch(new("w", "e", "b"), 1, 0, [control]);
        var encoded = BatchJson.Encode(batch);
        Assert.Equal(2, Minor(encoded));
        var restored = Assert.Single(BatchJson.Decode(encoded).Entries);
        var nested = restored is SnapshotChunk chunk ? chunk.Changes : Assert.IsType<CatchUpDelta>(restored).Changes;
        Assert.Equal(facts, nested);
        var nestedControl = catchUp ? (WorldChange)new CatchUpDelta("s", 1, [new SnapshotBegin("s", 0, 2)]) : new SnapshotChunk("s", 1, [new SnapshotBegin("s", 0, 2)]);
        Assert.Throws<ProtocolException>(() => BatchJson.Encode(batch with { Entries = [nestedControl] }));
    }

    /// <summary>Исходные arrays не сохраняются контрактами, поэтому последующая мутация не меняет equality или wire snapshot.</summary>
    [Fact]
    public void FactCollectionsCloneSourcesAndCompareByContents()
    {
        var levels = new[] { 3, 1, 2 };
        var operations = new[] { new OperationContract("read", OperationSafety.ReadOnly, DeliverySemantics.RequestReply, false) };
        var sector = new SectorUpsert(new("s", 1, 1, new ValueList<int>(levels)));
        var service = new ServiceUpsert(new("service", 1, 1, new("n", 1), "camera.stream", "tag", true,
            new ValueList<OperationContract>(operations, 64)));
        var batch = new InputBatch(new("w", "e", "b"), 1, 0, [sector, service]);
        var before = BatchJson.Encode(batch);
        levels[0] = 4096;
        operations[0] = new OperationContract("changed");
        Assert.Equal(before, BatchJson.Encode(batch));
        Assert.Equal(batch.Entries, BatchJson.Decode(before).Entries);
    }

    /// <summary>Codec отвергает недопустимый локальный enum или network metadata также без входного JSON.</summary>
    [Fact]
    public void EncodeUsesSharedFactValidationForLocallyConstructedModels()
    {
        var valid = Assert.IsType<ServiceUpsert>(Assert.Single(Decode(Service).Entries));
        var invalidSafety = valid with { Service = valid.Service with { Operations = new([new OperationContract("read", (OperationSafety)100)], 64) } };
        var invalidDelivery = valid with { Service = valid.Service with { Operations = new([new OperationContract("read", Delivery: (DeliverySemantics)100)], 64) } };
        var node = Assert.IsType<NodeUpsert>(Assert.Single(Decode(Node).Entries));
        WorldChange[] invalid =
        [
            invalidSafety, invalidDelivery, new SectorUpsert(new("s", 1, 1, new([1, 1]))),
            node with { Node = node.Node with { State = node.Node.State with { Network = new("area", null, "unknown") } } },
            new SnapshotBegin("s", 0, 4)
        ];
        foreach (var change in invalid)
            Assert.Equal("schema_invalid", Assert.Throws<ProtocolException>(() =>
                BatchJson.Encode(new(new("w", "e", "b"), 1, 0, [change]))).Code);
    }

    /// <summary>Собирает строгую оболочку фактов с выбранной minor-версией без потери больших counters.</summary>
    private static InputBatch Decode(string entries, int minor = 2) => BatchJson.Decode(Encoding.UTF8.GetBytes($$"""
        {"protocol_major":1,"protocol_minor":{{minor}},"world_epoch":"w","engine_epoch":"e","binding_epoch":"b",
        "sequence":"1","simulation_time_ds":0,"entries":[{{entries}}]}
        """));

    /// <summary>Читает только диагностическое значение версии для проверки выбора writer.</summary>
    private static int Minor(byte[] encoded)
    {
        using var json = JsonDocument.Parse(encoded);
        return json.RootElement.GetProperty("protocol_minor").GetInt32();
    }

    /// <summary>Негативный contract case обязан возвращать безопасный schema code без echo JSON.</summary>
    private static void AssertSchema(string entry) =>
        Assert.Equal("schema_invalid", Assert.Throws<ProtocolException>(() => Decode(entry)).Code);
}
