using Netrun.Core;
using Netrun.Core.Entities;
using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;
using Netrun.Core.Access;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет вынесенный из DM constrained BFS, ограниченность работы и stale/replay защиту.</summary>
public sealed class EntitySimulationTests
{
    /// <summary>Точное поколение тестового узла.</summary>
    private static NodeKey Key(string id, ulong generation = 1) => new(id, generation);
    /// <summary>Одна native сущность; её revision не совпадает с request sequence.</summary>
    private static readonly ActorKey Actor = new("actor", 7);

    /// <summary>Полный immutable факт без native ссылок.</summary>
    private static NodeFact Node(string id, bool online = true, bool relay = false, ulong generation = 1) =>
        new(id, generation, 1, new(id, "server", new(1, 1, 1), "s", online, new(null, null, IsNtnetRelay: relay)));

    /// <summary>Небольшая явная публикация для проверки маршрутов без зависимости от геометрического builder.</summary>
    private static GraphSnapshot Graph(NodeFact[] facts, (string From, string To)[] links, ulong version = 1)
    {
        var nodes = facts.ToDictionary(node => Key(node.NodeId, node.Generation));
        var addresses = nodes.ToDictionary(pair => pair.Value.State.Address, pair => pair.Key, StringComparer.Ordinal);
        var edges = links.Select(link => new PhysicalEdge(addresses[link.From], addresses[link.To], PhysicalLinkKind.Apc, 1, 1)).ToArray();
        var adjacency = nodes.Keys.ToDictionary(key => key, _ => new List<PhysicalEdge>());
        foreach (var edge in edges) { adjacency[edge.From].Add(edge); adjacency[edge.To].Add(edge); }
        foreach (var (key, incident) in adjacency)
            incident.Sort((a, b) => StringComparer.Ordinal.Compare(
                nodes[a.From == key ? a.To : a.From].State.Address,
                nodes[b.From == key ? b.To : b.From].State.Address));
        var relays = nodes.Where(pair => pair.Value.State.Network!.IsNtnetRelay)
            .Select(pair => pair.Key).OrderBy(key => key.NodeId, StringComparer.Ordinal).ToArray();
        return new GraphSnapshot(version, 42, nodes, addresses, edges, adjacency, relays,
            new(), nodes.Keys.ToDictionary(key => key, _ => "lan"), 3);
    }

    /// <summary>Конструирует обычный immutable ACL query; источник намеренно не обязан входить в AllowedNodes.</summary>
    private static EntityNavigationRequest Request(string destination = "D", ulong sequence = 1, string producer = "navigation",
        params string[] allowed) => new(new(producer, sequence), Actor, 19, Key("A"), Key(destination),
            Access(allowed));

    /// <summary>Raw native policies и direct grants; готовые allowed-node результаты в production API не передаются.</summary>
    private static EntityAccessSnapshot Access(string[] grants)
    {
        var network = new NetworkKey("lan", 1);
        return new(new("session", "principal", new([])),
            new(grants.Prepend("A").Distinct().Select(id => new NodeAccessPolicy(Key(id), network, 0, RequiredAccess: new([])))),
            new([new(network, "epoch", new([]))]),
            new(grants.Select(id => new NodeAccessGrant(Key(id), AccessLevel.User))), new([]), new([]));
    }

    /// <summary>Продвигает solver с малым бюджетом, включая continuation выдачи страниц.</summary>
    private static List<EntityNavigationPage> Finish(EntityNavigationBroker broker, int budget = 1)
    {
        var pages = new List<EntityNavigationPage>();
        for (var i = 0; i < 100_000; i++)
        {
            broker.Advance(budget);
            while (broker.TryDequeueEvent(out var page)) pages.Add(page!);
            if (broker.ActiveCount == 0) return pages;
        }
        throw new InvalidOperationException("Navigation did not terminate.");
    }

    /// <summary>Кратчайший по hops маршрут выбирается только среди разрешённых узлов; более длинный допустимый путь сохраняется.</summary>
    [Fact]
    public void ConstrainedSearchFindsLongerAllowedRouteWithoutDmBfs()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C"), Node("D"), Node("E")],
            [("A", "B"), ("B", "D"), ("A", "C"), ("C", "E"), ("E", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        var request = Request(allowed: ["C", "E", "D"]);
        Assert.Equal(AdmissionStatus.Accepted, broker.Submit(request).Status);
        Assert.False(broker.TryDequeueEvent(out _));
        var page = Assert.Single(Finish(broker));
        Assert.Equal("completed", page.Code);
        Assert.Equal(new[] { Key("C"), Key("E"), Key("D") }, page.Nodes);
        Assert.Equal(Actor, page.Actor);
        Assert.Equal(19UL, page.ActorRevision);
        Assert.Equal(Key("A"), page.Source);
        Assert.Equal(Key("D"), page.Destination);
        Assert.Equal(graph.Version, page.GraphVersion);
        Assert.Equal(graph.InputSequence, page.GraphInputSequence);
        Assert.Equal("binding", page.BindingEpoch);
    }

    /// <summary>Все транзитные узлы требуют и ACL, и online, а не только конечное назначение.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DeniedOrOfflineIntermediateNodeDoesNotGrantTravel(bool allowed, bool online)
    {
        var graph = Graph([Node("A"), Node("B", online), Node("D")], [("A", "B"), ("B", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        broker.Submit(Request(allowed: allowed ? ["B", "D"] : ["D"]));
        var page = Assert.Single(Finish(broker));
        Assert.Equal("unreachable", page.Code);
        Assert.Empty(page.Nodes);
    }

    /// <summary>Замена поколения узла не наследует ACL старого поколения с тем же Id.</summary>
    [Fact]
    public void ReplacedGenerationIsNotAuthorizedByOldNodeFact()
    {
        var graph = Graph([Node("A"), Node("B", generation: 2), Node("D")], [("A", "B"), ("B", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        broker.Submit(Request(allowed: ["B", "D"]));
        Assert.Equal("access_domain_changed", Assert.Single(Finish(broker)).Code);
    }

    /// <summary>Реальные relay дают один quantum hop; каждый endpoint и global backbone должны быть operational.</summary>
    [Theory]
    [InlineData(true, true, "completed")]
    [InlineData(false, true, "unreachable")]
    [InlineData(true, false, "unreachable")]
    public void QuantumTravelUsesExistingBackboneAndRelayAvailability(bool backbone, bool operational, string code)
    {
        var graph = Graph([Node("A"), Node("R1", relay: true), Node("R2", relay: true), Node("D")],
            [("A", "R1"), ("R2", "D")]);
        var availability = new Availability(graph) { BackboneEnabled = backbone };
        availability.Nodes[Key("R2")] = new(true, operational);
        using var broker = new EntityNavigationBroker(() => graph, "binding", availability: availability);
        broker.Submit(Request(allowed: ["R1", "R2", "D"]));
        var page = Assert.Single(Finish(broker));
        Assert.Equal(code, page.Code);
        if (code == "completed") Assert.Equal(new[] { Key("R1"), Key("R2"), Key("D") }, page.Nodes);
    }

    /// <summary>Большая степень узла и список ACL не разворачиваются внутри admission либо одного Advance.</summary>
    [Fact]
    public void WorkBudgetBoundsAclLoadingAndEveryNeighborExpansion()
    {
        var facts = Enumerable.Range(0, 512).Select(i => Node($"N{i:D3}")).Prepend(Node("A")).ToArray();
        var graph = Graph(facts, facts.Skip(1).Select(node => ("A", node.NodeId)).ToArray());
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        broker.Submit(Request("N511", allowed: facts.Skip(1).Select(node => node.NodeId).ToArray()));
        Assert.Equal(1, broker.Advance(1));
        Assert.False(broker.TryDequeueEvent(out _));
        Assert.Equal(511, broker.Advance(511));
        Assert.False(broker.TryDequeueEvent(out _));
        Assert.Equal(5, broker.Advance(5));
        Assert.False(broker.TryDequeueEvent(out _));
        Assert.Equal("completed", Assert.Single(Finish(broker)).Code);
    }

    /// <summary>Небольшой запрос получает slice между шагами большого, не ожидая полного графового поиска.</summary>
    [Fact]
    public void ConcurrentRequestsAdvanceFairly()
    {
        var facts = Enumerable.Range(0, 100).Select(i => Node($"N{i:D3}")).Prepend(Node("A")).ToArray();
        var graph = Graph(facts, facts.Skip(1).Select(node => ("A", node.NodeId)).ToArray());
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        broker.Submit(Request("N099", producer: "large", allowed: facts.Skip(1).Select(node => node.NodeId).ToArray()));
        broker.Submit(Request("A", producer: "small"));
        broker.Advance(64);
        Assert.True(broker.TryDequeueEvent(out var page));
        Assert.Equal("small", page!.Key.ProducerId);
        Assert.Equal("completed", page.Code);
        Assert.Empty(page.Nodes);
        Assert.Equal(1, broker.ActiveCount);
    }

    /// <summary>Путь сегментируется без потери порядка и без непроверенного partial publish.</summary>
    [Fact]
    public void PagedPathExcludesSourceAndResumesOnlyAfterPageIsRead()
    {
        var graph = Graph([Node("A"), Node("B"), Node("C"), Node("D")], [("A", "B"), ("B", "C"), ("C", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding", new(PageRows: 1));
        broker.Submit(Request(allowed: ["B", "C", "D"]));
        broker.Advance(1000);
        Assert.Equal(1, broker.EventCount);
        Assert.Equal(0, broker.Advance(1000));
        var pages = new List<EntityNavigationPage>();
        while (broker.ActiveCount > 0)
        {
            Assert.True(broker.TryDequeueEvent(out var page));
            pages.Add(page!);
            broker.Advance(1000);
        }
        Assert.Equal(new[] { Key("B"), Key("C"), Key("D") }, pages.SelectMany(page => page.Nodes));
        Assert.Equal(new ulong[] { 1, 2, 3 }, pages.Select(page => page.PageIndex));
        Assert.All(pages.Take(2), page => Assert.False(page.Complete));
        Assert.Equal("completed", pages[^1].Code);
    }

    /// <summary>Изменение snapshot во время BFS отзывает весь query, не маркируя старые шаги новой версией.</summary>
    [Fact]
    public void GraphChangeDuringSearchRejectsOldReadModel()
    {
        GraphSnapshot? graph = Graph([Node("A"), Node("B"), Node("D")], [("A", "B"), ("B", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        broker.Submit(Request(allowed: ["B", "D"]));
        broker.Advance(2);
        graph = Graph([Node("A"), Node("B"), Node("D")], [("A", "D")], version: 2);
        var page = Assert.Single(Finish(broker));
        Assert.Equal("navigation_changed", page.Code);
        Assert.Equal(1UL, page.GraphVersion);
        Assert.Empty(page.Nodes);
    }

    /// <summary>Смена availability после генерации, но до handoff, также запрещает completed старого пути.</summary>
    [Fact]
    public void AvailabilityChangeBeforeHandoffRevokesCompletedPath()
    {
        var graph = Graph([Node("A"), Node("D")], [("A", "D")]);
        var availability = new Availability(graph);
        using var broker = new EntityNavigationBroker(() => graph, "binding", availability: availability);
        broker.Submit(Request(allowed: ["D"]));
        broker.Advance(100);
        availability.Revision++;
        Assert.True(broker.TryDequeueEvent(out var page));
        Assert.Equal("navigation_changed", page!.Code);
        Assert.Empty(page.Nodes);
        Assert.Equal(0, broker.ActiveCount);
    }

    /// <summary>Pending topology не запускает поиск; исчезновение уже захваченной публикации отзывает partial путь.</summary>
    [Fact]
    public void PendingTopologyWaitsBeforeCaptureAndRevokesPublishedPartialPath()
    {
        GraphSnapshot? graph = null;
        using var broker = new EntityNavigationBroker(() => graph, "binding", new(PageRows: 1));
        broker.Submit(Request(allowed: ["B", "D"]));
        Assert.Equal(0, broker.Advance(100));
        graph = Graph([Node("A"), Node("B"), Node("D")], [("A", "B"), ("B", "D")]);
        broker.Advance(100);
        Assert.True(broker.TryDequeueEvent(out var partial));
        Assert.Equal("page", partial!.Code);
        graph = null;
        broker.Advance(1);
        Assert.True(broker.TryDequeueEvent(out var terminal));
        Assert.Equal("navigation_changed", terminal!.Code);
        Assert.True(terminal.Complete);
        Assert.Empty(terminal.Nodes);
    }

    /// <summary>Отмена заменяет готовую непрочитанную страницу одним terminal и освобождает slot при его handoff.</summary>
    [Fact]
    public void CancellationRevokesQueuedSuccessAndDoesNotAllowReplay()
    {
        var graph = Graph([Node("A"), Node("D")], [("A", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        var request = Request(allowed: ["D"]);
        broker.Submit(request);
        broker.Advance(100);
        Assert.True(broker.Cancel(request.Key).Accepted);
        Assert.Equal(1, broker.EventCount);
        Assert.True(broker.TryDequeueEvent(out var page));
        Assert.Equal("cancelled", page!.Code);
        Assert.Equal(AdmissionStatus.Duplicate, broker.Submit(request).Status);
        Assert.False(broker.TryDequeueEvent(out _));
    }

    /// <summary>Повтор ключа сравнивает actor generation/revision и ACL, а не только endpoints.</summary>
    [Fact]
    public void ReplayIdentityCoversAllAuthorityFields()
    {
        var graph = Graph([Node("A"), Node("D")], [("A", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding");
        var request = Request(allowed: ["D"]);
        broker.Submit(request);
        Assert.Equal(AdmissionStatus.Duplicate, broker.Submit(request).Status);
        Assert.Equal("request_conflict", broker.Submit(request with { ActorRevision = 20 }).Code);
        Assert.Equal("request_conflict", broker.Submit(request with { Actor = Actor with { Generation = 8 } }).Code);
        Assert.Equal("request_conflict", broker.Submit(request with { Access = request.Access with { NodeGrants = new([]) } }).Code);
        Finish(broker);
        Assert.Equal(AdmissionStatus.Duplicate, broker.Submit(request).Status);
    }

    /// <summary>Вытеснение terminal history не снимает producer watermark.</summary>
    [Fact]
    public void HistoryEvictionDoesNotAllowAnOldRequestToStartAgain()
    {
        var graph = Graph([Node("A")], []);
        using var broker = new EntityNavigationBroker(() => graph, "binding", new(HistoryCapacity: 1));
        var first = Request("A", sequence: 1);
        broker.Submit(first);
        Finish(broker);
        broker.Submit(Request("A", sequence: 2));
        Finish(broker);
        Assert.Equal(1, broker.HistoryCount);
        Assert.Equal("request_expired", broker.Submit(first).Code);
    }

    /// <summary>Backpressure сохраняет slot и head serial до terminal handoff.</summary>
    [Fact]
    public void QueryCapacityDoesNotConsumeBlockedSerialOrAllowOvertaking()
    {
        var graph = Graph([Node("A")], []);
        using var broker = new EntityNavigationBroker(() => graph, "binding", new(MaxQueries: 1));
        broker.Submit(Request("A", producer: "first"));
        var blocked = Request("A", sequence: 1, producer: "second");
        Assert.Equal(AdmissionStatus.WouldBlock, broker.Submit(blocked).Status);
        Assert.Equal("producer_backpressure", broker.Submit(blocked with { Key = new("second", 2) }).Code);
        broker.Advance(100);
        Assert.Equal(AdmissionStatus.WouldBlock, broker.Submit(blocked).Status);
        Assert.True(broker.TryDequeueEvent(out _));
        Assert.Equal(AdmissionStatus.Accepted, broker.Submit(blocked).Status);
    }

    /// <summary>Чужой output pressure не приводит к частично исполненной отмене.</summary>
    [Fact]
    public void CancellationCanRetryAfterForeignOutputPressure()
    {
        var graph = Graph([Node("A")], []);
        using var broker = new EntityNavigationBroker(() => graph, "binding", new(MaxEvents: 1));
        var first = Request("A", producer: "first");
        var second = Request("A", producer: "second");
        broker.Submit(first);
        broker.Submit(second);
        broker.Advance(100);
        Assert.True(broker.Cancel(second.Key).WouldBlock);
        Assert.True(broker.TryDequeueEvent(out _));
        Assert.True(broker.Cancel(second.Key).Accepted);
        Assert.True(broker.TryDequeueEvent(out var page));
        Assert.Equal(second.Key, page!.Key);
        Assert.Equal("cancelled", page.Code);
    }

    /// <summary>Локальный workspace bound не подменяется частичным успешным маршрутом.</summary>
    [Fact]
    public void WorkspaceCapacityFailsClosed()
    {
        var graph = Graph([Node("A"), Node("B"), Node("D")], [("A", "B"), ("B", "D")]);
        using var broker = new EntityNavigationBroker(() => graph, "binding", new(MaxNodes: 1));
        broker.Submit(Request(allowed: ["B", "D"]));
        Assert.Equal("navigation_capacity", Assert.Single(Finish(broker)).Code);
    }

    /// <summary>Shape validation одинаково отбрасывает нулевые identities/revisions и malformed ACL.</summary>
    [Fact]
    public void InvalidNativeIdentityNeverStartsWork()
    {
        var request = Request(allowed: ["D"]);
        Assert.False(EntityNavigationBroker.IsValidRequest(request with { ActorRevision = 0 }));
        Assert.False(EntityNavigationBroker.IsValidRequest(request with { Actor = new("actor", 0) }));
        Assert.False(EntityNavigationBroker.IsValidRequest(request with
        {
            Access = request.Access with
            {
                Nodes = new([new NodeAccessPolicy(new("D", 0), null, 0)])
            }
        }));
        Assert.Null(EntityNavigationBroker.Fingerprint(null));
        Assert.Null(EntityNavigationBroker.Fingerprint(request with { ActorRevision = 0 }));
        Assert.True(EntityNavigationBroker.IsValidRequest(request));
    }

    /// <summary>Dispose старого binding освобождает события; новый broker не видит прежних requests.</summary>
    [Fact]
    public void RebindCannotReturnOldBindingEvents()
    {
        var graph = Graph([Node("A")], []);
        var old = new EntityNavigationBroker(() => graph, "old");
        old.Submit(Request("A"));
        old.Advance(100);
        old.Dispose();
        Assert.Equal(0, old.EventCount);
        Assert.Throws<ObjectDisposedException>(() => old.TryDequeueEvent(out _));
        using var current = new EntityNavigationBroker(() => graph, "new");
        current.Submit(Request("A"));
        Assert.Equal("new", Assert.Single(Finish(current)).BindingEpoch);
    }

    /// <summary>Тестовый полный policy snapshot с raw ID facts; candidate не получает unsigned USER по умолчанию.</summary>
    private static EntityAccessSnapshot PolicySnapshot(ValueList<int>? codes = null)
    {
        var network = new NetworkKey("lan", 1);
        return new(new("session", "principal", codes ?? new([])),
            new([new(Key("A"), network, 0, new([99])), new(Key("D"), network, 3, new([42]))]),
            new([new(network, "epoch", new([5]))]), new([]), new([]), new([]));
    }

    /// <summary>Готовит pure authority только через bounded public Advance.</summary>
    private static EntityAccessAuthority Resolve(EntityAccessSnapshot snapshot)
    {
        var authority = new EntityAccessAuthority(snapshot);
        Assert.False(authority.CanTraverse(Key("D"), Key("A")));
        for (var i = 0; i < 1000 && !authority.Ready && authority.ErrorCode is null; i++)
            Assert.Equal(1, authority.Advance(1));
        Assert.Null(authority.ErrorCode);
        Assert.True(authority.Ready);
        return authority;
    }

    /// <summary>ID guest не превращается в acquired GUEST/key для перемещения.</summary>
    [Fact]
    public void NativeGuestIdAllowsReadingButDoesNotAllowTraversal()
    {
        var snapshot = PolicySnapshot(new([5]));
        var authority = Resolve(snapshot);
        Assert.Equal(AccessLevel.Guest, authority.LegitNetworkLevel(snapshot.Networks[0].Key));
        Assert.Equal(AccessLevel.Guest, authority.NodeLevel(Key("D"), Key("A")));
        Assert.False(authority.CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Captain/Network даёт ROOT только после успешной проверки native USER требований узла.</summary>
    [Theory]
    [InlineData(20, false, AccessLevel.Guest, false)]
    [InlineData(67, false, AccessLevel.Guest, false)]
    [InlineData(20, true, AccessLevel.Root, true)]
    [InlineData(67, true, AccessLevel.Root, true)]
    [InlineData(9, true, AccessLevel.User, true)]
    public void RootIdsDoNotBypassUnmatchedMachineRequirements(int code, bool matches, AccessLevel expected, bool traversal)
    {
        var snapshot = PolicySnapshot(new(matches ? [code, 42] : [code]));
        var authority = Resolve(snapshot);
        Assert.Equal(expected, authority.NodeLevel(Key("D"), Key("A")));
        Assert.Equal(traversal, authority.CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Явный пустой required_access ANY сохраняет отличие от native obj requirement fallback.</summary>
    [Fact]
    public void EmptyRequiredAccessDoesNotBecomeObjectAccessFallback()
    {
        var snapshot = PolicySnapshot(new([42]));
        var target = snapshot.Nodes[1] with { RequiredAccess = new([]), RequireAll = new([42]) };
        var authority = Resolve(snapshot with { Nodes = new([snapshot.Nodes[0], target]) });
        Assert.Equal(AccessLevel.Guest, authority.NodeLevel(Key("D"), Key("A")));
        Assert.False(authority.CanTraverse(Key("D"), Key("A")));
        authority = Resolve(snapshot with { Nodes = new([snapshot.Nodes[0], target with { RequiredAccess = null }]) });
        Assert.Equal(AccessLevel.User, authority.NodeLevel(Key("D"), Key("A")));
    }

    /// <summary>Объектные req_access ALL и req_one_access ANY проверяются совместно; пустой native ID не становится USER.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ObjectRequirementsNeedAllAndOneAndNonemptyCredentials(bool all, bool one)
    {
        var snapshot = PolicySnapshot(new(all ? one ? [11, 12, 13] : [11, 12] : [13]));
        var target = snapshot.Nodes[1] with { RequiredAccess = null, RequireAll = new([11, 12]), RequireAny = new([13, 14]) };
        var authority = Resolve(snapshot with { Nodes = new([snapshot.Nodes[0], target]) });
        Assert.Equal(all && one, authority.CanTraverse(Key("D"), Key("A")));
        authority = Resolve(snapshot with
        {
            Principal = snapshot.Principal with { AccessCodes = new([]) },
            Nodes = new([snapshot.Nodes[0], target with { RequireAll = new([]), RequireAny = new([]) }])
        });
        Assert.False(authority.CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Обычный ключ и приобретённый LAN GUEST дают travel, не USER машин.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryKeyAndAcquiredGuestPermitTraversalWithoutMachineUser(bool ordinaryKey)
    {
        var snapshot = PolicySnapshot();
        snapshot = ordinaryKey ? snapshot with { NetworkKeys = new([new("lan", "epoch")]) } :
            snapshot with { NetworkGrants = new([new(new("lan", 1), AccessLevel.Guest, "epoch")]) };
        var authority = Resolve(snapshot);
        Assert.True(authority.CanTraverse(Key("D"), Key("A")));
        Assert.Equal(AccessLevel.Guest, authority.NodeLevel(Key("D"), Key("A")));
    }

    /// <summary>Все scope stamps проверяются в Core: rotation, node epoch, migration и replacement отзывают grants.</summary>
    [Theory]
    [InlineData("valid", true)]
    [InlineData("node_epoch", false)]
    [InlineData("network_epoch", false)]
    [InlineData("network_generation", false)]
    [InlineData("node_generation", false)]
    public void SignedNodeGrantCannotBeLaunderedIntoChangedAuthorityDomain(string change, bool permitted)
    {
        var snapshot = PolicySnapshot();
        snapshot = snapshot with { NodeGrants = new([new(Key("D"), AccessLevel.User, new("lan", 1), "epoch", 3)]) };
        if (change == "node_epoch") snapshot = snapshot with { Nodes = new([snapshot.Nodes[0], snapshot.Nodes[1] with { KeyEpoch = 4 }]) };
        if (change == "network_epoch") snapshot = snapshot with { Networks = new([snapshot.Networks[0] with { KeyEpoch = "rotated" }]) };
        if (change == "network_generation") snapshot = snapshot with
        {
            Nodes = new(snapshot.Nodes.Select(node => node with { Network = new("lan", 2) })),
            Networks = new([snapshot.Networks[0] with { Key = new("lan", 2) }])
        };
        if (change == "node_generation") snapshot = snapshot with { NodeGrants = new([snapshot.NodeGrants[0] with { Node = Key("D", 2) }]) };
        Assert.Equal(permitted, Resolve(snapshot).CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Снятие реального credential файла удаляет authority из нового snapshot; прошлый file scope не сливается в direct grants.</summary>
    [Fact]
    public void RemovingCredentialSourceRevokesImportedGrant()
    {
        var snapshot = PolicySnapshot() with { NodeGrants = new([new(Key("D"), AccessLevel.User, new("lan", 1), "epoch", 3)]) };
        Assert.True(Resolve(snapshot).CanTraverse(Key("D"), Key("A")));
        Assert.False(Resolve(snapshot with { NodeGrants = new([]) }).CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Враждебные врата блокируют LAN key/acquired GUEST, однако настоящий USER узла их проходит.</summary>
    [Fact]
    public void FortressGateDoesNotAcceptAnOrdinaryLanKeyAsMachineUser()
    {
        var snapshot = PolicySnapshot();
        snapshot = snapshot with
        {
            NetworkKeys = new([new("lan", "epoch")]),
            Nodes = new([snapshot.Nodes[0], snapshot.Nodes[1] with { Fortress = new(true, "owner:other") }])
        };
        Assert.False(Resolve(snapshot).CanTraverse(Key("D"), Key("A")));
        Assert.True(Resolve(snapshot with { NodeGrants = new([new(Key("D"), AccessLevel.User)]) }).CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Факты личной фракции, native ownership и sanctioned station process сохраняют существующий gate friendship.</summary>
    [Theory]
    [InlineData("personal")]
    [InlineData("session")]
    [InlineData("principal")]
    [InlineData("master")]
    [InlineData("station")]
    [InlineData("rogue")]
    public void FortressFriendshipUsesRawPrincipalFacts(string relation)
    {
        var snapshot = PolicySnapshot();
        var principal = snapshot.Principal;
        var fortress = new FortressAccessPolicy(true, "owner");
        switch (relation)
        {
            case "personal": principal = principal with { PersonalFactionId = "owner:mine" }; fortress = fortress with { FactionId = "owner:mine" }; break;
            case "session": fortress = fortress with { OwnerSessionId = principal.SessionId }; break;
            case "principal": fortress = fortress with { OwnerPrincipalId = principal.PrincipalId }; break;
            case "master": principal = principal with { MasterSessionId = "master" }; fortress = fortress with { OwnerSessionId = "master" }; break;
            case "station": principal = principal with { Sanctioned = true }; fortress = fortress with { FactionId = "station" }; break;
            case "rogue": principal = principal with { FactionId = "rogue" }; fortress = fortress with { FactionId = "rogue" }; break;
        }
        snapshot = snapshot with
        {
            Principal = principal,
            NetworkKeys = new([new("lan", "epoch")]),
            Nodes = new([snapshot.Nodes[0], snapshot.Nodes[1] with { Fortress = fortress }])
        };
        Assert.True(Resolve(snapshot).CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Личный owner scope не принимает унаследованный session/ckey от другой personal faction.</summary>
    [Fact]
    public void PersonalFortressOwnerDoesNotFallBackToReusedConsoleSession()
    {
        var snapshot = PolicySnapshot();
        snapshot = snapshot with
        {
            Principal = snapshot.Principal with { PersonalFactionId = "owner:new" },
            NetworkKeys = new([new("lan", "epoch")]),
            Nodes = new([snapshot.Nodes[0], snapshot.Nodes[1] with {
                Fortress = new(true, "owner", OwnerFactionId: "owner:old", OwnerSessionId: "session") }])
        };
        Assert.False(Resolve(snapshot).CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Native device ownership даёт ROOT лишь самому устройству и не открывает его ЛВС целиком.</summary>
    [Fact]
    public void NativeDeviceOwnerDoesNotGainUnrelatedLanTraversal()
    {
        var snapshot = PolicySnapshot();
        snapshot = snapshot with
        {
            Principal = snapshot.Principal with { NativeOwnerId = "body" },
            Nodes = new([snapshot.Nodes[0] with { NativeOwners = new(["body"]) }, snapshot.Nodes[1]])
        };
        var authority = Resolve(snapshot);
        Assert.Equal(AccessLevel.Root, authority.NodeLevel(Key("A"), Key("A")));
        Assert.False(authority.CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Locked native principal не планирует hops даже с действующими acquired USER и ключами.</summary>
    [Fact]
    public void LockedPrincipalFailsClosed()
    {
        var snapshot = Access(["D"]);
        Assert.False(Resolve(snapshot with { Principal = snapshot.Principal with { Locked = true } }).CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Конфликт raw policy одной identity не выбирается по порядку сериализации.</summary>
    [Fact]
    public void ConflictingPoliciesRejectWholeAuthoritySnapshot()
    {
        var snapshot = PolicySnapshot();
        snapshot = snapshot with { Nodes = new([snapshot.Nodes[0], snapshot.Nodes[1], snapshot.Nodes[1] with { KeyEpoch = 4 }]) };
        var authority = new EntityAccessAuthority(snapshot);
        authority.Advance(100);
        Assert.False(authority.Ready);
        Assert.Equal("access_policy_conflict", authority.ErrorCode);
        Assert.False(authority.CanTraverse(Key("D"), Key("A")));
    }

    /// <summary>Fingerprint authority учитывает каждый stamp, requirement и principal field; partial signed scope недопустим.</summary>
    [Fact]
    public void AuthorityFingerprintIncludesRevocationAndNullRequirementSemantics()
    {
        var snapshot = PolicySnapshot();
        var fingerprint = EntityAccessAuthority.Fingerprint(snapshot);
        Assert.NotNull(fingerprint);
        Assert.NotEqual(fingerprint, EntityAccessAuthority.Fingerprint(snapshot with { Networks = new([snapshot.Networks[0] with { KeyEpoch = "other" }]) }));
        Assert.NotEqual(fingerprint, EntityAccessAuthority.Fingerprint(snapshot with { Nodes = new([snapshot.Nodes[0], snapshot.Nodes[1] with { RequiredAccess = null }]) }));
        Assert.NotEqual(fingerprint, EntityAccessAuthority.Fingerprint(snapshot with { Principal = snapshot.Principal with { Sanctioned = true } }));
        Assert.Null(EntityAccessAuthority.Fingerprint(snapshot with { NodeGrants = new([new(Key("D"), AccessLevel.User, new("lan", 1))]) }));
    }

    /// <summary>Native policy прежней LAN не подменяет новый derived binding, уже опубликованный Core.</summary>
    [Fact]
    public void CoreGraphOwnsAuthorityDomainBinding()
    {
        var snapshot = PolicySnapshot();
        var authority = new EntityAccessAuthority(snapshot, _ => "different-lan");
        authority.Advance(100);
        Assert.Equal("access_domain_changed", authority.ErrorCode);
        Assert.False(authority.Ready);
        Assert.False(authority.CanTraverse(Key("D"), Key("A")));
        authority = new EntityAccessAuthority(snapshot, _ => "lan");
        authority.Advance(100);
        Assert.True(authority.Ready);
    }

    /// <summary>Явный изменяемый fixture overlay; производственный broker видит только INetworkAvailability.</summary>
    private sealed class Availability(GraphSnapshot graph) : INetworkAvailability
    {
        /// <summary>Проверяемая authoritative overlay revision.</summary>
        public ulong Revision { get; set; } = 1;
        /// <summary>Явное разрешение quantum переходов.</summary>
        public bool BackboneEnabled { get; set; } = true;
        /// <summary>Exact generation states тестового мира.</summary>
        public Dictionary<NodeKey, NodeAvailability> Nodes { get; } = graph.Nodes.ToDictionary(pair => pair.Key,
            pair => new NodeAvailability(pair.Value.State.Online));
        /// <inheritdoc />
        public NodeAvailability Get(NodeKey key) => Nodes.GetValueOrDefault(key, new(false, false, false));
    }
}
