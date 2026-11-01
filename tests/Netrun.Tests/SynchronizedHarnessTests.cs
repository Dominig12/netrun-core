using System.Text;
using System.Text.Json;
using Netrun.Core;
using Netrun.Host;
using Netrun.Protocol;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет сквозной snapshot pipeline и запрет успешного EOF посреди синхронизации.</summary>
public sealed class SynchronizedHarnessTests
{
    /// <summary>Независимые epochs автономного framing-прогона.</summary>
    private static readonly Epochs Epochs = new("world", "engine", "binding");

    /// <summary>Fixture подтверждает wire checksum, duplicate chunk и публикацию только после catch-up.</summary>
    [Fact]
    public async Task ChurnFixturePublishesOnlyAfterCatchUpBarrier()
    {
        await using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot-churn.jsonl"));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await Harness.ReplaySynchronizedAsync(input, output, errors));
        Assert.Equal("", errors.ToString());
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, lines.Length);
        var states = new[] { "snapshotting", "snapshotting", "snapshotting", "catching_up", "catching_up", "ready", "ready" };
        for (var i = 0; i < lines.Length; i++)
        {
            using var json = JsonDocument.Parse(lines[i]);
            Assert.Equal(states[i], json.RootElement.GetProperty("sync_state").GetString());
            Assert.Equal(i < 5 ? 0 : 1, json.RootElement.GetProperty("node_count").GetInt32());
            Assert.Equal(i == 2 ? "duplicate" : "applied", json.RootElement.GetProperty("status").GetString());
        }
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("6", last.RootElement.GetProperty("applied_sequence").GetString());
        Assert.Equal(2, last.RootElement.GetProperty("tracked_count").GetInt32());
    }

    /// <summary>Корректно прочитанный begin не равен готовому snapshot даже при чистом EOF.</summary>
    [Fact]
    public async Task JsonReplayRejectsIncompleteSnapshotAtCleanEof()
    {
        var body = BatchJson.Encode(new(Epochs, 1, 0, [new SnapshotBegin("snapshot", 0)]));
        using var input = new MemoryStream(body);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(2, await Harness.ReplaySynchronizedAsync(input, output, errors));
        Assert.Equal("snapshot_incomplete" + Environment.NewLine, errors.ToString());
        Assert.Contains("snapshotting", output.ToString());
    }

    /// <summary>Бинарный стенд применяет тот же completion gate, что и JSONL replay.</summary>
    [Fact]
    public async Task FramedReplayRejectsIncompleteSnapshotAtCleanEof()
    {
        using var input = new MemoryStream();
        await FramedStream.WriteAsync(input, BatchJson.Encode(new(Epochs, 1, 0, [new SnapshotBegin("snapshot", 0)])));
        input.Position = 0;
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        Assert.Equal(2, await Harness.FramedSynchronizedAsync(input, output, errors, Epochs));
        Assert.Equal("snapshot_incomplete" + Environment.NewLine, errors.ToString());
    }

    /// <summary>Пустой мир синхронизируется без chunks, но всё равно требует commit и catch-up barrier.</summary>
    [Fact]
    public async Task FramedEmptySnapshotCompletesSuccessfully()
    {
        InputBatch[] batches =
        [
            new(Epochs, 1, 0, [new SnapshotBegin("snapshot", 0)]),
            new(Epochs, 2, 0, [new SnapshotCommit("snapshot", 0, 0, SnapshotChecksum.Compute([]))]),
            new(Epochs, 3, 0, [new CatchUpComplete("snapshot", 0)])
        ];
        using var input = new MemoryStream();
        foreach (var batch in batches)
            await FramedStream.WriteAsync(input, BatchJson.Encode(batch));
        input.Position = 0;
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        Assert.Equal(0, await Harness.FramedSynchronizedAsync(input, output, errors, Epochs));
        Assert.Equal("", errors.ToString());
        output.Position = 0;
        await FramedStream.ReadAsync(output);
        await FramedStream.ReadAsync(output);
        using var last = JsonDocument.Parse((await FramedStream.ReadAsync(output))!);
        Assert.Equal("ready", last.RootElement.GetProperty("sync_state").GetString());
    }

    /// <summary>Обязательный snapshot не обходится обычным delta или legacy-режимом на sync-входе.</summary>
    [Fact]
    public async Task HarnessModesDoNotSilentlyFallBackAcrossContracts()
    {
        using var input = new MemoryStream(BatchJson.Encode(new(Epochs, 1, 0, [])));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(2, await Harness.ReplaySynchronizedAsync(input, output, errors));
        Assert.Contains("not_ready", output.ToString());
        using var legacy = new MemoryStream(BatchJson.Encode(new(Epochs, 1, 0, [new SnapshotBegin("snapshot", 0)])));
        output.GetStringBuilder().Clear();
        Assert.Equal(2, await Harness.ReplayAsync(legacy, output, errors));
        Assert.Contains("unsupported_change", output.ToString());
    }
}
