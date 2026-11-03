using Netrun.Core;
using Netrun.Core.Communication;
using Netrun.Core.Discovery;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет bounded поиск по live facts отдельно от native accepts/permissions и route solver.</summary>
public sealed class DiscoveryTests
{
    /// <summary>Изменяемый published reader для независимых churn/revocation сценариев.</summary>
    private sealed class World : INetworkWorldView
    {
        /// <summary>Актуальные physical generations.</summary>
        internal Dictionary<string, NodeFact> CurrentNodes { get; } = new(StringComparer.Ordinal);
        /// <summary>Актуальные exact service instances.</summary>
        internal Dictionary<string, ServiceFact> CurrentServices { get; } = new(StringComparer.Ordinal);
        /// <inheritdoc />
        public IReadOnlyDictionary<string, NodeFact> Nodes => CurrentNodes;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, ServiceFact> Services => CurrentServices;
        /// <inheritdoc />
        public IReadOnlyDictionary<string, SectorFact> Sectors { get; } = new Dictionary<string, SectorFact>();
    }

    /// <summary>Независимый сетевой oracle позволяет измерить query slice и ordinary-first policy.</summary>
    private sealed class Routes : IRouteProvider
    {
        /// <summary>Контролируемая достижимость без реализации Dijkstra внутри теста.</summary>
        internal bool Reachable = true;
        /// <summary>Порядок фактически проверенных endpoints.</summary>
        internal List<(NodeKey Destination, RouteKind Mode)> Calls { get; } = [];
        /// <inheritdoc />
        public RouteResult FindRoute(GraphSnapshot graph, NodeKey source, NodeKey destination, RouteKind mode = RouteKind.Machine)
        {
            Calls.Add((destination, mode));
            return new(Reachable ? RouteStatus.Found : RouteStatus.Unreachable,
                Reachable ? [source, destination] : [], Reachable ? 1 : 0, graph.Version, 0);
        }
    }

    /// <summary>Отдельный capability oracle наблюдает точную frequency и никогда не используется неявно.</summary>
    private sealed class Communications : ICommunicationRouteProvider
    {
        /// <summary>Частоты всех явных fallback обращений.</summary>
        internal List<int?> Frequencies { get; } = [];
        /// <inheritdoc />
        public RouteResult FindRoute(GraphSnapshot graph, ServiceKey source, ServiceKey destination, int? frequency, INetworkWorldView world)
        {
            Frequencies.Add(frequency);
            var found = frequency is null or 1459;
            return new(found ? RouteStatus.Found : RouteStatus.Unreachable, found ? [world.Services[source.ServiceId].Owner,
                world.Services[destination.ServiceId].Owner] : [], found ? 3 : 0, graph.Version, 0);
        }
    }

    /// <summary>Композиция test dependencies без fake engine или native handlers.</summary>
    private sealed class Scene : IDisposable
    {
        /// <summary>Опубликованные факты меняются только самим тестом.</summary>
        internal World World { get; } = new();
        /// <summary>Реальный family/tag индекс под проверкой.</summary>
        internal ServiceDirectory Directory { get; } = new();
        /// <summary>Route oracle без скрытой фильтрации authorities.</summary>
        internal Routes Routes { get; } = new();
        /// <summary>Explicit fallback dependency.</summary>
        internal Communications Communications { get; } = new();
        /// <summary>Null моделирует промежуток до topology publication.</summary>
        internal GraphSnapshot? Graph;
        /// <summary>Самостоятельный bounded discovery owner.</summary>
        internal ServiceDiscovery Discovery { get; }

        /// <summary>Создаёт source и указанное число network-eligible authorities; native acceptance намеренно не моделируется.</summary>
        internal Scene(int destinations = 1, DiscoveryOptions? options = null)
        {
            World.CurrentNodes["source-node"] = Node("source-node");
            World.CurrentServices["source"] = Service("source", "source-node", "origin", "source-tag");
            Directory.Upsert(World.CurrentServices["source"]);
            for (var i = 0; i < destinations; i++) AddDestination(i);
            Graph = MakeGraph();
            Discovery = new(World, Directory, () => Graph, Routes, "binding", options, Communications);
        }

        /// <summary>Добавляет новый service generation в world и каталог; graph публикуется тестом явно.</summary>
        internal void AddDestination(int index)
        {
            var id = Id(index);
            var node = id + "-node";
            World.CurrentNodes[node] = Node(node);
            World.CurrentServices[id] = Service(id, node);
            Assert.Equal(DirectoryUpdateResult.Applied, Directory.Upsert(World.CurrentServices[id]));
        }

        /// <summary>Фиксирует отдельную immutable graph publication из текущих physical facts.</summary>
        internal GraphSnapshot MakeGraph() => new(1, 1,
            World.Nodes.Values.ToDictionary(node => new NodeKey(node.NodeId, node.Generation)),
            World.Nodes.Values.ToDictionary(node => node.State.Address, node => new NodeKey(node.NodeId, node.Generation)), [],
            World.Nodes.Values.ToDictionary(node => new NodeKey(node.NodeId, node.Generation), _ => new List<PhysicalEdge>()), [], [], [], 3);

        /// <inheritdoc />
        public void Dispose() => Discovery.Dispose();
    }

    /// <summary>Ordinal identity позволяет сравнивать фактический порядок страниц.</summary>
    private static string Id(int index) => "authority-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Допустимый online physical endpoint с exact generation.</summary>
    private static NodeFact Node(string id) => new(id, 1, 1, new(id, "server", new(1, 1, 1), "s", true));
    /// <summary>Полный valid fact с declared operation, без synthetic native eligibility атрибутов.</summary>
    private static ServiceFact Service(string id, string owner = "owner", string family = "research.authority", string? tag = "tag") =>
        new(id, 1, 1, new(owner, 1), family, tag, true, new([new("read", OperationSafety.ReadOnly)], 64));
    /// <summary>По умолчанию caller получает все bounded authorities, а не первый target до native filtering.</summary>
    private static DiscoveryRequest Request(ulong sequence = 1, string producer = "producer") =>
        new(new(producer, sequence), new("source", 1), "research.authority");
    /// <summary>Получает одну готовую страницу без implicit Advance.</summary>
    private static DiscoveryPage Take(ServiceDiscovery discovery)
    {
        Assert.True(discovery.TryDequeueEvent(out var page));
        return Assert.IsType<DiscoveryPage>(page);
    }

    /// <summary>Generation replacement переносит оба indexes; старый capture immutable и stale remove не удаляет новый instance.</summary>
    [Fact]
    public void DirectoryIndexesExactGenerationsAndKeepsCapturedKeysImmutable()
    {
        var directory = new ServiceDirectory(1);
        var original = Service("a");
        Assert.Equal(DirectoryUpdateResult.Applied, directory.Upsert(original));
        var pinned = directory.Capture(original.Family, original.Tag);
        var replacement = original with { Generation = 2, Family = "other", Tag = "new" };
        Assert.Equal(DirectoryUpdateResult.Applied, directory.Upsert(replacement));
        Assert.False(directory.Remove(new("a", 1)));
        Assert.Empty(directory.Capture(original.Family).Keys);
        Assert.Empty(directory.Capture("other", "tag").Keys);
        Assert.Equal(new ServiceKey("a", 2), Assert.Single(directory.Capture("other", "new").Keys));
        Assert.Equal(new ServiceKey("a", 1), Assert.Single(pinned.Keys));
        Assert.True(directory.Revision > pinned.Revision);
        Assert.Equal(DirectoryUpdateResult.Capacity, directory.Upsert(Service("b")));
        Assert.True(directory.Remove(new("a", 2)));
        Assert.Equal(DirectoryUpdateResult.Applied, directory.Upsert(Service("b")));
        directory.Reset();
        Assert.Equal(0, directory.Count);
        Assert.Empty(directory.Capture("research.authority").Keys);
        Assert.Single(pinned.Keys);
    }

    /// <summary>Shared fact validation и conflict checks происходят до изменения revision/indexes.</summary>
    [Fact]
    public void DirectoryValidatesFullFactsAndDoesNotPublishConflictingRevisions()
    {
        var directory = new ServiceDirectory();
        var original = Service("a", tag: new string('t', 256));
        Assert.Equal(DirectoryUpdateResult.Applied, directory.Upsert(original));
        var revision = directory.Revision;
        Assert.Equal(DirectoryUpdateResult.Ignored, directory.Upsert(original));
        Assert.Equal(DirectoryUpdateResult.Invalid, directory.Upsert(original with { Generation = 0 }));
        Assert.Equal(DirectoryUpdateResult.Invalid, directory.Upsert(original with { Family = "bad\ud800" }));
        Assert.Equal(DirectoryUpdateResult.Conflict, directory.Upsert(original with { Family = "different" }));
        Assert.Equal(revision, directory.Revision);
        Assert.Single(directory.Capture(original.Family, original.Tag).Keys);
        Assert.False(directory.Remove(new(null!, 1)));
    }

    /// <summary>Capture обходит только выбранный bucket, возвращает bounded ordinal префикс и явный truncation marker.</summary>
    [Fact]
    public void DirectoryCaptureHasIndependentFamilyAndTagBucketsAndExplicitCap()
    {
        var directory = new ServiceDirectory();
        directory.Upsert(Service("z"));
        directory.Upsert(Service("a"));
        directory.Upsert(Service("b", tag: "other"));
        directory.Upsert(Service("foreign", family: "other"));
        var bounded = directory.Capture("research.authority", limit: 2);
        Assert.Equal(["a", "b"], bounded.Keys.Select(key => key.ServiceId));
        Assert.True(bounded.Truncated);
        Assert.Equal(["a", "z"], directory.Capture("research.authority", "tag").Keys.Select(key => key.ServiceId));
        Assert.False(directory.Capture("research.authority", "tag").Truncated);
    }

    /// <summary>Submit не маршрутизирует inline; нулевой budget не продвигает query, native authority selection не обрезается до одного.</summary>
    [Fact]
    public void DefaultQueryReturnsEveryNetworkEligibleAuthorityAsynchronously()
    {
        using var scene = new Scene(3);
        Assert.Equal(AdmissionStatus.Accepted, scene.Discovery.Submit(Request()).Status);
        Assert.Equal(0, scene.Discovery.Advance(0));
        Assert.Empty(scene.Routes.Calls);
        Assert.False(scene.Discovery.TryDequeueEvent(out _));
        Assert.Equal(3, scene.Discovery.Advance(20));
        var page = Take(scene.Discovery);
        Assert.True(page.Complete);
        Assert.Equal("completed", page.Code);
        Assert.Equal(3, page.Candidates.Count);
        Assert.All(scene.Routes.Calls, call => Assert.Equal(RouteKind.Discovery, call.Mode));
        Assert.Equal(0, scene.Discovery.ActiveCount);
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
    }

    /// <summary>Recipient tag отбирает только нужный bucket, а explicit result cap никогда не скрывается как полный ответ.</summary>
    [Fact]
    public void RecipientTagAndExplicitMaxResultsProduceHonestTerminalStatus()
    {
        using var scene = new Scene(3);
        var other = scene.World.CurrentServices[Id(2)] with { Revision = 2, Tag = "other" };
        scene.World.CurrentServices[Id(2)] = other;
        scene.Directory.Upsert(other);
        scene.Discovery.Submit(Request() with { RecipientTag = "tag", MaxResults = 1 });
        scene.Discovery.Advance(100);
        var truncated = Take(scene.Discovery);
        Assert.Single(truncated.Candidates);
        Assert.True(truncated.Complete);
        Assert.Equal("truncated", truncated.Code);
        scene.Discovery.Submit(Request(2) with { RecipientTag = "other" });
        scene.Discovery.Advance(100);
        Assert.Equal(Id(2), Assert.Single(Take(scene.Discovery).Candidates).ServiceId);
    }

    /// <summary>Pending topology не создаёт negative answer и не вызывает route oracle; публикация продолжает тот же query.</summary>
    [Fact]
    public void PendingGraphParksWithoutSpinAndLaterResumesSameQuery()
    {
        using var scene = new Scene();
        scene.Graph = null;
        scene.Discovery.Submit(Request());
        Assert.Equal(0, scene.Discovery.Advance(100));
        Assert.Equal(0, scene.Discovery.Advance(100));
        Assert.Empty(scene.Routes.Calls);
        Assert.Equal(1, scene.Discovery.ActiveCount);
        Assert.False(scene.Discovery.TryDequeueEvent(out _));
        scene.Graph = scene.MakeGraph();
        Assert.Equal(1, scene.Discovery.Advance(1));
        Assert.Single(Take(scene.Discovery).Candidates);
    }

    /// <summary>Один query получает не более восьми checks до уступки; внешний budget никогда не превышается.</summary>
    [Fact]
    public void RoundRobinSlicesRespectBothQueryAndGlobalWorkBudgets()
    {
        using var scene = new Scene(9);
        scene.Discovery.Submit(Request());
        scene.Discovery.Submit(Request(2));
        Assert.Equal(10, scene.Discovery.Advance(10));
        Assert.Equal(10, scene.Routes.Calls.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(index => Id(index) + "-node").Concat([Id(0) + "-node", Id(1) + "-node"]),
            scene.Routes.Calls.Select(call => call.Destination.NodeId));
        Assert.Equal(2, scene.Discovery.ActiveCount);
        Assert.Equal(1, scene.Discovery.Advance(1));
        Assert.Equal(new RequestKey("producer", 1), Take(scene.Discovery).Key);
        Assert.Equal(1, scene.Discovery.ActiveCount);
    }

    /// <summary>Pinned keys не видят новый service; live retirement/tag/offline факты фильтруются даже для buffered результата.</summary>
    [Fact]
    public void ChurnCannotResurrectRetiredKeysOrLeakNewServicesIntoPinnedQuery()
    {
        using var scene = new Scene(4);
        scene.Discovery.Submit(Request() with { RecipientTag = "tag" });
        scene.Discovery.Advance(1);
        var first = scene.World.CurrentServices[Id(0)];
        scene.World.CurrentServices[Id(0)] = first with { Online = false };
        var replacement = scene.World.CurrentServices[Id(1)] with { Generation = 2 };
        scene.World.CurrentServices[Id(1)] = replacement;
        scene.Directory.Upsert(replacement);
        var retagged = scene.World.CurrentServices[Id(2)] with { Revision = 2, Tag = "different" };
        scene.World.CurrentServices[Id(2)] = retagged;
        scene.Directory.Upsert(retagged);
        scene.AddDestination(4);
        scene.Graph = scene.MakeGraph();
        scene.Discovery.Advance(100);
        Assert.Equal(new ServiceKey(Id(3), 1), Assert.Single(Take(scene.Discovery).Candidates));
        scene.Discovery.Submit(Request(2) with { RecipientTag = "tag" });
        scene.Discovery.Advance(100);
        Assert.Equal([new ServiceKey(Id(1), 2), new ServiceKey(Id(3), 1), new ServiceKey(Id(4), 1)], Take(scene.Discovery).Candidates);
    }

    /// <summary>Source revocation отзывает unread pages и остаток query даже во время pending graph.</summary>
    [Fact]
    public void SourceRevocationProducesOneFailureAndReleasesAllRetainedState()
    {
        using var scene = new Scene(2, new(PageSize: 1));
        scene.Discovery.Submit(Request());
        scene.Discovery.Advance(1);
        scene.World.CurrentServices["source"] = scene.World.CurrentServices["source"] with { Tag = "revoked" };
        scene.Graph = null;
        var page = Take(scene.Discovery);
        Assert.True(page.Complete);
        Assert.Equal("source_unavailable", page.Code);
        Assert.Empty(page.Candidates);
        Assert.Equal(0, scene.Discovery.ActiveCount);
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
        Assert.False(scene.Discovery.TryDequeueEvent(out _));
        Assert.Equal(RequestState.Failed, scene.Discovery.Submit(Request()).State);
    }

    /// <summary>Непрочитанная terminal page не отдаёт новые поколения и не исчезает, пока graph pending.</summary>
    [Fact]
    public void CompletedPagesRemainBoundedAndAreLiveCheckedAtHandoff()
    {
        using var scene = new Scene();
        scene.Discovery.Submit(Request());
        scene.Discovery.Advance(1);
        Assert.Equal(0, scene.Discovery.ActiveCount);
        Assert.Equal(1, scene.Discovery.RetainedCandidateCount);
        scene.Graph = null;
        Assert.False(scene.Discovery.TryDequeueEvent(out _));
        Assert.Equal(1, scene.Discovery.EventCount);
        scene.World.CurrentServices[Id(0)] = scene.World.CurrentServices[Id(0)] with { Generation = 2 };
        scene.Graph = scene.MakeGraph();
        var page = Take(scene.Discovery);
        Assert.Empty(page.Candidates);
        Assert.True(page.Complete);
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
    }

    /// <summary>Full event queue не потребляет следующий candidate; освобождение одного slot продолжает exact cursor.</summary>
    [Fact]
    public void EventBackpressurePreservesCursorAndDoesNotLoseAnyResult()
    {
        using var scene = new Scene(3, new(MaxEvents: 1, PageSize: 1));
        scene.Discovery.Submit(Request());
        Assert.Equal(1, scene.Discovery.Advance(100));
        Assert.Equal(0, scene.Discovery.Advance(100));
        Assert.Single(scene.Routes.Calls);
        var pages = new List<DiscoveryPage> { Take(scene.Discovery) };
        Assert.Equal(1, scene.Discovery.Advance(100));
        pages.Add(Take(scene.Discovery));
        Assert.Equal(1, scene.Discovery.Advance(100));
        pages.Add(Take(scene.Discovery));
        Assert.Equal(Enumerable.Range(0, 3).Select(Id), pages.SelectMany(page => page.Candidates).Select(key => key.ServiceId));
        Assert.False(pages[0].Complete);
        Assert.True(pages[^1].Complete);
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
    }

    /// <summary>Настоящий предел 64 разбивает широкий ответ; terminal pages продолжают удерживать только свои keys.</summary>
    [Fact]
    public void WideReplyUsesBoundedPagesAndReleasesKeysAsConsumerReadsThem()
    {
        using var scene = new Scene(70);
        scene.Discovery.Submit(Request());
        Assert.Equal(70, scene.Discovery.Advance(100));
        Assert.Equal(2, scene.Discovery.EventCount);
        Assert.Equal(70, scene.Discovery.RetainedCandidateCount);
        var first = Take(scene.Discovery);
        Assert.Equal(64, first.Candidates.Count);
        Assert.False(first.Complete);
        Assert.Equal(6, scene.Discovery.RetainedCandidateCount);
        var last = Take(scene.Discovery);
        Assert.Equal(6, last.Candidates.Count);
        Assert.True(last.Complete);
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
        Assert.Equal(Enumerable.Range(0, 70).Select(Id), first.Candidates.Concat(last.Candidates).Select(key => key.ServiceId));
    }

    /// <summary>Удалённое поколение physical owner нельзя обнаружить через оставшуюся service запись.</summary>
    [Fact]
    public void PhysicalOwnerRetirementSkipsCandidateBeforeAnyRouteSearch()
    {
        using var scene = new Scene();
        scene.Discovery.Submit(Request());
        var owner = Id(0) + "-node";
        scene.World.CurrentNodes[owner] = scene.World.CurrentNodes[owner] with { Generation = 2 };
        Assert.Equal(1, scene.Discovery.Advance(10));
        Assert.Empty(Take(scene.Discovery).Candidates);
        Assert.Empty(scene.Routes.Calls);
    }

    /// <summary>Ограничение snapshot явно truncates, а global retained cap учитывает unread pages завершённого query.</summary>
    [Fact]
    public void CandidateCapsIncludeCompletedUnreadPagesAndWouldBlockDoesNotSpendSerial()
    {
        using var scene = new Scene(4, new(MaxCandidates: 3, MaxRetainedCandidates: 3));
        scene.Discovery.Submit(Request());
        scene.Discovery.Advance(100);
        Assert.Equal(3, scene.Discovery.RetainedCandidateCount);
        Assert.Equal("candidate_capacity", scene.Discovery.Submit(Request(2)).Code);
        var page = Take(scene.Discovery);
        Assert.Equal(3, page.Candidates.Count);
        Assert.Equal("truncated", page.Code);
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
        Assert.Equal(AdmissionStatus.Accepted, scene.Discovery.Submit(Request(2)).Status);
    }

    /// <summary>Освобождение query slot между кадрами не позволяет новому serial уничтожить retry старого.</summary>
    [Fact]
    public void PipelinedDiscoveryHeadRetainsAdmissionUntilRetryOrCancellation()
    {
        using var scene = new Scene(1, new(MaxQueries: 1, MaxProducers: 2));
        scene.Discovery.Submit(Request(producer: "busy"));
        Assert.Equal(AdmissionStatus.WouldBlock, scene.Discovery.Submit(Request()).Status);
        scene.Discovery.Advance(100);
        _ = Take(scene.Discovery);
        Assert.Equal("producer_backpressure", scene.Discovery.Submit(Request(2)).Code);
        Assert.Equal(AdmissionStatus.Accepted, scene.Discovery.Submit(Request()).Status);
        Assert.Equal(AdmissionStatus.WouldBlock, scene.Discovery.Submit(Request(2)).Status);
        Assert.Equal("request_unknown", scene.Discovery.Cancel(Request(2).Key).Code);
        scene.Discovery.Advance(100);
        _ = Take(scene.Discovery);
        Assert.Equal(AdmissionStatus.Accepted, scene.Discovery.Submit(Request(3)).Status);
        Assert.Equal(2, scene.Discovery.ProducerCount);
    }

    /// <summary>Per-source и global query slots независимы; отказ не резервирует producer sequence.</summary>
    [Fact]
    public void SourceAndGlobalQueryCapsAreIndependent()
    {
        using var scene = new Scene(1, new(MaxQueries: 2, MaxQueriesPerSource: 1));
        scene.World.CurrentServices["source2"] = Service("source2", "source-node", "origin");
        scene.World.CurrentServices["source3"] = Service("source3", "source-node", "origin");
        scene.Discovery.Submit(Request());
        Assert.Equal("source_capacity", scene.Discovery.Submit(Request(2)).Code);
        Assert.Equal(AdmissionStatus.Accepted, scene.Discovery.Submit(Request(2) with { Source = new("source2", 1) }).Status);
        Assert.Equal("query_capacity", scene.Discovery.Submit(Request(3) with { Source = new("source3", 1) }).Code);
        scene.Discovery.Advance(100);
        _ = Take(scene.Discovery);
        _ = Take(scene.Discovery);
        Assert.Equal(AdmissionStatus.Accepted, scene.Discovery.Submit(Request(3)).Status);
    }

    /// <summary>Dedup identity не включает world/directory revisions; history eviction оставляет producer barrier против forgotten replay.</summary>
    [Fact]
    public void DedupHistoryAndProducerWatermarkNeverReexecuteForgottenQuery()
    {
        using var scene = new Scene(1, new(HistoryCapacity: 1, MaxProducers: 1));
        scene.Discovery.Submit(Request());
        Assert.Equal(AdmissionStatus.Duplicate, scene.Discovery.Submit(Request()).Status);
        Assert.Equal("request_conflict", scene.Discovery.Submit(Request() with { RecipientTag = "other" }).Code);
        scene.Discovery.Advance(1);
        _ = Take(scene.Discovery);
        scene.Directory.Upsert(scene.World.Services[Id(0)] with { Revision = 2 });
        var duplicate = scene.Discovery.Submit(Request());
        Assert.Equal(AdmissionStatus.Duplicate, duplicate.Status);
        Assert.Equal(RequestState.Completed, duplicate.State);
        Assert.False(scene.Discovery.TryDequeueEvent(out _));
        Assert.Equal("producer_capacity", scene.Discovery.Submit(Request(producer: "other")).Code);
        scene.Discovery.Submit(Request(2));
        scene.Discovery.Advance(1);
        _ = Take(scene.Discovery);
        Assert.Equal(1, scene.Discovery.HistoryCount);
        Assert.Equal(1, scene.Discovery.ProducerCount);
        Assert.Equal("request_expired", scene.Discovery.Submit(Request()).Code);
        Assert.Equal(2, scene.Routes.Calls.Count);
    }

    /// <summary>Cancel при pressure неизменяем; собственную unread page можно отозвать и заменить единственным terminal cancellation.</summary>
    [Fact]
    public void CancellationRespectsPressureAndCleansQuerySnapshots()
    {
        using var scene = new Scene(2, new(MaxEvents: 1, PageSize: 1));
        scene.Discovery.Submit(Request());
        scene.Discovery.Submit(Request(2));
        scene.Discovery.Advance(1);
        Assert.True(scene.Discovery.Cancel(Request(2).Key).WouldBlock);
        Assert.Equal(2, scene.Discovery.ActiveCount);
        Assert.True(scene.Discovery.Cancel(Request().Key).Accepted);
        var first = Take(scene.Discovery);
        Assert.Equal("cancelled", first.Code);
        Assert.Empty(first.Candidates);
        Assert.Equal(2, scene.Discovery.RetainedCandidateCount);
        Assert.True(scene.Discovery.Cancel(Request(2).Key).Accepted);
        Assert.Equal("cancelled", Take(scene.Discovery).Code);
        Assert.Equal(0, scene.Discovery.ActiveCount);
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
        Assert.Equal(RequestState.Cancelled, scene.Discovery.Submit(Request()).State);
    }

    /// <summary>Communication fallback требует явного opt-in, передаёт nullable frequency и уступает обычному пути.</summary>
    [Fact]
    public void CommunicationFrequencyIsExplicitAndOrdinaryPathHasPriority()
    {
        using var scene = new Scene();
        scene.Routes.Reachable = false;
        scene.Discovery.Submit(Request());
        scene.Discovery.Advance(1);
        Assert.Empty(Take(scene.Discovery).Candidates);
        Assert.Empty(scene.Communications.Frequencies);
        scene.Discovery.Submit(Request(2) with { Communications = true, Frequency = 1459 });
        scene.Discovery.Advance(1);
        Assert.Single(Take(scene.Discovery).Candidates);
        scene.Discovery.Submit(Request(3) with { Communications = true, Frequency = 1461 });
        scene.Discovery.Advance(1);
        Assert.Empty(Take(scene.Discovery).Candidates);
        scene.Discovery.Submit(Request(4) with { Communications = true });
        scene.Discovery.Advance(1);
        Assert.Single(Take(scene.Discovery).Candidates);
        Assert.Equal(new int?[] { 1459, 1461, null }, scene.Communications.Frequencies);
        scene.Routes.Reachable = true;
        scene.Discovery.Submit(Request(5) with { Communications = true, Frequency = 1461 });
        scene.Discovery.Advance(1);
        Assert.Single(Take(scene.Discovery).Candidates);
        Assert.Equal(3, scene.Communications.Frequencies.Count);
        Assert.Equal(AdmissionStatus.Rejected, scene.Discovery.Submit(Request(6) with { Frequency = 1459 }).Status);
    }

    /// <summary>Непригодный source не принимает serial; invalid metadata и disposed binding не запускают route checks.</summary>
    [Fact]
    public void InvalidSourcesAndMetadataAreRejectedBeforeAdmissionMutation()
    {
        using var scene = new Scene();
        Assert.Equal(AdmissionStatus.Rejected, scene.Discovery.Submit(Request() with { Source = new("source", 2) }).Status);
        Assert.Equal(AdmissionStatus.Rejected, scene.Discovery.Submit(Request() with { Key = new("bad:producer", 1) }).Status);
        Assert.Equal(AdmissionStatus.Rejected, scene.Discovery.Submit(Request() with { DestinationFamily = "bad\ud800" }).Status);
        Assert.Equal(AdmissionStatus.Rejected, scene.Discovery.Submit(Request() with { MaxResults = 4097 }).Status);
        Assert.Equal(0, scene.Discovery.ProducerCount);
        Assert.Equal(AdmissionStatus.Accepted, scene.Discovery.Submit(Request()).Status);
        scene.Discovery.Dispose();
        Assert.Equal(0, scene.Discovery.RetainedCandidateCount);
        Assert.Throws<ObjectDisposedException>(() => scene.Discovery.Submit(Request(2)));
    }
}
