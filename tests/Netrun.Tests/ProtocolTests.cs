using System.Buffers.Binary;
using System.Text;
using Netrun.Core;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет строгую схему обмена и чтение кадров независимо от границ потокового I/O.</summary>
public sealed class ProtocolTests
{
    /// <summary>Эталон с максимальным ulong, кириллицей и разными видами записей для проверки точности и порядка.</summary>
    private const string Valid = """
        {"protocol_major":1,"protocol_minor":0,"world_epoch":"world-a","engine_epoch":"engine-a",
         "binding_epoch":"binding-a","sequence":"18446744073709551615","simulation_time_ds":42,"entries":[
         {"kind":"node_upsert","node_id":"n1","generation":"1","node_revision":"2","state":{
          "address":"Адрес","node_type":"server","position":{"x":10,"y":20,"z":2},"sector_id":"s1","online":true}},
         {"kind":"node_remove","node_id":"n2","generation":"3","node_revision":"4"}]}
        """;

    /// <summary>Повторное кодирование сохраняет содержимое, порядок записей и Unicode без округления счётчика.</summary>
    [Fact]
    public void RoundTripPreservesOrderIdentityAndUnicode()
    {
        var batch = Decode(Valid);
        Assert.Equal(ulong.MaxValue, batch.Sequence);
        Assert.Equal(42, batch.SimulationTimeDs);
        Assert.Equal(new Epochs("world-a", "engine-a", "binding-a"), batch.Epochs);
        var upsert = Assert.IsType<NodeUpsert>(batch.Entries[0]);
        Assert.Equal("Адрес", upsert.Node.State.Address);
        Assert.Equal(new Position(10, 20, 2), upsert.Node.State.Position);
        Assert.Equal(new NodeRemove("n2", 3, 4), batch.Entries[1]);
        var restored = BatchJson.Decode(BatchJson.Encode(batch));
        Assert.Equal(batch.Epochs, restored.Epochs);
        Assert.Equal(batch.Sequence, restored.Sequence);
        Assert.Equal(batch.SimulationTimeDs, restored.SimulationTimeDs);
        Assert.Equal(batch.Entries, restored.Entries);
    }

    /// <summary>Запрещает неоднозначные десятичные строки и переполнение у всех видов wire-счётчиков.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("١")]
    public void RejectsNoncanonicalCounters(string value)
    {
        AssertSchema(Valid.Replace("\"18446744073709551615\"", $"\"{value}\"", StringComparison.Ordinal));
        AssertSchema(Valid.Replace("\"generation\":\"1\"", $"\"generation\":\"{value}\"", StringComparison.Ordinal));
        AssertSchema(Valid.Replace("\"node_revision\":\"2\"", $"\"node_revision\":\"{value}\"", StringComparison.Ordinal));
    }

    /// <summary>Числа, null и коллекции не должны неявно преобразовываться в строковые идентификаторы последовательности.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void CountersMustBeStrings(string jsonValue) =>
        AssertSchema(Valid.Replace("\"18446744073709551615\"", jsonValue, StringComparison.Ordinal));

    /// <summary>Любой пропуск, повтор, расширение или нарушение типа/диапазона отклоняется до применения пакета.</summary>
    [Theory]
    [InlineData("\"protocol_major\":1", "\"protocol_major\":1,\"protocol_major\":1")]
    [InlineData("\"protocol_minor\":0", "\"protocol_minor\":0,\"unexpected\":0")]
    [InlineData("\"generation\":\"1\"", "\"generation\":\"1\",\"generation\":\"2\"")]
    [InlineData("\"online\":true", "\"online\":true,\"extra\":true")]
    [InlineData("\"x\":10", "\"x\":10,\"x\":10")]
    [InlineData("\"world_epoch\":\"world-a\"", "\"world_epoch\":null")]
    [InlineData("\"world_epoch\":\"world-a\"", "\"world_epoch\":\"   \"")]
    [InlineData("\"node_id\":\"n1\"", "\"node_id\":\"   \"")]
    [InlineData("\"x\":10", "\"x\":0")]
    [InlineData("\"x\":10", "\"x\":32768")]
    [InlineData("\"y\":20", "\"y\":-1")]
    [InlineData("\"z\":2", "\"z\":4097")]
    [InlineData("\"online\":true", "\"online\":1")]
    [InlineData("\"simulation_time_ds\":42", "\"simulation_time_ds\":2147483648")]
    [InlineData("\"simulation_time_ds\":42", "\"simulation_time_ds\":-1")]
    [InlineData("\"simulation_time_ds\":42", "\"simulation_time_ds\":\"42\"")]
    [InlineData("\"world_epoch\":\"world-a\",", "")]
    public void RejectsUnknownDuplicateMissingAndInvalidProperties(string find, string replacement) =>
        AssertSchema(Valid.Replace(find, replacement, StringComparison.Ordinal));

    /// <summary>Неизвестный kind не превращается в игнорируемое изменение при сохранении cumulative ACK.</summary>
    [Fact]
    public void RejectsUnknownEntryKind() =>
        Assert.Equal("entry_kind", Assert.Throws<ProtocolException>(() =>
            Decode(Valid.Replace("node_upsert", "request_submit", StringComparison.Ordinal))).Code);

    /// <summary>Новая версия протокола требует явной поддержки, а не оптимистичного разбора прежней схемой.</summary>
    [Theory]
    [InlineData("\"protocol_major\":1", "\"protocol_major\":2")]
    [InlineData("\"protocol_minor\":0", "\"protocol_minor\":1")]
    public void RejectsUnsupportedVersion(string find, string replacement) =>
        Assert.Equal("protocol_version", Assert.Throws<ProtocolException>(() =>
            Decode(Valid.Replace(find, replacement, StringComparison.Ordinal))).Code);

    /// <summary>Повреждённый текст отклоняется; безопасная диагностика не содержит ни секретного значения, ни JSON.</summary>
    [Fact]
    public void RejectsInvalidUtf8AndMalformedJsonWithoutEchoingInput()
    {
        var invalidUtf8 = Encoding.UTF8.GetBytes(Valid);
        invalidUtf8[0] = 0xff;
        Assert.Equal("invalid_utf8", Assert.Throws<ProtocolException>(() => BatchJson.Decode(invalidUtf8)).Code);
        var error = Assert.Throws<ProtocolException>(() => Decode("{\"secret\":\"never-log-this"));
        Assert.Equal("invalid_json", error.Code);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("never-log-this", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Лимиты работы, строк, вложенности и тела действуют как для wire-входа, так и для локального Encode.</summary>
    [Fact]
    public void EnforcesEntryTextDepthAndBodyLimits()
    {
        var batch = Decode(Valid);
        Assert.Throws<ProtocolException>(() => BatchJson.Encode(batch with
        {
            Entries = Enumerable.Repeat<WorldChange>(new NodeRemove("n", 1, 1), 129).ToArray()
        }));
        AssertSchema(Valid.Replace("world-a", new string('a', 129), StringComparison.Ordinal));
        AssertSchema(Valid.Replace("world-a", "world\\u0000a", StringComparison.Ordinal));
        AssertSchema(Valid.Replace("Адрес", new string('a', 257), StringComparison.Ordinal));
        Assert.Throws<ProtocolException>(() => Decode(new string('[', 33) + "0" + new string(']', 33)));
        Assert.Equal("frame_size", Assert.Throws<ProtocolException>(() => BatchJson.Decode([])).Code);
        Assert.Equal("frame_size", Assert.Throws<ProtocolException>(() =>
            BatchJson.Decode(new byte[FramedStream.MaxFrameBytes + 1])).Code);
    }

    /// <summary>Проверяет обе стороны границы 128 записей; пустой пакет остаётся допустимым продвижением времени.</summary>
    [Fact]
    public void DecodeEnforcesEntryLimitAndAcceptsEmptyBatches()
    {
        const string entry = """{"kind":"node_remove","node_id":"n","generation":"1","node_revision":"1"}""";
        const string envelope = """
            {"protocol_major":1,"protocol_minor":0,"world_epoch":"w","engine_epoch":"e",
            "binding_epoch":"b","sequence":"1","simulation_time_ds":0,"entries":[ENTRIES]}
            """;
        Assert.Empty(Decode(envelope.Replace("ENTRIES", "", StringComparison.Ordinal)).Entries);
        Assert.Equal(128, Decode(envelope.Replace("ENTRIES",
            string.Join(',', Enumerable.Repeat(entry, 128)), StringComparison.Ordinal)).Entries.Count);
        AssertSchema(envelope.Replace("ENTRIES",
            string.Join(',', Enumerable.Repeat(entry, 129)), StringComparison.Ordinal));
    }

    /// <summary>Однобайтовые чтения не смешивают соседние кадры; EOF допустим только после полного второго кадра.</summary>
    [Fact]
    public async Task ReadsFragmentedAndConcatenatedFramesAndCleanEof()
    {
        var body = BatchJson.Encode(Decode(Valid));
        using var output = new MemoryStream();
        await FramedStream.WriteAsync(output, body);
        await FramedStream.WriteAsync(output, body);
        using var input = new FragmentedStream(output.ToArray(), 1);
        Assert.Equal(body, await FramedStream.ReadAsync(input));
        Assert.Equal(body, await FramedStream.ReadAsync(input));
        Assert.Null(await FramedStream.ReadAsync(input));
    }

    /// <summary>Нулевая и слишком большая объявленная длина запрещаются ещё до попытки получить тело.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1_048_577u)]
    [InlineData(uint.MaxValue)]
    public async Task RejectsInvalidPrefixBeforeReadingBody(uint size)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, size);
        using var input = new PrefixOnlyStream(prefix);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => FramedStream.ReadAsync(input));
        Assert.Equal("frame_size", error.Code);
        Assert.Equal(1, input.Reads);
    }

    /// <summary>Любой EOF после начала заголовка считается усечённым кадром, включая полностью отсутствующее тело.</summary>
    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 2 })]
    [InlineData(new byte[] { 0, 0, 0, 2, 65 })]
    public async Task RejectsTruncatedHeadersAndBodies(byte[] wire)
    {
        using var input = new FragmentedStream(wire, 1);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => FramedStream.ReadAsync(input));
        Assert.Equal("frame_truncated", error.Code);
    }

    /// <summary>Проверяет network byte order и отсутствие частичной записи при заранее известном недопустимом размере.</summary>
    [Fact]
    public async Task WritesNetworkOrderAndRejectsInvalidSizeBeforeWriting()
    {
        using var output = new MemoryStream();
        await FramedStream.WriteAsync(output, new byte[] { 65, 66 });
        Assert.Equal(new byte[] { 0, 0, 0, 2, 65, 66 }, output.ToArray());
        var previousLength = output.Length;
        await Assert.ThrowsAsync<ProtocolException>(() => FramedStream.WriteAsync(output, ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<ProtocolException>(() =>
            FramedStream.WriteAsync(output, new byte[FramedStream.MaxFrameBytes + 1]));
        Assert.Equal(previousLength, output.Length);
    }

    /// <summary>Переводит текст сценария в те же UTF-8 байты, которые получает настоящий протокольный кодек.</summary>
    private static InputBatch Decode(string json) => BatchJson.Decode(Encoding.UTF8.GetBytes(json));

    /// <summary>Отличает безопасный отказ схемы от необработанного исключения парсера или другого класса ошибки.</summary>
    private static void AssertSchema(string json) =>
        Assert.Equal("schema_invalid", Assert.Throws<ProtocolException>(() => Decode(json)).Code);

    /// <summary>Поток с короткими чтениями моделирует произвольное разбиение заголовка и тела транспортом.</summary>
    private sealed class FragmentedStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes)
    {
        /// <summary>Не возвращает больше fragmentSize байт, даже если вызывающий код запросил весь кадр.</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
    }

    /// <summary>Поток-сторож: после отдачи недопустимого заголовка любое чтение тела немедленно проваливает тест.</summary>
    private sealed class PrefixOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        /// <summary>Число попыток чтения; отказ по размеру обязан произойти после единственного чтения заголовка.</summary>
        public int Reads { get; private set; }

        /// <summary>Разрешает получить заголовок один раз и обнаруживает преждевременный запрос тела.</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++Reads > 1)
                throw new InvalidOperationException("The oversized frame must not read its body.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
