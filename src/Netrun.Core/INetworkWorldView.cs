namespace Netrun.Core;

/// <summary>Общая read-only граница доменных потребителей; mutable-хранилища и IPC не раскрываются.</summary>
public interface INetworkWorldView
{
    /// <summary>Текущие полные факты узлов, индексированные логическим ID.</summary>
    IReadOnlyDictionary<string, NodeFact> Nodes { get; }
    /// <summary>Текущие определения секторов и исходного порядка этажей.</summary>
    IReadOnlyDictionary<string, SectorFact> Sectors { get; }
    /// <summary>Объявления экземпляров сервисов; поколение owner проверяет потребитель.</summary>
    IReadOnlyDictionary<string, ServiceFact> Services { get; }
}
