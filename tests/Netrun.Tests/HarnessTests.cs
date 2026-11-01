using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Интеграционные проверки codec → mirror → результата Host.</summary>
public sealed class HarnessTests
{
    /// <summary>Replay воспроизводит удаление, повтор кадра и пересоздание нового поколения.</summary>
    [Fact]
    public async Task ReplayMatchesGoldenLifecycleOutput()
    {
        await using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mirror-lifecycle.jsonl"));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await Harness.ReplayAsync(input, output, errors));
        Assert.Equal("", errors.ToString());
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        var expected = new[] { ("applied", "1", 1), ("applied", "2", 0), ("duplicate", "2", 0), ("applied", "3", 1) };
        for (var i = 0; i < lines.Length; i++)
        {
            using var json = JsonDocument.Parse(lines[i]);
            var root = json.RootElement;
            Assert.Equal(expected[i].Item1, root.GetProperty("status").GetString());
            Assert.Equal(expected[i].Item2, root.GetProperty("applied_sequence").GetString());
            Assert.Equal(expected[i].Item3, root.GetProperty("node_count").GetInt32());
            Assert.Equal((i + 1).ToString(), root.GetProperty("output_sequence").GetString());
            Assert.Equal(1, root.GetProperty("tracked_count").GetInt32());
        }
    }

    /// <summary>ACK выставляется после применения, а не после чтения первого кадра из потока.</summary>
    [Fact]
    public async Task FramedHarnessStopsOnGapAndKeepsLastAppliedAck()
    {
        var epochs = new Epochs("world", "engine", "binding");
        using var input = new MemoryStream();
        await FramedStream.WriteAsync(input, BatchJson.Encode(new(epochs, 1, 0, [])));
        await FramedStream.WriteAsync(input, BatchJson.Encode(new(epochs, 3, 1, [])));
        input.Position = 0;
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        Assert.Equal(2, await Harness.FramedAsync(input, output, errors, epochs));
        output.Position = 0;
        using var first = JsonDocument.Parse((await FramedStream.ReadAsync(output))!);
        using var second = JsonDocument.Parse((await FramedStream.ReadAsync(output))!);
        Assert.Equal("applied", first.RootElement.GetProperty("status").GetString());
        Assert.Equal("resync_required", second.RootElement.GetProperty("status").GetString());
        Assert.Equal("1", second.RootElement.GetProperty("applied_sequence").GetString());
        Assert.Null(await FramedStream.ReadAsync(output));
    }

    /// <summary>Стенд ограничивает строку до JSON parser и не раскрывает её содержимое.</summary>
    [Fact]
    public async Task OversizedJsonLineProducesOnlySafeErrorCode()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('s', FramedStream.MaxFrameBytes + 2)));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(2, await Harness.ReplayAsync(input, output, errors));
        Assert.Equal("frame_size" + Environment.NewLine, errors.ToString());
        Assert.Equal("", output.ToString());
    }

    /// <summary>CRLF и отсутствие завершающего LF не меняют смысл реплея.</summary>
    [Fact]
    public async Task ReplaySupportsCrLfAndFinalLineWithoutNewline()
    {
        var batch = new InputBatch(new("world", "engine", "binding"), 1, 0, []);
        var json = Encoding.UTF8.GetString(BatchJson.Encode(batch));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json + "\r\n" + json));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await Harness.ReplayAsync(input, output, errors));
        Assert.Contains("duplicate", output.ToString());
    }

    /// <summary>Пустой replay не считается успешной проверкой фактов.</summary>
    [Fact]
    public async Task EmptyReplayFailsExplicitly()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(2, await Harness.ReplayAsync(Stream.Null, output, errors));
        Assert.Equal("empty_replay" + Environment.NewLine, errors.ToString());
    }
}
