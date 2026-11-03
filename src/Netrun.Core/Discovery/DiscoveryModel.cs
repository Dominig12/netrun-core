using Netrun.Core.Transport;

namespace Netrun.Core.Discovery;

/// <summary>Результат изменения индекса; каталог не заменяет ordering/tombstone ledger основного mirror.</summary>
public enum DirectoryUpdateResult
{
    /// <summary>Новое полное объявление опубликовано в индексах.</summary>
    Applied,
    /// <summary>Та же или более старая версия не меняет индекс.</summary>
    Ignored,
    /// <summary>Объявление не проходит общие инварианты фактов.</summary>
    Invalid,
    /// <summary>Один revision имеет два разных полных содержимых.</summary>
    Conflict,
    /// <summary>Для нового service ID нет свободного bounded slot.</summary>
    Capacity
}

/// <summary>Неизменяемая ограниченная выборка точных поколений; новые регистрации не добавляются в уже начатый запрос.</summary>
public sealed class ServiceCandidateSnapshot
{
    /// <summary>Собственный immutable список, упорядоченный ordinal service ID.</summary>
    public ValueList<ServiceKey> Keys { get; }
    /// <summary>В индексе были дополнительные кандидаты сверх явно заданного предела.</summary>
    public bool Truncated { get; }
    /// <summary>Версия индекса в момент bounded capture.</summary>
    public ulong Revision { get; }

    /// <summary>Копирует только bounded список выбранного family/tag bucket.</summary>
    internal ServiceCandidateSnapshot(IEnumerable<ServiceKey> keys, bool truncated, ulong revision, int maximum)
    {
        Keys = new(keys, maximum);
        Truncated = truncated;
        Revision = revision;
    }
}

/// <summary>Сетевой поиск; произвольные native accepts/matches/permissions не сериализуются в ядро.</summary>
/// <param name="Key">Producer identity с положительным монотонным sequence.</param>
/// <param name="Source">Точный экземпляр исходного сервиса.</param>
/// <param name="DestinationFamily">Объявленное семейство адресатов.</param>
/// <param name="RecipientTag">Точный tag либо null для всех tags выбранного family.</param>
/// <param name="Communications">Явный opt-in fallback через отдельный allowlisted communication provider.</param>
/// <param name="Frequency">Положительная native frequency либо null для control traffic.</param>
/// <param name="MaxResults">Явный предел результатов; по умолчанию полный bounded candidate limit.</param>
public sealed record DiscoveryRequest(RequestKey Key, ServiceKey Source, string DestinationFamily,
    string? RecipientTag = null, bool Communications = false, int? Frequency = null, int MaxResults = int.MaxValue);

/// <summary>Immutable страница только service identities; последующий transport и DM повторно проверяют доставку и native права.</summary>
public sealed record DiscoveryPage : TransportEvent
{
    /// <summary>До 64 точных service generations без native references или payload.</summary>
    public ValueList<ServiceKey> Candidates { get; }
    /// <summary>Последняя страница этого запроса; пустая terminal страница допустима.</summary>
    public bool Complete { get; }
    /// <summary>page/completed/truncated/cancelled либо безопасная причина отказа source/route policy.</summary>
    public string Code { get; }

    /// <summary>Защитно копирует не более 64 keys, прекращая перечисление при первом лишнем элементе.</summary>
    public DiscoveryPage(string bindingEpoch, RequestKey key, IEnumerable<ServiceKey> candidates, bool complete, string code)
        : base(bindingEpoch, key)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Candidates = new(candidates, 64);
        if (!NodeFactRules.IsValidText(bindingEpoch, 128) || !TransportFingerprint.ValidKey(key) ||
            !TransportFingerprint.ValidToken(code, 64) || Candidates.Any(candidate => candidate is null ||
                candidate.Generation == 0 || !NodeFactRules.IsValidText(candidate.ServiceId, 128)))
            throw new ArgumentException("Invalid discovery page.");
        Complete = complete;
        Code = code;
    }
}

/// <summary>Опциональные quotas и справедливая ротация; default не ограничивает active work.</summary>
/// <param name="MaxQueries">Одновременно принятые запросы; default — машинный maximum.</param>
/// <param name="MaxQueriesPerSource">Запросы одного source service; default — машинный maximum.</param>
/// <param name="MaxCandidates">Pinned keys одного запроса; default — полный каталог.</param>
/// <param name="MaxRetainedCandidates">Сумма pinned keys активных запросов; default — машинный maximum.</param>
/// <param name="MaxEvents">Непрочитанные страницы; default — машинный maximum.</param>
/// <param name="PageSize">Результаты одной страницы, максимум 64.</param>
/// <param name="ChecksPerSlice">Проверки одного query до уступки очереди, максимум 8.</param>
/// <param name="HistoryCapacity">Окно terminal fingerprints без хранения результатов, максимум 4096.</param>
/// <param name="MaxProducers">Не вытесняемые watermarks за binding; default — машинный maximum.</param>
public sealed record DiscoveryOptions(int MaxQueries = int.MaxValue, int MaxQueriesPerSource = int.MaxValue,
    int MaxCandidates = int.MaxValue, int MaxRetainedCandidates = int.MaxValue, int MaxEvents = int.MaxValue,
    int PageSize = 64, int ChecksPerSlice = 8, int HistoryCapacity = 4096, int MaxProducers = int.MaxValue);
