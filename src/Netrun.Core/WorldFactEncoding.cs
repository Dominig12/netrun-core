namespace Netrun.Core;

/// <summary>Однозначное внутреннее кодирование фактов для semantic replay; не межъязыковой wire checksum.</summary>
internal static class WorldFactEncoding
{
    /// <summary>Кодирует узловое намерение, включая nullable geometry и native metadata.</summary>
    internal static void WriteNode(BinaryWriter writer, NodeMutation mutation)
    {
        writer.Write(mutation.Fact is null ? (byte)2 : (byte)1);
        writer.Write(mutation.NodeId);
        writer.Write(mutation.Generation);
        writer.Write(mutation.Revision);
        if (mutation.Fact is not { State: var state })
            return;
        writer.Write(state.Address);
        writer.Write(state.NodeType);
        writer.Write(state.Position is not null);
        if (state.Position is { } position)
        {
            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(position.Z);
        }
        WriteOptional(writer, state.SectorId);
        writer.Write(state.Online);
        writer.Write(state.Network is not null);
        if (state.Network is not { } network)
            return;
        WriteOptional(writer, network.BaseAreaId);
        WriteOptional(writer, network.SuppliedAreaId);
        writer.Write(network.Department);
        writer.Write(network.IsNtnetRelay);
        writer.Write(network.RelayOperational);
        writer.Write(network.IsStationLevel);
        writer.Write(network.IsMiningLevel);
        writer.Write(network.InitialAnchor is not null);
        if (network.InitialAnchor is { } anchor)
        {
            writer.Write(anchor.NodeId);
            writer.Write(anchor.Generation);
        }
    }

    /// <summary>Кодирует один заранее проверенный catalog change с отдельными тегами модулей.</summary>
    internal static void WriteCatalog(BinaryWriter writer, WorldChange change)
    {
        switch (change)
        {
            case SectorUpsert upsert:
                var sector = upsert.Sector;
                WriteIdentity(writer, 8, sector.SectorId, sector.Generation, sector.Revision);
                writer.Write(sector.Levels.Count);
                foreach (var z in sector.Levels)
                    writer.Write(z);
                break;
            case SectorRemove remove:
                WriteIdentity(writer, 9, remove.SectorId, remove.Generation, remove.Revision);
                break;
            case ServiceUpsert upsert:
                var service = upsert.Service;
                WriteIdentity(writer, 10, service.ServiceId, service.Generation, service.Revision);
                writer.Write(service.Owner.NodeId);
                writer.Write(service.Owner.Generation);
                writer.Write(service.Family);
                WriteOptional(writer, service.Tag);
                writer.Write(service.Online);
                writer.Write(service.Operations.Count);
                foreach (var operation in service.Operations)
                {
                    writer.Write(operation.OperationId);
                    writer.Write((byte)operation.Safety);
                    writer.Write((byte)operation.Delivery);
                    writer.Write(operation.Ordered);
                }
                writer.Write(service.Communication is not null);
                if (service.Communication is { } port)
                {
                    writer.Write((byte)port.Kind);
                    writer.Write(port.On);
                    writer.Write(port.Toggled);
                    writer.Write(port.Operational);
                    writer.Write(port.NativeZ);
                    writer.Write(port.LongRange);
                    writer.Write(port.Receiving);
                    writer.Write(port.Broadcasting);
                    writer.Write(port.ListeningFrequencies.Count);
                    foreach (var frequency in port.ListeningFrequencies.Order())
                        writer.Write(frequency);
                    writer.Write(port.ConfiguredDirectedLinks.Count);
                    foreach (var link in port.ConfiguredDirectedLinks.OrderBy(link => link.NodeId, StringComparer.Ordinal).ThenBy(link => link.Generation))
                    {
                        writer.Write(link.NodeId);
                        writer.Write(link.Generation);
                    }
                }
                break;
            case ServiceRemove remove:
                WriteIdentity(writer, 11, remove.ServiceId, remove.Generation, remove.Revision);
                break;
            default:
                throw new InvalidOperationException("Unsupported catalog change.");
        }
    }

    /// <summary>Кодирует nullable string с отдельным presence bit; null отличается от пустой строки.</summary>
    private static void WriteOptional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }

    /// <summary>Общий заголовок полного факта/удаления.</summary>
    private static void WriteIdentity(BinaryWriter writer, byte kind, string id, ulong generation, ulong revision)
    {
        writer.Write(kind);
        writer.Write(id);
        writer.Write(generation);
        writer.Write(revision);
    }
}
