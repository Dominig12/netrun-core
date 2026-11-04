using Netrun.Core;
using Netrun.Core.Runtime;
using Netrun.Core.Sessions;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>
/// Регрессия: команды консоли-сессии проходят центральный RuntimeFingerprint и принимаются рантаймом.
/// Раньше для них не было ветки в Compute, поэтому каждый нырок через нейролинк давал
/// invalid_runtime_input.ConfigureConsoleSession и полный ресинк сессии.
/// </summary>
public sealed class ConsoleSessionRuntimeTests
{
    private static readonly Epochs Epochs = new("world", "engine", "binding");

    private static RuntimeInput Input(ulong sequence, RuntimeCommand command) => new(Epochs, sequence, 0, command);

    private static ConfigureConsoleSession Configure() =>
        new(new RequestKey("console", 1),
            new ConsoleSessionFact(new ConsoleSessionKey("console", 1), 1, "operator", "jack", "map"));

    /// <summary>Публикует минимальный снапшот и доводит рантайм до Ready.</summary>
    private static ulong Publish(NetworkRuntime runtime)
    {
        WorldChange[] facts = [new SectorUpsert(new("s", 1, 1, new([1]))),
            new NodeUpsert(new("node", 1, 1, new("node", "apc", new(1, 1, 1), "s", true, new("home", "home"))))];
        var chunk = new SnapshotChunk("s", 1, facts);
        runtime.Apply(Input(1, new ApplyWorld([new SnapshotBegin("s", 0, 2)])));
        runtime.Apply(Input(2, new ApplyWorld([chunk])));
        runtime.Apply(Input(3, new ApplyWorld([new SnapshotCommit("s", 1, (ulong)facts.Length, SnapshotChecksum.Compute([chunk], 2))])));
        runtime.Apply(Input(4, new ApplyWorld([new CatchUpComplete("s", 0)])));
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        return 5;
    }

    /// <summary>ConfigureConsoleSession фингерпринтится и принимается рантаймом, а не отвергается invalid_runtime_input.</summary>
    [Fact]
    public void ConfigureConsoleSessionIsAcceptedThroughTheRuntime()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var result = runtime.Apply(Input(sequence, Configure()));
        Assert.NotEqual(ApplyStatus.Rejected, result.Apply.Status);
        Assert.Equal(AdmissionStatus.Accepted, result.Admission!.Status);
    }

    /// <summary>Повтор той же команды на прежнем sequence — дубликат в replay-леджере, а не invalid: фингерпринт считается.</summary>
    [Fact]
    public void ConfigureConsoleSessionReplayIsDeduplicated()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var configure = Input(sequence, Configure());
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(configure).Admission!.Status);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(configure).Apply.Status);
    }
}
