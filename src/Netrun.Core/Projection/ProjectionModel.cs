using Netrun.Core.Routing;
using Netrun.Core.Topology;
using Netrun.Core.Transport;

namespace Netrun.Core.Projection;

/// <summary>Одноразовая read-only проекция для native UI; выдача страниц не создаёт сетевых эффектов.</summary>
public enum ProjectionKind
{
    /// <summary>Все узлы, затем физические кабели; implicit relay clique не материализуется.</summary>
    Graph,
    /// <summary>Все достижимые узлы одного полного source tree.</summary>
    Routes
}

/// <summary>Ограниченный запрос проекции; повтор того же ключа не перезапускает перечисление.</summary>
/// <param name="Key">Монотонный producer serial в текущем binding.</param>
/// <param name="Kind">Граф либо полное дерево маршрутов.</param>
/// <param name="Source">Точное поколение источника для Routes; Graph требует null.</param>
/// <param name="Mode">Политика доступности дерева; для Graph не влияет на строки, но входит в identity запроса.</param>
public sealed record ProjectionRequest(RequestKey Key, ProjectionKind Kind, NodeKey? Source = null,
    RouteKind Mode = RouteKind.Discovery);

/// <summary>Типизированная строка без native references, произвольных полей или полных world facts.</summary>
public abstract record ProjectionRow;

/// <summary>Структурный узел графа; native adapter разрешает Key в weak-ссылку на объект самостоятельно.</summary>
/// <param name="Key">Точное поколение физического узла.</param>
/// <param name="Address">Канонический сетевой адрес на границе GraphInputSequence.</param>
/// <param name="Anchor">Точный APC из binding либо null для узла без привязки.</param>
/// <param name="LanId">Производная LAN либо null для изолированного узла.</param>
/// <param name="IsRelay">Расположенный реальный NTnet relay, являющийся членом implicit bucket.</param>
public sealed record GraphNodeRow(NodeKey Key, string Address, NodeKey? Anchor, string? LanId, bool IsRelay) : ProjectionRow;

/// <summary>Один неизменяемый физический кабель; virtual quantum связи сюда не входят.</summary>
/// <param name="Edge">Канонические поколения endpoints и положительная задержка в децисекундах.</param>
public sealed record GraphEdgeRow(PhysicalEdge Edge) : ProjectionRow;

/// <summary>Один достижимый узел source tree; пути при выдаче таблицы не материализуются.</summary>
/// <param name="Key">Достижимое точное поколение.</param>
/// <param name="Previous">Предыдущее поколение на выбранном пути; null только у источника.</param>
/// <param name="LatencyDs">Накопленная конечная задержка в децисекундах.</param>
/// <param name="Quantum">Хотя бы один virtual relay hop на всём predecessor пути.</param>
/// <param name="NodeRevision">Captured authoritative world fact revision; ноль только в legacy режиме без authority provider.</param>
public sealed record RouteRow(NodeKey Key, NodeKey? Previous, double LatencyDs, bool Quantum, ulong NodeRevision = 0) : ProjectionRow;

/// <summary>Ограниченная immutable страница. Client публикует накопленную таблицу только после completed; иной terminal отзывает частичные данные.</summary>
public sealed record ProjectionPage : TransportEvent
{
    /// <summary>Тип таблицы, совпадающий с принятым запросом.</summary>
    public ProjectionKind Kind { get; }
    /// <summary>Версия структурного графа; ноль допустим для отказа до получения публикации.</summary>
    public ulong GraphVersion { get; }
    /// <summary>Граница world inputs захваченной публикации.</summary>
    public ulong GraphInputSequence { get; }
    /// <summary>Ревизия availability при начале; Routes требует её актуальности, Graph хранит лишь captured metadata.</summary>
    public ulong AvailabilityVersion { get; }
    /// <summary>Captured infrastructure proof; ноль в legacy режиме без authority provider.</summary>
    public ulong InfrastructureStamp { get; }
    /// <summary>Captured transit availability proof; unrelated ordinary endpoints его не меняют.</summary>
    public ulong AvailabilityInfrastructureStamp { get; }
    /// <summary>Captured source world revision для Routes; ноль для Graph/legacy.</summary>
    public ulong SourceNodeRevision { get; }
    /// <summary>Точный источник Routes либо null для Graph.</summary>
    public NodeKey? Source { get; }
    /// <summary>Положительный порядковый номер страницы данного запроса; terminal отказ может пропустить отозванные номера.</summary>
    public ulong PageIndex { get; }
    /// <summary>Собственная копия максимум 64 строк; стандартный broker публикует не более 32.</summary>
    public ValueList<ProjectionRow> Rows { get; }
    /// <summary>Последняя страница; только Code completed разрешает публикацию накопленной клиентской таблицы.</summary>
    public bool Complete { get; }
    /// <summary>page/completed/cancelled/projection_changed либо безопасный доменный отказ без входного содержимого.</summary>
    public string Code { get; }

    /// <summary>Защитно копирует bounded строки; страница не удерживает cursor, дерево или GraphSnapshot.</summary>
    public ProjectionPage(string bindingEpoch, RequestKey key, ProjectionKind kind, ulong graphVersion,
        ulong graphInputSequence, ulong availabilityVersion, NodeKey? source, ulong pageIndex,
        IEnumerable<ProjectionRow> rows, bool complete, string code, ulong infrastructureStamp = 0,
        ulong availabilityInfrastructureStamp = 0, ulong sourceNodeRevision = 0) : base(bindingEpoch, key)
    {
        Kind = kind;
        GraphVersion = graphVersion;
        GraphInputSequence = graphInputSequence;
        AvailabilityVersion = availabilityVersion;
        InfrastructureStamp = infrastructureStamp;
        AvailabilityInfrastructureStamp = availabilityInfrastructureStamp;
        SourceNodeRevision = sourceNodeRevision;
        Source = source;
        PageIndex = pageIndex;
        Rows = new(rows, 64);
        Complete = complete;
        Code = code;
    }
}

/// <summary>Абсолютные memory caps одноразового broker; объём работы одного slice ограничен PageRows.</summary>
/// <param name="MaxQueries">Активные slots вплоть до handoff terminal страницы, 1..16.</param>
/// <param name="PageRows">Строк на страницу и work unit, 1..64; по умолчанию 32 для native apply budget.</param>
/// <param name="MaxEvents">Максимум ещё не переданных страниц, 1..256.</param>
/// <param name="HistoryCapacity">Последние terminal fingerprints, 1..4096; вытеснение не снимает watermark.</param>
/// <param name="MaxProducers">Не вытесняемые producer barriers текущего binding, 1..128.</param>
/// <param name="MaxNodes">Максимум узлов и captured NodeKeys одной проекции, 1..10000.</param>
/// <param name="MaxEdges">Максимум физических кабелей одной проекции, 0..100000.</param>
public sealed record ProjectionOptions(int MaxQueries = 16, int PageRows = 32, int MaxEvents = 64,
    int HistoryCapacity = 256, int MaxProducers = 128, int MaxNodes = 10_000, int MaxEdges = 100_000);
