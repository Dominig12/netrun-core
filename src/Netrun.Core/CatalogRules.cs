using Netrun.Core.Communication;

namespace Netrun.Core;

/// <summary>Инварианты секторного и сервисного модулей; ссылки на мир проверяются потребителями по поколениям.</summary>
internal static class CatalogRules
{
    /// <summary>Полная запись сектора сохраняет исходный порядок уникальных Z.</summary>
    internal static bool ValidSector(SectorFact? sector) => sector is not null && ValidIdentity(sector.SectorId, sector.Generation, sector.Revision) &&
        sector.Levels is { Count: > 0 and <= 4096 } && sector.Levels.All(z => z is >= 1 and <= 4096) &&
        sector.Levels.Distinct().Count() == sector.Levels.Count;

    /// <summary>Объявление сервиса ограничивает allowlist; opaque family/tag не дают прав на native-операции.</summary>
    internal static bool ValidService(ServiceFact? service) => service is { Owner: not null } &&
        ValidIdentity(service.ServiceId, service.Generation, service.Revision) &&
        NodeFactRules.IsValidText(service.Owner.NodeId, 128) && service.Owner.Generation > 0 &&
        NodeFactRules.IsValidText(service.Family, 128) && (service.Tag is null || NodeFactRules.IsValidText(service.Tag, 256)) &&
        service.Operations is { Count: > 0 and <= 64 } && service.Operations.All(operation => operation is not null &&
            NodeFactRules.IsValidText(operation.OperationId, 128) && Enum.IsDefined(operation.Safety) && Enum.IsDefined(operation.Delivery)) &&
        service.Operations.Select(operation => operation.OperationId).Distinct(StringComparer.Ordinal).Count() == service.Operations.Count &&
        (service.Communication is null || service.Communication.Owner == service.Owner && CommunicationPortCatalog.IsValid(service.Communication));

    /// <summary>Общее правило адресуемого факта и удаления.</summary>
    internal static bool ValidIdentity(string? id, ulong generation, ulong revision) =>
        NodeFactRules.IsValidText(id, 128) && generation > 0 && revision > 0;

    /// <summary>Проверяет один штатный вид catalog change, не смешивая его с изменениями узлов.</summary>
    internal static bool ValidChange(WorldChange change) => change switch
    {
        SectorUpsert upsert => ValidSector(upsert.Sector),
        SectorRemove remove => ValidIdentity(remove.SectorId, remove.Generation, remove.Revision),
        ServiceUpsert upsert => ValidService(upsert.Service),
        ServiceRemove remove => ValidIdentity(remove.ServiceId, remove.Generation, remove.Revision),
        _ => false
    };
}
