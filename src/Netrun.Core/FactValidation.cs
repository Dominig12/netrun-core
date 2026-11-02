namespace Netrun.Core;

/// <summary>Общая доменная валидация фактов для codec, снимков и модулей; не выполняет публикацию.</summary>
public static class FactValidation
{
    /// <summary>Проверяет полный штатный факт или удаление, включая nullable geometry и allowlist сервисов.</summary>
    public static bool IsValid(WorldChange? change) => change switch
    {
        NodeUpsert { Node: { } node } => NodeFactRules.IsValidMutation(new(node.NodeId, node.Generation, node.Revision, node)),
        NodeRemove remove => NodeFactRules.IsValidMutation(new(remove.NodeId, remove.Generation, remove.Revision, null)),
        SectorUpsert or SectorRemove or ServiceUpsert or ServiceRemove => CatalogRules.ValidChange(change),
        _ => false
    };

    /// <summary>Определяет необходимость полного checksum/схемы, не включая состояние control-протокола.</summary>
    public static bool RequiresExtendedFormat(WorldChange change) => change is SectorUpsert or SectorRemove or ServiceUpsert or ServiceRemove ||
        change is NodeUpsert { Node.State: { } state } && (state.Network is not null || state.Position is null || state.SectorId is null);

    /// <summary>Native port metadata не входит в frozen checksum v2; требует raw-wire checksum v3 и schema minor 3.</summary>
    public static bool RequiresChecksum3(WorldChange change) => change is ServiceUpsert { Service.Communication: not null };
}
