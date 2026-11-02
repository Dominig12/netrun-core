using Netrun.Core;
using Netrun.Core.Routing;
using Netrun.Core.Runtime;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет causal composition: world publication, graph admission, command replay и native lifecycle.</summary>
public sealed class RuntimeTests
{
    /// <summary>Каждый тест получает независимого владельца одного и того же синтетического binding.</summary>
    private static readonly Epochs Epochs = new("world", "engine", "binding");

    /// <summary>Полный node-факт с native placement для настоящего topology builder.</summary>
    private static NodeFact Node(string id, int x, string type = "server", ulong revision = 1, bool online = true) =>
        new(id, 1, revision, new(id, type, new(x, 1, 1), "s", online, new("home", "home")));

    /// <summary>Реальный allowlisted service contract, физический owner которого остаётся у mirror.</summary>
    private static ServiceFact Service(string id, string owner, ulong revision = 1, string tag = "tag") =>
        new(id, 1, revision, new(owner, 1), "test.status", tag, true,
            new ValueList<OperationContract>([new("read", OperationSafety.ReadOnly)]));

    /// <summary>Полный исходный world snapshot: sector, три физических узла и два service instances.</summary>
    private static WorldChange[] Facts() =>
    [
        new SectorUpsert(new("s", 1, 1, new ValueList<int>([1]))),
        new NodeUpsert(Node("apc", 1, "apc")), new NodeUpsert(Node("a", 2)), new NodeUpsert(Node("b", 3)),
        new ServiceUpsert(Service("source", "a")), new ServiceUpsert(Service("destination", "b"))
    ];

    /// <summary>Создаёт bounded request; его admission не является подтверждением native effect.</summary>
    private static NetworkRequest Request(ulong sequence = 1, NetworkValue? payload = null, int? deadline = null) =>
        new(new("producer", sequence), new("source", 1), new("destination", 1), "read", payload ?? NetworkValue.Null, deadline);

    /// <summary>Фиксирует полный логический frame; время по умолчанию не продвигается между командами.</summary>
    private static RuntimeInput Input(ulong sequence, RuntimeCommand command, int time = 0) => new(Epochs, sequence, time, command);

    /// <summary>Публикует extended snapshot через четыре настоящих sync frames и возвращает первый свободный sequence.</summary>
    private static ulong Publish(NetworkRuntime runtime)
    {
        var chunk = new SnapshotChunk("snapshot", 1, Facts());
        RuntimeInput[] inputs =
        [
            Input(1, new ApplyWorld([new SnapshotBegin("snapshot", 0, 2)])),
            Input(2, new ApplyWorld([chunk])),
            Input(3, new ApplyWorld([new SnapshotCommit("snapshot", 1, (ulong)chunk.Changes.Count, SnapshotChecksum.Compute([chunk], 2))])),
            Input(4, new ApplyWorld([new CatchUpComplete("snapshot", 0)]))
        ];
        foreach (var input in inputs)
            Assert.Equal(ApplyStatus.Applied, runtime.Apply(input).Apply.Status);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        return 5;
    }

    /// <summary>Route command запрещён до snapshot; после publication использует настоящий derived graph.</summary>
    [Fact]
    public void SnapshotReadinessPrecedesCommandsAndGraphRoutes()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var before = runtime.Apply(Input(1, new QueryRoute(new("a", 1), new("b", 1))));
        Assert.Equal(ApplyStatus.Rejected, before.Apply.Status);
        Assert.Equal("not_ready", before.Apply.Code);
        Assert.Equal(0UL, runtime.AppliedSequence);
        var sequence = Publish(runtime);
        var result = runtime.Apply(Input(sequence, new QueryRoute(new("a", 1), new("b", 1))));
        Assert.Equal(RouteStatus.Found, result.Route!.Status);
        Assert.Equal(2, result.Route.LatencyDs);
        Assert.Equal(1UL, result.GraphVersion);
        Assert.False(result.TopologyPending);
        Assert.Equal(3, runtime.Nodes.Count);
        Assert.Equal(2, runtime.Services.Count);
        Assert.Single(runtime.Sectors);
    }

    /// <summary>Availability-only вход не перестраивает граф, но native handoff получает актуальные raw revisions.</summary>
    [Fact]
    public void InvocationUsesCurrentRawNodeRevisionsInsteadOfStructuralGraphCopies()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence++, new SubmitRequest(Request()))).Admission!.Status);
        runtime.Apply(Input(sequence, new ApplyWorld([new NodeUpsert(Node("a", 2, revision: 7)), new NodeUpsert(Node("b", 3, revision: 9))])));
        Assert.Equal(1UL, runtime.Graph!.Version);
        Assert.Equal(1UL, runtime.Graph.Nodes[new("a", 1)].Revision);
        runtime.Pump();
        Assert.True(runtime.TryDequeueEvent(out var value));
        var invocation = Assert.IsType<ServiceInvocation>(value);
        Assert.Equal(7UL, invocation.SourceNodeRevision);
        Assert.Equal(9UL, invocation.DestinationNodeRevision);
    }

    /// <summary>Runtime replay не выполняет Submit дважды, а подмена типа команды закрывает весь binding.</summary>
    [Fact]
    public void GlobalCommandReplayCannotBeBypassedThroughEmptySyncTicks()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var submit = Input(sequence, new SubmitRequest(Request()));
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(submit).Admission!.Status);
        var duplicate = runtime.Apply(submit);
        Assert.Equal(ApplyStatus.Duplicate, duplicate.Apply.Status);
        Assert.Equal(AdmissionStatus.Accepted, duplicate.Admission!.Status);
        Assert.Equal(1, runtime.ActiveRequestCount);
        var conflict = runtime.Apply(Input(sequence, new ApplyWorld([])));
        Assert.Equal(ApplyStatus.ResyncRequired, conflict.Apply.Status);
        Assert.Equal(sequence, conflict.Apply.AppliedSequence);
        Assert.Null(runtime.Graph);
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(Input(sequence + 1, new ApplyWorld([]))).Apply.Status);
        runtime.Pump();
        while (runtime.TryDequeueEvent(out var value))
            Assert.IsNotType<ServiceInvocation>(value);
    }

    /// <summary>Порядок object properties и знаки числового нуля не меняют canonical request fingerprint.</summary>
    [Fact]
    public void CanonicalPayloadFingerprintUsesStructureRatherThanGetHashCode()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var first = NetworkValue.FromObject(new Dictionary<string, NetworkValue>
        {
            ["x"] = NetworkValue.FromNumber(0),
            ["y"] = NetworkValue.FromArray([NetworkValue.Null])
        });
        var second = NetworkValue.FromObject(new Dictionary<string, NetworkValue>
        {
            ["y"] = NetworkValue.FromArray([NetworkValue.Null]),
            ["x"] = NetworkValue.FromNumber(-0d)
        });
        Assert.Equal(ApplyStatus.Applied, runtime.Apply(Input(sequence, new SubmitRequest(Request(payload: first)))).Apply.Status);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(Input(sequence, new SubmitRequest(Request(payload: second)))).Apply.Status);
        var changed = NetworkValue.FromArray([NetworkValue.Null]);
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(Input(sequence, new SubmitRequest(Request(payload: changed)))).Apply.Status);
    }

    /// <summary>Power-only delta обновляет overlay и cache version без перестройки физического graph.</summary>
    [Fact]
    public void AvailabilityOnlyUpdatesKeepGraphAndChangeRouteAdmission()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var graph = runtime.Graph;
        runtime.Apply(Input(sequence++, new ApplyWorld([new NodeUpsert(Node("b", 3, revision: 2, online: false))])));
        Assert.Same(graph, runtime.Graph);
        Assert.False(runtime.TopologyPending);
        Assert.Equal(RouteStatus.Unreachable, runtime.Apply(Input(sequence++, new QueryRoute(new("a", 1), new("b", 1)))).Route!.Status);
        Assert.Equal(RouteStatus.Found, runtime.Apply(Input(sequence++, new QueryRoute(new("a", 1), new("b", 1), RouteKind.Discovery))).Route!.Status);
        runtime.Apply(Input(sequence++, new ApplyWorld([new NodeUpsert(Node("b", 3, revision: 3))])));
        Assert.Same(graph, runtime.Graph);
        Assert.Equal(RouteStatus.Found, runtime.Apply(Input(sequence, new QueryRoute(new("a", 1), new("b", 1)))).Route!.Status);
    }

    /// <summary>Service-only изменения не затрагивают graph identity и одновременно меняют текущий registry.</summary>
    [Fact]
    public void ServiceUpdatesDoNotRebuildTopology()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var graph = runtime.Graph;
        var result = runtime.Apply(Input(sequence, new ApplyWorld([new ServiceUpsert(Service("destination", "b", 2, "changed"))])));
        Assert.Equal(ApplyStatus.Applied, result.Apply.Status);
        Assert.Same(graph, runtime.Graph);
        Assert.Equal("changed", runtime.Services["destination"].Tag);
    }

    /// <summary>Одна catalog revision без изменения ordered levels не заставляет заново индексировать всю станцию.</summary>
    [Fact]
    public void SectorRevisionWithoutGeometryChangeKeepsGraph()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var graph = runtime.Graph;
        var result = runtime.Apply(Input(sequence, new ApplyWorld([
            new SectorUpsert(new("s", 1, 2, new ValueList<int>([1])))])));
        Assert.Equal(ApplyStatus.Applied, result.Apply.Status);
        Assert.Same(graph, runtime.Graph);
        Assert.False(result.TopologyPending);
        Assert.Equal(2UL, runtime.Sectors["s"].Revision);
    }

    /// <summary>Deferred structural inputs объединяются в одну новую публикацию; pending route не использует старый graph.</summary>
    [Fact]
    public void DeferredTopologyCoalescesWorldChangesAndClosesOldRoutes()
    {
        using var runtime = new NetworkRuntime(Epochs, new(DeferTopologyBuild: true));
        var sequence = Publish(runtime);
        Assert.Null(runtime.Graph);
        runtime.Pump(0);
        var first = runtime.Graph;
        Assert.Equal(1UL, first!.Version);
        runtime.Apply(Input(sequence++, new ApplyWorld([new NodeUpsert(Node("b", 20, revision: 2))])));
        Assert.True(runtime.TopologyPending);
        Assert.Null(runtime.Graph);
        var pending = runtime.Apply(Input(sequence++, new QueryRoute(new("a", 1), new("b", 1))));
        Assert.Equal("topology_pending", pending.ErrorCode);
        Assert.Null(pending.Route);
        runtime.Apply(Input(sequence++, new ApplyWorld([new NodeUpsert(Node("b", 40, revision: 3))])));
        runtime.Pump(0);
        Assert.Equal(2UL, runtime.Graph!.Version);
        Assert.Equal(sequence - 1, runtime.Graph.InputSequence);
        Assert.Equal(3UL, runtime.Nodes["b"].Revision);
        Assert.Equal(1UL, first.Version);
    }

    /// <summary>Сбой Build не откатывает принятые факты, но исключает старый graph до исправления исходных sector facts.</summary>
    [Fact]
    public void FailedTopologyBuildDoesNotLeakPreviousPublication()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var result = runtime.Apply(Input(sequence++, new ApplyWorld([new SectorRemove("s", 1, 2)])));
        Assert.Equal(ApplyStatus.Applied, result.Apply.Status);
        Assert.Equal("topology_missing_sector", result.ErrorCode);
        Assert.True(runtime.TopologyPending);
        Assert.Null(runtime.Graph);
        runtime.Pump(0);
        Assert.Null(runtime.Graph);
        Assert.Equal("topology_pending", runtime.Apply(Input(sequence++, new QueryRoute(new("a", 1), new("b", 1)))).ErrorCode);
        runtime.Apply(Input(sequence, new ApplyWorld([new SectorUpsert(new("s", 2, 1, new ValueList<int>([1])))])));
        Assert.False(runtime.TopologyPending);
        Assert.Equal(2UL, runtime.Graph!.Version);
    }

    /// <summary>Expected live revision conflict не потребляет sequence и допускает исправленный frame без нового snapshot.</summary>
    [Fact]
    public void RejectedLiveWorldFrameCanBeCorrectedAtTheSameSequence()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        var rejected = runtime.Apply(Input(sequence, new ApplyWorld([
            new NodeUpsert(Node("a", 20, revision: 2)), new NodeUpsert(Node("b", 3, online: false))])));
        Assert.Equal(ApplyStatus.Rejected, rejected.Apply.Status);
        Assert.Equal("revision_conflict", rejected.Apply.Code);
        Assert.Equal(sequence - 1, runtime.AppliedSequence);
        Assert.Equal(MirrorSyncState.Ready, runtime.State);
        Assert.Equal(2, runtime.Nodes["a"].State.Position!.X);
        var corrected = runtime.Apply(Input(sequence, new ApplyWorld([new NodeUpsert(Node("a", 20, revision: 2))])));
        Assert.Equal(ApplyStatus.Applied, corrected.Apply.Status);
        Assert.Equal(sequence, runtime.AppliedSequence);
    }

    /// <summary>Outer gap уничтожает staging независимо от корректной внутренней формы snapshot chunk.</summary>
    [Fact]
    public void GlobalSequenceGapClosesSnapshotAndPreservesAck()
    {
        using var runtime = new NetworkRuntime(Epochs);
        runtime.Apply(Input(1, new ApplyWorld([new SnapshotBegin("s", 0)])));
        var result = runtime.Apply(Input(3, new ApplyWorld([new SnapshotChunk("s", 1, [])])));
        Assert.Equal(ApplyStatus.ResyncRequired, result.Apply.Status);
        Assert.Equal("sequence_gap", result.Apply.Code);
        Assert.Equal(1UL, result.Apply.AppliedSequence);
        Assert.Equal(MirrorSyncState.ResyncRequired, runtime.State);
        Assert.Empty(runtime.Nodes);
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(Input(2, new ApplyWorld([new SnapshotChunk("s", 1, [])]))).Apply.Status);
    }

    /// <summary>Вытеснённый общий replay не воспринимается как новый command или новый sync input.</summary>
    [Fact]
    public void ExpiredGlobalReplayRequiresNewBinding()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Core: new(ReplayWindow: 1)));
        var sequence = Publish(runtime);
        var query = Input(sequence, new QueryRoute(new("a", 1), new("b", 1)));
        runtime.Apply(query);
        runtime.Apply(Input(sequence + 1, new ConfigureBackbone(false)));
        Assert.Equal(ApplyStatus.ResyncRequired, runtime.Apply(query).Apply.Status);
    }

    /// <summary>Pump не создаёт wall-time прогресс; due/timeout наступает только после подтверждённого логического input времени.</summary>
    [Fact]
    public void PumpUsesOnlyConfirmedLogicalTime()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Transport: new(TickLagDs: 0.001)));
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new SubmitRequest(Request())));
        for (var i = 0; i < 10; i++)
            Assert.Equal(0, runtime.Pump());
        Assert.False(runtime.TryDequeueEvent(out _));
        Assert.Equal(0, runtime.SimulationTimeDs);
        runtime.Apply(Input(sequence, new ApplyWorld([]), time: 1));
        Assert.Equal(1, runtime.Pump());
        Assert.True(runtime.TryDequeueEvent(out var invocation));
        Assert.IsType<ServiceInvocation>(invocation);
        Assert.Equal(1, runtime.SimulationTimeDs);
    }

    /// <summary>Deadline до invocation учитывает ожидание между command и первым вызовом Pump.</summary>
    [Fact]
    public void DeadlineUsesTimeAdvancedByWorldFrames()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new SubmitRequest(Request(deadline: 1))));
        runtime.Apply(Input(sequence, new ApplyWorld([]), time: 2));
        runtime.Pump();
        Assert.True(runtime.TryDequeueEvent(out var value));
        Assert.Equal(RequestState.TimedOut, Assert.IsType<RequestCompleted>(value).State);
        Assert.False(runtime.TryDequeueEvent(out _));
    }

    /// <summary>Would-block относится к domain admission; frame ACK продвигается, retry domain key идёт новым frame.</summary>
    [Fact]
    public void DomainWouldBlockConsumesFrameButNotRequestKey()
    {
        using var runtime = new NetworkRuntime(Epochs, new(Transport: new(MaxActive: 1)));
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new SubmitRequest(Request())));
        var blockedFrame = Input(sequence++, new SubmitRequest(Request(2)));
        var blocked = runtime.Apply(blockedFrame);
        Assert.Equal(ApplyStatus.Applied, blocked.Apply.Status);
        Assert.Equal(AdmissionStatus.WouldBlock, blocked.Admission!.Status);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(blockedFrame).Apply.Status);
        Assert.Equal(1, runtime.ActiveRequestCount);
        Assert.True(runtime.Apply(Input(sequence++, new CancelRequest(Request().Key))).Action!.Accepted);
        Assert.True(runtime.TryDequeueEvent(out _));
        Assert.Equal(AdmissionStatus.Accepted, runtime.Apply(Input(sequence, new SubmitRequest(Request(2)))).Admission!.Status);
    }

    /// <summary>Native result применяется ровно один раз, а replay возвращает metadata без второго terminal event.</summary>
    [Fact]
    public void InvocationLifecycleIsCorrelatedAcrossGlobalCommandFrames()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new SubmitRequest(Request())));
        runtime.Pump();
        Assert.True(runtime.TryDequeueEvent(out var output));
        var invocation = Assert.IsType<ServiceInvocation>(output);
        var result = Input(sequence, new FinishInvocation(new(Epochs.Binding, invocation.InvocationId, true, NetworkValue.FromArray([]))));
        Assert.True(runtime.Apply(result).Action!.Accepted);
        Assert.Equal(ApplyStatus.Duplicate, runtime.Apply(result).Apply.Status);
        Assert.True(runtime.TryDequeueEvent(out var terminal));
        Assert.Equal(RequestState.Completed, Assert.IsType<RequestCompleted>(terminal).State);
        Assert.False(runtime.TryDequeueEvent(out _));
        Assert.Equal(0, runtime.ActiveRequestCount);
    }

    /// <summary>Отзыв service между due и handoff не выпускает уже подготовленный invocation наружу.</summary>
    [Fact]
    public void WorldRevocationBeforeHandoffRetractsQueuedInvocation()
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence++, new SubmitRequest(Request())));
        runtime.Pump();
        runtime.Apply(Input(sequence, new ApplyWorld([new ServiceRemove("destination", 1, 2)])));
        Assert.True(runtime.TryDequeueEvent(out var value));
        Assert.Equal(RequestState.Failed, Assert.IsType<RequestCompleted>(value).State);
        Assert.False(runtime.TryDequeueEvent(out _));
    }

    /// <summary>Disconnect различает invocation, уже отданный native миру, и очередь, которая ещё не могла вызвать эффект.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisconnectionClassifiesNativeUncertaintyAtHandoff(bool handedOff)
    {
        using var runtime = new NetworkRuntime(Epochs);
        var sequence = Publish(runtime);
        runtime.Apply(Input(sequence, new SubmitRequest(Request())));
        runtime.Pump();
        if (handedOff)
        {
            Assert.True(runtime.TryDequeueEvent(out var invocation));
            Assert.IsType<ServiceInvocation>(invocation);
        }
        runtime.MarkDisconnected();
        Assert.Equal(MirrorSyncState.ResyncRequired, runtime.State);
        Assert.Null(runtime.Graph);
        runtime.Pump();
        Assert.True(runtime.TryDequeueEvent(out var value));
        Assert.Equal(handedOff ? RequestState.OutcomeUnknown : RequestState.Failed, Assert.IsType<RequestCompleted>(value).State);
    }

    /// <summary>Dispose завершает lifetime и сохраняет опубликованные world facts только для безопасной диагностики.</summary>
    [Fact]
    public void DisposalClosesProcessingWithoutDiscardingPublishedDiagnostics()
    {
        var runtime = new NetworkRuntime(Epochs);
        Publish(runtime);
        runtime.Dispose();
        runtime.Dispose();
        Assert.Equal(MirrorSyncState.ResyncRequired, runtime.State);
        Assert.Equal(3, runtime.Nodes.Count);
        Assert.Throws<ObjectDisposedException>(() => runtime.Apply(Input(5, new ApplyWorld([]))));
        Assert.Throws<ObjectDisposedException>(() => runtime.Pump());
    }

    /// <summary>Публичная factory позволяет внешнему provider создавать immutable результат и прекращает бесконечный enumerable по лимиту.</summary>
    [Fact]
    public void ExternalRouteResultFactoryValidatesAndBoundsOwnedPath()
    {
        var result = RouteResult.Create(RouteStatus.Found, [new NodeKey("a", 1)], 0, 1, 2);
        Assert.Throws<NotSupportedException>(() => ((IList<NodeKey>)result.Path).Clear());
        Assert.Throws<ArgumentException>(() => RouteResult.Create(RouteStatus.Found, [], 0, 1, 1));
        Assert.Throws<ArgumentException>(() => RouteResult.Create(RouteStatus.Unreachable, [], double.NaN, 1, 1));
        Assert.Throws<ArgumentException>(() => RouteResult.Create(RouteStatus.Found, [new NodeKey("bad\ud800", 1)], 0, 1, 1));
        var consumed = 0;
        Assert.Throws<ArgumentException>(() => RouteResult.Create(RouteStatus.Found, Endless(), 1, 1, 1, maxPathLength: 2));
        Assert.Equal(3, consumed);

        // Если factory сначала материализует enumerable целиком, этот сценарий не завершится.
        IEnumerable<NodeKey> Endless()
        {
            while (true)
            {
                consumed++;
                yield return new NodeKey("a", 1);
            }
        }
    }
}
