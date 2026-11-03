using Netrun.Core;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет transport lifecycle отдельно от route solver и native-эффектов.</summary>
public sealed class TransportTests
{
    /// <summary>Изменяемый fake имитирует atomically опубликованные факты между transport-вызовами.</summary>
    private sealed class World : INetworkWorldView
    {
        /// <summary>Узлы current generation без скрытой привязки к сетевому адресу.</summary>
        internal Dictionary<string, NodeFact> MutableNodes { get; } = [];
        /// <summary>Текущие сервисы позволяют отзывать tag/owner/generation во время полёта.</summary>
        internal Dictionary<string, ServiceFact> MutableServices { get; } = [];
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => MutableNodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors { get; } = new Dictionary<string, SectorFact>();
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services => MutableServices;
    }

    /// <summary>Независимый route oracle записывает направление и версию графа каждого обращения.</summary>
    private sealed class Routes : IRouteProvider
    {
        /// <summary>Изменяемая достижимость проверяет повторный solve вместо фиксации admission path.</summary>
        internal bool Reachable = true;
        /// <summary>Стоимость до machine-scale, в децисекундах.</summary>
        internal double Latency;
        /// <summary>Наблюдаемые route calls без удержания payload.</summary>
        internal List<(NodeKey Source, NodeKey Destination, ulong Version)> Calls { get; } = [];
        /// <inheritdoc />
        public RouteResult FindRoute(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine)
        {
            Calls.Add((source, destination, graph.Version));
            return new(Reachable ? RouteStatus.Found : RouteStatus.Unreachable,
                Reachable ? [source, destination] : [], Reachable ? Latency : 0, graph.Version, 0);
        }
    }

    /// <summary>Объединяет только тестовые зависимости; production transport получает их по интерфейсам.</summary>
    private sealed class Scene
    {
        /// <summary>Живой fake world.</summary>
        internal World World { get; } = new();
        /// <summary>Управляемый route oracle.</summary>
        internal Routes Routes { get; } = new();
        /// <summary>Null имитирует dirty topology между фактом и публикацией.</summary>
        internal GraphSnapshot? Graph;
        /// <summary>Проверяемый последовательный transport.</summary>
        internal NetworkTransport Transport { get; }
        /// <summary>Создаёт две точные service/node identities и отдельную immutable graph публикацию.</summary>
        internal Scene(TransportOptions? options = null, DeliverySemantics delivery = DeliverySemantics.DeliveryOnly,
            bool ordered = false, OperationSafety safety = OperationSafety.ReadOnly)
        {
            World.MutableNodes["source-node"] = new("source-node", 1, 1, new("A", "server", new(1, 1, 1), "s", true));
            World.MutableNodes["target-node"] = new("target-node", 1, 1, new("B", "server", new(2, 1, 1), "s", true));
            var operations = new ValueList<OperationContract>([new("send", safety, delivery, ordered)], 64);
            World.MutableServices["source"] = new("source", 1, 1, new("source-node", 1), "test.source", "source-tag", true, operations);
            World.MutableServices["target"] = new("target", 1, 1, new("target-node", 1), "test.target", "target-tag", true, operations);
            Graph = MakeGraph(1);
            Transport = new NetworkTransport(World, () => Graph, Routes, "binding", options);
        }
        /// <summary>Создаёт структурную публикацию с новой версией; oracle задаёт путь независимо от кабелей.</summary>
        internal GraphSnapshot MakeGraph(ulong version) => new(version, version,
            World.Nodes.Values.ToDictionary(node => new NodeKey(node.NodeId, node.Generation)),
            World.Nodes.Values.ToDictionary(node => node.State.Address, node => new NodeKey(node.NodeId, node.Generation)), [],
            World.Nodes.Values.ToDictionary(node => new NodeKey(node.NodeId, node.Generation), _ => new List<PhysicalEdge>()), [], [], [], 3);
    }

    /// <summary>Стандартный запрос не coalesce и не получает неявного deadline.</summary>
    private static NetworkRequest Request(ulong sequence = 1, string producer = "producer") =>
        new(new(producer, sequence), new("source", 1), new("target", 1), "send", NetworkValue.FromNumber(sequence));

    /// <summary>Забирает готовый exact event без предположения, что admission исполняет native handler.</summary>
    private static T Take<T>(NetworkTransport transport) where T : TransportEvent
    {
        Assert.True(transport.TryDequeueEvent(out var item));
        return Assert.IsType<T>(item);
    }

    /// <summary>Формирует известный ответ выданного invocation, не вызывая какие-либо игровые эффекты.</summary>
    private static InvocationResult Result(ServiceInvocation invocation, NetworkValue? payload = null) =>
        new(invocation.BindingEpoch, invocation.InvocationId, true, payload);

    /// <summary>Поздний pipelined serial не закрывает watermark запроса, который ещё ждёт admission capacity.</summary>
    [Fact]
    public void PipelinedAdmissionFenceSurvivesCapacityReleaseAndKeepsProducerRegistryBounded()
    {
        var scene = new Scene(new(MaxActive: 1, MaxProducers: 2));
        scene.Transport.Submit(Request(producer: "busy"), 0);
        Assert.Equal(AdmissionStatus.WouldBlock, scene.Transport.Submit(Request(), 0).Status);
        Assert.Equal(2, scene.Transport.ProducerCount);
        Assert.Equal("producer_capacity", scene.Transport.Submit(Request(producer: "third"), 0).Code);
        scene.Transport.Advance(0, 1);
        var busy = Take<ServiceInvocation>(scene.Transport);
        Assert.True(scene.Transport.CompleteInvocation(Result(busy), 0).Accepted);
        _ = Take<RequestCompleted>(scene.Transport);
        Assert.Equal("producer_backpressure", scene.Transport.Submit(Request(2), 0).Code);
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(Request(), 0).Status);
        Assert.True(scene.Transport.Cancel(Request().Key, 0).Accepted);
        _ = Take<RequestCompleted>(scene.Transport);
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(Request(2), 0).Status);
    }

    /// <summary>Coalescing также не обходит blocked head; отмена непринятого запроса освобождает producer.</summary>
    [Fact]
    public void PipelinedCoalescingWaitsForHeadCancellation()
    {
        var scene = new Scene(new(MaxActive: 1), safety: OperationSafety.Idempotent);
        var state = Request() with { CoalescingKey = "state" };
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(state, 0).Status);
        Assert.Equal(AdmissionStatus.WouldBlock, scene.Transport.Submit(Request(2), 0).Status);
        var later = Request(3) with { CoalescingKey = "state" };
        Assert.Equal("producer_backpressure", scene.Transport.Submit(later, 0).Code);
        Assert.Equal("request_unknown", scene.Transport.Cancel(Request(2).Key, 0).Code);
        Assert.Equal(AdmissionStatus.Coalesced, scene.Transport.Submit(later, 0).Status);
        Assert.Equal(RequestState.Superseded, Take<RequestCompleted>(scene.Transport).State);
        scene.Transport.Advance(0, 1);
        Assert.True(later.Payload.DeepEquals(Take<ServiceInvocation>(scene.Transport).Payload));
    }

    /// <summary>Смена source generation завершает старый head отказом и освобождает admission нового экземпляра.</summary>
    [Fact]
    public void PipelinedAdmissionFenceReleasesRejectedStaleGeneration()
    {
        var scene = new Scene(new(MaxActive: 1));
        scene.Transport.Submit(Request(producer: "busy"), 0);
        Assert.Equal(AdmissionStatus.WouldBlock, scene.Transport.Submit(Request(), 0).Status);
        Assert.True(scene.Transport.Cancel(Request(producer: "busy").Key, 0).Accepted);
        _ = Take<RequestCompleted>(scene.Transport);
        var previous = scene.World.MutableServices["source"];
        scene.World.MutableServices["source"] = previous with { Generation = 2, Revision = 2 };
        Assert.Equal(AdmissionStatus.Rejected, scene.Transport.Submit(Request(), 0).Status);
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(Request(2) with { Source = new("source", 2) }, 0).Status);
    }

    /// <summary>Даже нулевой transit требует Advance; known delivery-only outcome и оба уровня replay не дублируются.</summary>
    [Fact]
    public void ZeroDelayIsAsyncAndKnownCompletionIsDeduplicated()
    {
        var scene = new Scene();
        var request = Request();
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(request, 0).Status);
        Assert.Equal(0, scene.Transport.EventCount);
        Assert.Equal(1, scene.Transport.Advance(0, 1));
        var invocation = Take<ServiceInvocation>(scene.Transport);
        Assert.Equal("binding/producer:1", invocation.InvocationId);
        Assert.Equal("target-tag", invocation.CapturedTag);
        Assert.True(scene.Transport.CompleteInvocation(Result(invocation), 0).Accepted);
        Assert.Equal(RequestState.Completed, Take<RequestCompleted>(scene.Transport).State);
        Assert.Equal("duplicate", scene.Transport.CompleteInvocation(Result(invocation), 0).Code);
        var duplicate = scene.Transport.Submit(request, 0);
        Assert.Equal(AdmissionStatus.Duplicate, duplicate.Status);
        Assert.Equal(RequestState.Completed, duplicate.State);
        Assert.Equal("request_conflict", scene.Transport.Submit(request with { Payload = NetworkValue.Null }, 0).Code);
        Assert.Equal(0, scene.Transport.ActiveCount);
        Assert.False(scene.Transport.TryDequeueEvent(out _));
    }

    /// <summary>Новый граф и альтернативный маршрут разрешены на due; initial path не является обязательной трассой.</summary>
    [Fact]
    public void DeliveryRevalidatesAgainstCurrentGraphAndAllowsAlternateRoute()
    {
        var scene = new Scene();
        scene.Routes.Latency = 100;
        scene.Transport.Submit(Request(), 0);
        Assert.Equal(0, scene.Transport.Advance(1, 10));
        scene.Graph = scene.MakeGraph(2);
        Assert.Equal(1, scene.Transport.Advance(2, 10));
        _ = Take<ServiceInvocation>(scene.Transport);
        Assert.Equal(new[] { 1UL, 2UL, 2UL }, scene.Routes.Calls.Select(call => call.Version));
    }

    /// <summary>Смена captured tag, service generation или physical owner generation отзывает in-flight обращение.</summary>
    [Theory]
    [InlineData("tag")]
    [InlineData("service")]
    [InlineData("node")]
    public void RetirementOrTagRevocationNeverInvokesReplacement(string change)
    {
        var scene = new Scene();
        scene.Routes.Latency = 100;
        scene.Transport.Submit(Request(), 0);
        var target = scene.World.MutableServices["target"];
        if (change == "tag") scene.World.MutableServices["target"] = target with { Tag = "new-tag" };
        else if (change == "service") scene.World.MutableServices["target"] = target with { Generation = 2 };
        else
        {
            scene.World.MutableNodes["target-node"] = scene.World.MutableNodes["target-node"] with { Generation = 2 };
            scene.World.MutableServices["target"] = target with { Owner = new("target-node", 2) };
        }
        scene.Transport.Advance(2, 10);
        Assert.Equal(RequestState.Failed, Take<RequestCompleted>(scene.Transport).State);
        Assert.False(scene.Transport.TryDequeueEvent(out _));
    }

    /// <summary>Публикация invocation в буфер ещё не разрешает handoff через dirty или отозванный world context.</summary>
    [Fact]
    public void BufferedInvocationIsRecheckedAtHandoffAndWaitsForTopology()
    {
        var scene = new Scene();
        scene.Transport.Submit(Request(), 0);
        scene.Transport.Advance(0, 1);
        scene.Graph = null;
        Assert.False(scene.Transport.TryDequeueEvent(out _));
        scene.Graph = scene.MakeGraph(2);
        scene.World.MutableServices["target"] = scene.World.MutableServices["target"] with { Tag = "changed" };
        Assert.Equal(RequestState.Failed, Take<RequestCompleted>(scene.Transport).State);
        Assert.Equal(0, scene.Transport.ActiveCount);
    }

    /// <summary>Опциональный deadline до native handoff доказывает отсутствие эффекта и завершается TimedOut.</summary>
    [Fact]
    public void DeadlineBeforeInvocationExpiresWithoutAnEffect()
    {
        var scene = new Scene();
        scene.Routes.Latency = 100;
        scene.Transport.Submit(Request() with { DeadlineDs = 1 }, 0);
        scene.Transport.Advance(1, 1);
        Assert.Equal(RequestState.TimedOut, Take<RequestCompleted>(scene.Transport).State);
        Assert.Equal(0, scene.Transport.ActiveCount);
    }

    /// <summary>Отсутствие deadline сохраняет no-TTL поведение даже при длительной паузе между Advance.</summary>
    [Fact]
    public void NoDeadlineDoesNotInventATimeout()
    {
        var scene = new Scene();
        scene.Transport.Submit(Request(), 0);
        scene.Transport.Advance(1_000_000, 1);
        _ = Take<ServiceInvocation>(scene.Transport);
        Assert.Equal(RequestState.AwaitingInvocation, scene.Transport.GetRequestState(Request().Key));
    }

    /// <summary>После handoff отмена не откатывает мир, а потеря владельца не повторяет потенциально исполненный эффект.</summary>
    [Fact]
    public void CancelBeforeAndAfterInvocationHaveDifferentProofOfOutcome()
    {
        var scene = new Scene();
        scene.Transport.Submit(Request(), 0);
        Assert.True(scene.Transport.Cancel(Request().Key, 0).Accepted);
        Assert.Equal(RequestState.Cancelled, Take<RequestCompleted>(scene.Transport).State);
        Assert.Equal(0, scene.Transport.Advance(100, 100));

        scene.Transport.Submit(Request(2), 100);
        scene.Transport.Advance(100, 1);
        var invocation = Take<ServiceInvocation>(scene.Transport);
        Assert.Equal("too_late", scene.Transport.Cancel(Request(2).Key, 100).Code);
        Assert.True(scene.Transport.MarkUnavailable(100).Accepted);
        Assert.Equal(RequestState.OutcomeUnknown, Take<RequestCompleted>(scene.Transport).State);
        Assert.Equal(0, scene.Transport.Advance(101, 100));
        Assert.False(scene.Transport.CompleteInvocation(Result(invocation), 101).Accepted);
        Assert.Equal(AdmissionStatus.Duplicate, scene.Transport.Submit(Request(2), 101).Status);
    }

    /// <summary>Deadline после handed-off invocation даёт unknown, а не обещание отсутствия native эффекта.</summary>
    [Fact]
    public void InvocationDeadlineIsOutcomeUnknownAndNeverReissues()
    {
        var scene = new Scene();
        scene.Transport.Submit(Request() with { DeadlineDs = 5 }, 0);
        scene.Transport.Advance(0, 1);
        _ = Take<ServiceInvocation>(scene.Transport);
        scene.Transport.Advance(5, 1);
        Assert.Equal(RequestState.OutcomeUnknown, Take<RequestCompleted>(scene.Transport).State);
        Assert.Equal(0, scene.Transport.Advance(6, 100));
    }

    /// <summary>Ответ использует reverse route и отдельную задержку, а отказ сети до ответа остаётся явным failure.</summary>
    [Fact]
    public void RequestReplyModelsReverseTransitAndRevalidatesResponseRoute()
    {
        var scene = new Scene(delivery: DeliverySemantics.RequestReply);
        scene.Routes.Latency = 100;
        scene.Transport.Submit(Request(), 0);
        scene.Transport.Advance(2, 1);
        var invocation = Take<ServiceInvocation>(scene.Transport);
        Assert.True(scene.Transport.CompleteInvocation(Result(invocation, NetworkValue.FromText("answer")), 3).Accepted);
        Assert.Equal(RequestState.ResponseInTransit, scene.Transport.GetRequestState(Request().Key));
        Assert.Equal(0, scene.Transport.Advance(4, 1));
        scene.Routes.Reachable = false;
        scene.Transport.Advance(5, 1);
        var failed = Take<RequestCompleted>(scene.Transport);
        Assert.Equal(RequestState.Failed, failed.State);
        Assert.Equal("response_route_unavailable", failed.Code);
        Assert.Equal("target-node", scene.Routes.Calls[^1].Source.NodeId);
        Assert.Equal("source-node", scene.Routes.Calls[^1].Destination.NodeId);
    }

    /// <summary>Output pressure не потребляет следующую due-работу и не принимает результат, для которого нет terminal slot.</summary>
    [Fact]
    public void EventBackpressurePreservesDueWorkAndInvocationResult()
    {
        var scene = new Scene(new(MaxEvents: 1));
        scene.Transport.Submit(Request(), 0);
        scene.Transport.Submit(Request(2), 0);
        Assert.Equal(1, scene.Transport.Advance(0, 10));
        var first = Take<ServiceInvocation>(scene.Transport);
        Assert.Equal(1, scene.Transport.Advance(0, 10));
        Assert.True(scene.Transport.CompleteInvocation(Result(first), 0).WouldBlock);
        var second = Take<ServiceInvocation>(scene.Transport);
        Assert.True(scene.Transport.CompleteInvocation(Result(first), 0).Accepted);
        Assert.Equal(RequestState.Completed, Take<RequestCompleted>(scene.Transport).State);
        Assert.True(scene.Transport.CompleteInvocation(Result(second), 0).Accepted);
        Assert.Equal(RequestState.Completed, Take<RequestCompleted>(scene.Transport).State);
    }

    /// <summary>Coalescing сохраняет original key, due и position; устаревшая revision не подменяет свежий body.</summary>
    [Fact]
    public void CoalescingPreservesOriginalDeliveryAndNewestBody()
    {
        var scene = new Scene();
        scene.Routes.Latency = 100;
        var original = Request() with { CoalescingKey = "state", StateRevision = 2, Payload = NetworkValue.FromText("old") };
        scene.Transport.Submit(original, 0);
        var replacement = Request(2) with { CoalescingKey = "state", StateRevision = 3, Payload = NetworkValue.FromText("new"), NativeContext = "new-native", AuthorizationContext = "new-auth" };
        var merged = scene.Transport.Submit(replacement, 1);
        Assert.Equal(AdmissionStatus.Coalesced, merged.Status);
        Assert.Equal(original.Key, merged.CoalescedInto);
        Assert.Equal(RequestState.Superseded, Take<RequestCompleted>(scene.Transport).State);
        scene.Transport.Submit(replacement with { Key = new("producer", 3), StateRevision = 1, Payload = NetworkValue.FromText("stale") }, 1);
        _ = Take<RequestCompleted>(scene.Transport);
        Assert.Equal(0, scene.Transport.Advance(1, 10));
        Assert.Equal(1, scene.Transport.Advance(2, 10));
        var invocation = Take<ServiceInvocation>(scene.Transport);
        Assert.Equal(original.Key, invocation.Key);
        Assert.Equal("new", Assert.IsType<NetworkText>(invocation.Payload).Value);
        Assert.Equal("new-native", invocation.NativeContext);
        Assert.Equal("new-auth", invocation.AuthorizationContext);
        Assert.Equal(AdmissionStatus.Duplicate, scene.Transport.Submit(original, 2).Status);
    }

    /// <summary>Команды без opt-in не объединяются; unsafe mutating opt-in отвергается до admission.</summary>
    [Fact]
    public void MutatingCommandsCannotBeCoalescedAccidentally()
    {
        var scene = new Scene(safety: OperationSafety.Mutating);
        Assert.Equal("coalescing_unsafe", scene.Transport.Submit(Request() with { CoalescingKey = "unsafe" }, 0).Code);
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(Request(), 0).Status);
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(Request(2), 0).Status);
        Assert.Equal(2, scene.Transport.ActiveCount);
    }

    /// <summary>Ordered сервис не получает второй invocation до известного terminal первого.</summary>
    [Fact]
    public void OrderedServiceRetainsAdmissionOrderAcrossNativeExecution()
    {
        var scene = new Scene(ordered: true);
        scene.Transport.Submit(Request(), 0);
        scene.Transport.Submit(Request(2), 0);
        Assert.Equal(1, scene.Transport.Advance(0, 10));
        var first = Take<ServiceInvocation>(scene.Transport);
        Assert.Equal(1UL, first.Key.Sequence);
        Assert.Equal(0, scene.Transport.Advance(0, 10));
        scene.Transport.CompleteInvocation(Result(first), 0);
        Assert.Equal(1, scene.Transport.Advance(0, 10));
        _ = Take<RequestCompleted>(scene.Transport);
        Assert.Equal(2UL, Take<ServiceInvocation>(scene.Transport).Key.Sequence);
    }

    /// <summary>Ограничение workBudget и weighted lanes защищают background от бесконечного ordinary потока.</summary>
    [Fact]
    public void AdvanceBudgetAndWeightedLanesAreBounded()
    {
        var scene = new Scene(new(OrdinaryWeight: 2));
        for (ulong i = 1; i <= 3; i++) scene.Transport.Submit(Request(i), 0);
        scene.Transport.Submit(Request(1, "background") with { CoalescingKey = "state" }, 0);
        Assert.Equal(2, scene.Transport.Advance(0, 2));
        Assert.Equal("producer", Take<ServiceInvocation>(scene.Transport).Key.ProducerId);
        Assert.Equal("producer", Take<ServiceInvocation>(scene.Transport).Key.ProducerId);
        Assert.Equal(1, scene.Transport.Advance(0, 1));
        Assert.Equal("background", Take<ServiceInvocation>(scene.Transport).Key.ProducerId);
    }

    /// <summary>Вытеснённый terminal ID остаётся закрытым watermark; history не нужен для предотвращения повторного эффекта.</summary>
    [Fact]
    public void BoundedHistoryWatermarkAndActiveAdmissionNeverReexecuteExpiredKey()
    {
        var scene = new Scene(new(MaxActive: 1, HistoryCapacity: 1, MaxProducers: 1));
        scene.Transport.Submit(Request(), 0);
        Assert.Equal(AdmissionStatus.WouldBlock, scene.Transport.Submit(Request(2), 0).Status);
        scene.Transport.Cancel(Request().Key, 0);
        _ = Take<RequestCompleted>(scene.Transport);
        scene.Transport.Submit(Request(2), 0);
        scene.Transport.Cancel(Request(2).Key, 0);
        _ = Take<RequestCompleted>(scene.Transport);
        Assert.Equal(1, scene.Transport.HistoryCount);
        Assert.Equal("request_expired", scene.Transport.Submit(Request(), 0).Code);
        Assert.Equal("producer_capacity", scene.Transport.Submit(Request(1, "another"), 0).Code);
        Assert.Equal(1, scene.Transport.ProducerCount);
        Assert.Equal(AdmissionStatus.Accepted, scene.Transport.Submit(Request(3), 0).Status);
    }

    /// <summary>Object insertion order и знак zero не меняют content identity, но форма пустой коллекции меняет.</summary>
    [Fact]
    public void FingerprintUsesTypedPayloadAndCanonicalObjectOrder()
    {
        var scene = new Scene();
        var first = NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["a"] = NetworkValue.FromNumber(-0.0), ["b"] = NetworkValue.Null });
        var reordered = NetworkValue.FromObject(new Dictionary<string, NetworkValue> { ["b"] = NetworkValue.Null, ["a"] = NetworkValue.FromNumber(0.0) });
        var request = Request() with { Payload = first };
        scene.Transport.Submit(request, 0);
        Assert.Equal(AdmissionStatus.Duplicate, scene.Transport.Submit(request with { Payload = reordered }, 0).Status);
        Assert.Equal("request_conflict", scene.Transport.Submit(request with { Payload = NetworkValue.FromArray([]) }, 0).Code);
    }
}
