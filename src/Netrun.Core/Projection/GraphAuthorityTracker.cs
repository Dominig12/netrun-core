using Netrun.Core.Routing;

namespace Netrun.Core.Projection;

/// <summary>O(touched facts) authority ledger; владелец вызывает Observe после атомарного accepted world batch.</summary>
public sealed class GraphAuthorityTracker : IGraphAuthority
{
    /// <summary>Current authoritative revisions читаются из mirror, не из давно построенного structural graph.</summary>
    private readonly INetworkWorldView _world;
    /// <summary>Тот же default backbone, что у NetworkAvailability.</summary>
    private bool _backboneEnabled = true;
    /// <inheritdoc />
    public ulong Revision { get; private set; }
    /// <inheritdoc />
    public ulong InfrastructureStamp { get; private set; }
    /// <inheritdoc />
    public ulong AvailabilityInfrastructureStamp { get; private set; }

    /// <summary>Получает read-only live reader без initial scan и без собственной копии всех facts.</summary>
    public GraphAuthorityTracker(INetworkWorldView world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
    }

    /// <summary>Initial snapshot/publication отзывает все старые proofs, не переиспользуя counter в этом binding.</summary>
    public void Reset()
    {
        Ensure(Revision);
        Ensure(InfrastructureStamp);
        Ensure(AvailabilityInfrastructureStamp);
        Revision++;
        InfrastructureStamp++;
        AvailabilityInfrastructureStamp++;
    }

    /// <summary>Любая новая exact fact revision обновляет endpoint proof; только реальные forwarders меняют global stamps.</summary>
    public void ObserveNodeChange(NodeFact? before, NodeFact? after)
    {
        if (before == after) return;
        var infrastructure = IsInfrastructure(before) || IsInfrastructure(after);
        var identityChanged = before is null || after is null || before.NodeId != after.NodeId || before.Generation != after.Generation;
        var structural = infrastructure && (identityChanged || StructuralChange(before!.State, after!.State));
        var availability = infrastructure && (identityChanged || before!.State.Online != after!.State.Online ||
            (before.State.Network?.RelayOperational ?? true) != (after.State.Network?.RelayOperational ?? true));
        Ensure(Revision);
        if (structural) Ensure(InfrastructureStamp);
        if (availability) Ensure(AvailabilityInfrastructureStamp);
        Revision++;
        if (structural) InfrastructureStamp++;
        if (availability) AvailabilityInfrastructureStamp++;
    }

    /// <summary>Ordered sector geometry влияет на physical paths; одна revision с прежними levels proof не отзывает.</summary>
    public void ObserveSectorChange(SectorFact? before, SectorFact? after)
    {
        if (before is null && after is null || before is not null && after is not null &&
            before.SectorId == after.SectorId && before.Levels.Equals(after.Levels)) return;
        Ensure(Revision);
        Ensure(InfrastructureStamp);
        Revision++;
        InfrastructureStamp++;
    }

    /// <summary>Виртуальный relay forwarding меняется без structural rebuild, но отзывает transit authority.</summary>
    public void SetBackbone(bool enabled)
    {
        if (_backboneEnabled == enabled) return;
        Ensure(Revision);
        Ensure(AvailabilityInfrastructureStamp);
        _backboneEnabled = enabled;
        Revision++;
        AvailabilityInfrastructureStamp++;
    }

    /// <inheritdoc />
    public ulong NodeRevision(NodeKey key) => key is { Generation: > 0, NodeId: not null } && _world.Nodes.TryGetValue(key.NodeId, out var node) &&
        node.Generation == key.Generation ? node.Revision : 0;

    /// <inheritdoc />
    public bool IsEndpoint(NodeKey key) => key is { Generation: > 0, NodeId: not null } && _world.Nodes.TryGetValue(key.NodeId, out var node) &&
        node.Generation == key.Generation && !IsInfrastructure(node);

    /// <summary>APC mesh и настоящие NTnet relays — единственные internal forwarding роли physical builder.</summary>
    private static bool IsInfrastructure(NodeFact? node) => node is not null &&
        (node.State.NodeType == "apc" || node.State.Network?.IsNtnetRelay == true);

    /// <summary>Сравнивает topology-relevant поля, отдельно от online/RelayOperational overlay и одной fact revision.</summary>
    private static bool StructuralChange(NodeState before, NodeState after)
    {
        var left = before.Network;
        var right = after.Network;
        return before.Address != after.Address || before.NodeType != after.NodeType || before.Position != after.Position ||
            before.SectorId != after.SectorId || left?.BaseAreaId != right?.BaseAreaId || left?.SuppliedAreaId != right?.SuppliedAreaId ||
            (left?.Department ?? "general") != (right?.Department ?? "general") ||
            (left?.IsNtnetRelay ?? false) != (right?.IsNtnetRelay ?? false) ||
            (left?.IsStationLevel ?? true) != (right?.IsStationLevel ?? true) ||
            (left?.IsMiningLevel ?? false) != (right?.IsMiningLevel ?? false) || left?.InitialAnchor != right?.InitialAnchor;
    }

    /// <summary>Исчерпание версии обнаруживается до изменения stamps; wrap не возвращает старую authority.</summary>
    private static void Ensure(ulong value)
    {
        if (value == ulong.MaxValue) throw new RoutingException("authority_version_exhausted");
    }
}
