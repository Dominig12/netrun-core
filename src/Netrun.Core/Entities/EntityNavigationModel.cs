using Netrun.Core.Transport;
using Netrun.Core.Access;

namespace Netrun.Core.Entities;

/// <summary>Независимое от DM-ссылок поколение native-представления сущности.</summary>
/// <param name="Id">Непустая opaque identity длиной до 128 символов.</param>
/// <param name="Generation">Положительное поколение; повторное использование Id требует нового поколения.</param>
public sealed record ActorKey(string Id, ulong Generation);

/// <summary>Запрос пути по фактам допуска; выдача пути не является разрешением выполнить native-переход.</summary>
/// <param name="Key">Монотонный producer serial в текущем binding.</param>
/// <param name="Actor">Поколение сущности, для которой собраны факты допуска.</param>
/// <param name="ActorRevision">Ревизия residence/authority native-сущности; потребитель повторно проверяет её перед использованием.</param>
/// <param name="Source">Точное поколение исходного узла.</param>
/// <param name="Destination">Точное поколение назначения.</param>
/// <param name="Access">Immutable raw native policy/credential facts; допуск каждого шага вычисляется Core.</param>
public sealed record EntityNavigationRequest(RequestKey Key, ActorKey Actor, ulong ActorRevision,
    NodeKey Source, NodeKey Destination, EntityAccessSnapshot Access);

/// <summary>Страница пути без Source; native-потребитель публикует путь только после completed и проверки всех revisions.</summary>
public sealed record EntityNavigationPage : TransportEvent
{
    /// <summary>Поколение исходной сущности, не сериализованная native-ссылка.</summary>
    public ActorKey Actor { get; }
    /// <summary>Ревизия residence/authority при сборе ACL.</summary>
    public ulong ActorRevision { get; }
    /// <summary>Захваченный исходный узел.</summary>
    public NodeKey Source { get; }
    /// <summary>Захваченное назначение.</summary>
    public NodeKey Destination { get; }
    /// <summary>Версия structural публикации; ноль до её захвата.</summary>
    public ulong GraphVersion { get; }
    /// <summary>Граница world-фактов захваченной публикации.</summary>
    public ulong GraphInputSequence { get; }
    /// <summary>Захваченная availability revision.</summary>
    public ulong AvailabilityVersion { get; }
    /// <summary>Порядковый номер страницы; начинается с единицы.</summary>
    public ulong PageIndex { get; }
    /// <summary>Последовательные шаги пути; Source исключён, Destination завершает успешный непустой путь.</summary>
    public ValueList<NodeKey> Nodes { get; }
    /// <summary>Последняя страница; любой код кроме completed отзывает ранее переданные шаги.</summary>
    public bool Complete { get; }
    /// <summary>page/completed/unreachable/stale_endpoint/navigation_changed/cancelled/navigation_capacity.</summary>
    public string Code { get; }

    /// <summary>Защитно копирует не более 64 шагов; страница не удерживает граф, ACL или workspace поиска.</summary>
    public EntityNavigationPage(string bindingEpoch, RequestKey key, ActorKey actor, ulong actorRevision,
        NodeKey source, NodeKey destination, ulong graphVersion, ulong graphInputSequence,
        ulong availabilityVersion, ulong pageIndex, IEnumerable<NodeKey> nodes, bool complete, string code)
        : base(bindingEpoch, key)
    {
        Actor = actor;
        ActorRevision = actorRevision;
        Source = source;
        Destination = destination;
        GraphVersion = graphVersion;
        GraphInputSequence = graphInputSequence;
        AvailabilityVersion = availabilityVersion;
        PageIndex = pageIndex;
        Nodes = new(nodes, 64);
        Complete = complete;
        Code = code;
    }
}

/// <summary>Локальные пределы памяти; одна единица Advance рассматривает один соседний узел, а не весь граф.</summary>
/// <param name="MaxQueries">Активные slots до handoff terminal страницы.</param>
/// <param name="MaxEvents">Непереданные страницы; на запрос допускается не больше одной.</param>
/// <param name="PageRows">Количество шагов на страницу, 1..64.</param>
/// <param name="MaxNodes">Предел visited/path workspace одного запроса.</param>
/// <param name="HistoryCapacity">Окно terminal fingerprints, не хранящее ACL и пути.</param>
/// <param name="MaxProducers">Не вытесняемые producer barriers одного binding.</param>
public sealed record EntityNavigationOptions(int MaxQueries = int.MaxValue, int MaxEvents = int.MaxValue,
    int PageRows = 32, int MaxNodes = int.MaxValue, int HistoryCapacity = 256, int MaxProducers = int.MaxValue);
