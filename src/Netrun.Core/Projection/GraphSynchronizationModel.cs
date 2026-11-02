using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Projection;

/// <summary>Proof обычной route authority: инфраструктура отделена от revision отдельных degree-one endpoints.</summary>
public interface IGraphAuthority
{
    /// <summary>Любое изменение read model, включая online-only fact revision; служит O(1) проверке idle stream.</summary>
    ulong Revision { get; }
    /// <summary>APC/real-relay structure и ordered sectors; unrelated ordinary leaf movement не меняет stamp.</summary>
    ulong InfrastructureStamp { get; }
    /// <summary>APC/real-relay online/operational и backbone; ordinary leaf availability не меняет stamp.</summary>
    ulong AvailabilityInfrastructureStamp { get; }
    /// <summary>Последняя world fact revision exact generation, отдельно от structural GraphSnapshot; ноль означает отсутствие.</summary>
    ulong NodeRevision(NodeKey key);
    /// <summary>C# builder proof: non-APC/non-relay имеет degree не выше одного и не является internal forwarder.</summary>
    bool IsEndpoint(NodeKey key);
}

/// <summary>Snapshot либо последующее изменение уже подтверждённого baseline.</summary>
public enum GraphSynchronizationPhase
{
    /// <summary>Первый полный historical baseline; последующие leaf moves не перезапускают его.</summary>
    Snapshot,
    /// <summary>Только net changes относительно native-ACKed baseline.</summary>
    Delta
}

/// <summary>Закрытая typed graph mutation без whole world facts или native references.</summary>
public abstract record GraphSynchronizationOperation;
/// <summary>Полное structural представление exact node с captured authoritative world revision.</summary>
/// <param name="Node">Address/binding/LAN/relay значения frozen target.</param>
/// <param name="NodeRevision">Положительная revision current world fact в момент target capture.</param>
/// <param name="IsEndpoint">Явный C# proof обычного degree-one nonforwarder.</param>
public sealed record GraphSynchronizationNodeUpsert(GraphNodeRow Node, ulong NodeRevision, bool IsEndpoint) : GraphSynchronizationOperation;
/// <summary>Удаление exact generation, включая уже исчезнувшую native weak-reference.</summary>
/// <param name="Node">Отзываемая identity.</param>
public sealed record GraphSynchronizationNodeRemove(NodeKey Node) : GraphSynchronizationOperation;
/// <summary>Полный physical edge; stable ID не меняется при переименовании адресов или orientation payload.</summary>
/// <param name="EdgeId">SHA-256 canonical typed NodeKey pair и link kind, 64 lowercase hex.</param>
/// <param name="Edge">Physical geometry/cost frozen target.</param>
public sealed record GraphSynchronizationEdgeUpsert(string EdgeId, PhysicalEdge Edge) : GraphSynchronizationOperation;
/// <summary>Отзывает edge по opaque stable ID без сканирования native adjacency registry.</summary>
/// <param name="EdgeId">ID ранее переданного physical edge.</param>
public sealed record GraphSynchronizationEdgeRemove(string EdgeId) : GraphSynchronizationOperation;

/// <summary>Одна in-flight страница; явный Continue ACK означает завершение bounded native apply, не только чтение сокета.</summary>
public sealed record GraphSynchronizationPage : TransportEvent
{
    /// <summary>Положительный serial страницы stream; cancel/terminal может пропустить отозванную страницу.</summary>
    public ulong PageIndex { get; }
    /// <summary>Snapshot или delta относительно ACKed baseline.</summary>
    public GraphSynchronizationPhase Phase { get; }
    /// <summary>Graph version baseline в начале batch; ноль для первоначального snapshot.</summary>
    public ulong FromGraphVersion { get; }
    /// <summary>Frozen target structural version этого batch.</summary>
    public ulong GraphVersion { get; }
    /// <summary>Граница structural publication target.</summary>
    public ulong GraphInputSequence { get; }
    /// <summary>Frozen infrastructure proof, независимый от ordinary leaf churn.</summary>
    public ulong InfrastructureStamp { get; }
    /// <summary>Frozen transit availability proof.</summary>
    public ulong AvailabilityInfrastructureStamp { get; }
    /// <summary>Собственная immutable копия максимум 32 операций.</summary>
    public ValueList<GraphSynchronizationOperation> Operations { get; }
    /// <summary>Последняя страница текущего batch; successful batch не завершает persistent subscription.</summary>
    public bool BatchComplete { get; }
    /// <summary>page/completed/cancelled либо безопасная terminal причина; terminal содержит пустой Operations.</summary>
    public string Code { get; }

    /// <summary>Копирует ограниченный набор operations; страница не удерживает GraphSnapshot или cursor.</summary>
    public GraphSynchronizationPage(string bindingEpoch, RequestKey key, ulong pageIndex, GraphSynchronizationPhase phase,
        ulong fromGraphVersion, ulong graphVersion, ulong graphInputSequence, ulong infrastructureStamp,
        ulong availabilityInfrastructureStamp, IEnumerable<GraphSynchronizationOperation> operations, bool batchComplete, string code)
        : base(bindingEpoch, key)
    {
        PageIndex = pageIndex;
        Phase = phase;
        FromGraphVersion = fromGraphVersion;
        GraphVersion = graphVersion;
        GraphInputSequence = graphInputSequence;
        InfrastructureStamp = infrastructureStamp;
        AvailabilityInfrastructureStamp = availabilityInfrastructureStamp;
        Operations = new(operations, 32);
        BatchComplete = batchComplete;
        Code = code;
    }
}

/// <summary>Один stream удерживает один baseline и один frozen target, без graph history или unbounded journal.</summary>
/// <param name="MaxNodes">Baseline/target nodes, 1..10000 каждый.</param>
/// <param name="MaxEdges">Baseline/target edges, 0..100000 каждый.</param>
/// <param name="PageOperations">Операции одной страницы, 1..32.</param>
/// <param name="HistoryCapacity">Terminal statuses без retained graph/payload, 1..4096.</param>
/// <param name="MaxProducers">Не вытесняемые serial barriers за binding, 1..128.</param>
public sealed record GraphSynchronizationOptions(int MaxNodes = 10_000, int MaxEdges = 100_000,
    int PageOperations = 32, int HistoryCapacity = 256, int MaxProducers = 128);
