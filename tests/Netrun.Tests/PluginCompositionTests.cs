using Netrun.Core;
using Netrun.Core.Plugins;
using Netrun.Core.Runtime;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет закрытие composition и атомарное прохождение custom facts через настоящий runtime.</summary>
public sealed class PluginCompositionTests
{
    private static readonly Epochs Epochs = new("world", "engine", "binding");
    private sealed record CustomNode(NodeFact Node) : WorldChange;
    private sealed class Handler : NodeChangeHandler<CustomNode>
    {
        protected override NodeMutation Normalize(CustomNode change) => new(change.Node.NodeId,
            change.Node.Generation, change.Node.Revision, change.Node);
    }
    private sealed class Plugin : INetrunPlugin
    {
        internal INetrunPluginRegistry? Captured;
        public PluginMetadata Metadata => new("example.nodes", "Nodes", "1");
        public void Configure(INetrunPluginRegistry registry)
        { Captured = registry; registry.AddNodeChangeHandler(new Handler()); }
    }
    private sealed class IncompatiblePlugin : INetrunPlugin
    {
        public int ApiVersion => NetrunPluginApi.Version + 1;
        public PluginMetadata Metadata => new("example.future", "Future", "1");
        public void Configure(INetrunPluginRegistry registry) => throw new InvalidOperationException("Must not configure incompatible API.");
    }
    private static NodeFact Node(ulong revision = 1) => new("n", 1, revision, new("n", "server", new(1, 1, 1), "s", true));

    [Fact]
    public void RetainedRegistryCannotBeChangedAfterConfigure()
    {
        var plugin = new Plugin();
        using var runtime = new NetworkRuntime(Epochs, new(Plugins: [plugin]));
        Assert.Equal("plugin_registry_closed", Assert.Throws<PluginException>(() => plugin.Captured!.AddNodeChangeHandler(new Handler())).Code);
    }

    [Fact]
    public void ApiMismatchIsRejectedBeforeConfigure()
    {
        Assert.Equal("plugin_api_incompatible", Assert.Throws<PluginException>(() =>
            new NetworkRuntime(Epochs, new(Plugins: [new IncompatiblePlugin()]))).Code);
    }

    [Fact]
    public void CustomSnapshotAndDeltasShareCanonicalReplayAndAtomicity()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Plugins: [new Plugin()]));
        var canonical = new SnapshotChunk("s", 1, [new NodeUpsert(Node())]);
        RuntimeInput Input(ulong sequence, WorldChange change) => new(Epochs, sequence, 0, new ApplyWorld([change]));
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(Input(1, new SnapshotBegin("s", 0, 2))).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(Input(2, new SnapshotChunk("s", 1, [new CustomNode(Node())]))).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(Input(3, new SnapshotCommit("s", 1, 1, SnapshotChecksum.Compute([canonical], 2)))).Apply.Status);
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(Input(4, new CatchUpComplete("s", 0))).Apply.Status);
        Assert.Equal(Node(), runtime.Nodes["n"]);
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(Input(5, new CustomNode(Node(2)))).Apply.Status);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(Input(5, new NodeUpsert(Node(2)))).Apply.Status);
        var invalid = new RuntimeInput(Epochs, 6, 0, new ApplyWorld([new CustomNode(Node(3)), new CustomNode(Node(0))]));
        Assert.Equal(ApplyStatus.Rejected, runtime.Apply(invalid).Apply.Status);
        Assert.Equal(2UL, runtime.Nodes["n"].Revision);
        Assert.Equal(5UL, runtime.AppliedSequence);
    }
}
