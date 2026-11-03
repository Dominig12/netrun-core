using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Communication;

/// <summary>Native telecomms роль; не определяется общим node_type server/gateway.</summary>
public enum CommunicationPortKind
{
    /// <summary>Hub принимает relay receiving и отправляет через relay broadcasting.</summary>
    Hub,
    /// <summary>Relay независимо разрешает входящее receiving и исходящее broadcasting направления.</summary>
    Relay
}

/// <summary>Сырые native-факты одного communication port без рассчитанных путей или meta-graph edges.</summary>
/// <param name="Owner">Точный физический экземпляр.</param>
/// <param name="Kind">Native hub либо relay.</param>
/// <param name="On">Native машина включена.</param>
/// <param name="Toggled">Native программный переключатель включён.</param>
/// <param name="Operational">Native hardware допускает обработку.</param>
/// <param name="NativeZ">Именно object.z, 0..4096; не координата физического носителя.</param>
/// <param name="LongRange">Разрешает configured link между различными NativeZ хотя бы одного конца.</param>
/// <param name="Receiving">У relay разрешено направление relay → hub.</param>
/// <param name="Broadcasting">У relay разрешено направление hub → relay.</param>
/// <param name="ListeningFrequencies">Положительные int frequencies; пустое множество означает отсутствие фильтра.</param>
/// <param name="ConfiguredDirectedLinks">Точные поколения назначений native from.links; обратная связь не подразумевается.</param>
public sealed record CommunicationPortFact(NodeKey Owner, CommunicationPortKind Kind, bool On, bool Toggled,
    bool Operational, int NativeZ, bool LongRange, bool Receiving, bool Broadcasting,
    ValueList<int> ListeningFrequencies, ValueList<NodeKey> ConfiguredDirectedLinks);

/// <summary>Неизменяемый port catalog reader; старые snapshots не меняются при Upsert/Remove.</summary>
public sealed class CommunicationPortSnapshot
{
    /// <summary>Версия всех native link/switch/filter фактов этого reader.</summary>
    public ulong Revision { get; }
    /// <summary>Собственная read-only копия current generations.</summary>
    public IReadOnlyDictionary<NodeKey, CommunicationPortFact> Ports { get; }

    /// <summary>Копирует bounded индекс, не отдавая backing Dictionary.</summary>
    internal CommunicationPortSnapshot(ulong revision, IDictionary<NodeKey, CommunicationPortFact> ports)
    {
        Revision = revision;
        Ports = new System.Collections.ObjectModel.ReadOnlyDictionary<NodeKey, CommunicationPortFact>(
            new Dictionary<NodeKey, CommunicationPortFact>(ports));
    }
}

/// <summary>Ограниченные политики augmented поиска; исходный физический graph остаётся неизменным.</summary>
/// <param name="NativeLatencyDs">Положительная конечная стоимость native bridge в децисекундах.</param>
/// <param name="CacheCapacity">Общий LRU limit положительных и отрицательных overlay результатов.</param>
/// <param name="MaxNodes">Максимальный исходный physical graph для одного поиска.</param>
/// <param name="MaxQueueEntries">Предел одновременно ожидающих augmented queue candidates.</param>
public sealed record CommunicationOptions(double NativeLatencyDs = 3, int CacheCapacity = 256,
    int MaxNodes = int.MaxValue, int MaxQueueEntries = int.MaxValue);

/// <summary>Явный opt-in communication channel; общий NetworkRouter этой capability не получает.</summary>
public interface ICommunicationRouteProvider
{
    /// <summary>Проверяет оба service family и точные owner поколения; обычный Machine маршрут всегда имеет приоритет.</summary>
    RouteResult FindRoute(GraphSnapshot graph, ServiceKey source, ServiceKey destination, int? frequency,
        INetworkWorldView world);
}

/// <summary>Безопасный отказ native facts, конфигурации или пределов catalog.</summary>
/// <param name="code">Стабильный код без входных значений и link lists.</param>
public sealed class CommunicationException(string code) : Exception(code)
{
    /// <summary>Причина отказа для диагностики или typed caller.</summary>
    public string Code { get; } = code;
}
