using Netrun.Core.Transport;

namespace Netrun.Core.Discovery;

/// <summary>Последовательный live индекс семейства/tag; обновляется владельцем mirror после атомарной публикации фактов.</summary>
public sealed class ServiceDirectory
{
    /// <summary>Один актуальный generation на service ID; старые поколения удаляются из обоих индексов.</summary>
    private readonly Dictionary<string, ServiceFact> _services = new(StringComparer.Ordinal);
    /// <summary>Family bucket обеспечивает bounded capture без обхода всей registry.</summary>
    private readonly Dictionary<string, SortedSet<string>> _families = new(StringComparer.Ordinal);
    /// <summary>Точный family/tag bucket; null tag query использует полный family bucket.</summary>
    private readonly Dictionary<(string Family, string Tag), SortedSet<string>> _tags = [];
    /// <summary>Максимум одновременно индексируемых service IDs.</summary>
    private readonly int _maximum;

    /// <summary>Текущее количество живых service IDs.</summary>
    public int Count => _services.Count;
    /// <summary>Монотонная версия успешных изменений live индекса.</summary>
    public ulong Revision { get; private set; }

    /// <summary>Создаёт индекс с явным верхним пределом, не выделяя полный лимит заранее.</summary>
    public ServiceDirectory(int maxServices = 20_000)
    {
        if (maxServices < 1)
            throw new ArgumentOutOfRangeException(nameof(maxServices));
        _maximum = maxServices;
    }

    /// <summary>Валидирует full fact и атомарно заменяет indexes; mirror отвечает за retired generations после удаления.</summary>
    public DirectoryUpdateResult Upsert(ServiceFact fact)
    {
        if (!FactValidation.IsValid(new ServiceUpsert(fact))) return DirectoryUpdateResult.Invalid;
        if (_services.TryGetValue(fact.ServiceId, out var old))
        {
            if (fact.Generation < old.Generation || fact.Generation == old.Generation && fact.Revision < old.Revision)
                return DirectoryUpdateResult.Ignored;
            if (fact.Generation == old.Generation && fact.Revision == old.Revision)
                return fact == old ? DirectoryUpdateResult.Ignored : DirectoryUpdateResult.Conflict;
        }
        else if (_services.Count == _maximum) return DirectoryUpdateResult.Capacity;
        EnsureRevision();
        if (old is not null) RemoveIndexes(old);
        _services[fact.ServiceId] = fact;
        Bucket(_families, fact.Family).Add(fact.ServiceId);
        if (fact.Tag is { } tag) Bucket(_tags, (fact.Family, tag)).Add(fact.ServiceId);
        Revision++;
        return DirectoryUpdateResult.Applied;
    }

    /// <summary>Удаляет только exact generation; запоздалое удаление старого экземпляра не затрагивает замену.</summary>
    public bool Remove(ServiceKey key)
    {
        if (key is null || key.Generation == 0 || !NodeFactRules.IsValidText(key.ServiceId, 128) ||
            !_services.TryGetValue(key.ServiceId, out var fact) || fact.Generation != key.Generation)
            return false;
        EnsureRevision();
        RemoveIndexes(fact);
        _services.Remove(key.ServiceId);
        Revision++;
        return true;
    }

    /// <summary>Очищает все live indexes при новом snapshot/binding; ранее выданные выборки остаются immutable.</summary>
    public void Reset()
    {
        EnsureRevision();
        _services.Clear();
        _families.Clear();
        _tags.Clear();
        Revision++;
    }

    /// <summary>Копирует максимум limit ключей одного bucket, проверяя только один дополнительный элемент для truncation.</summary>
    public ServiceCandidateSnapshot Capture(string family, string? tag = null, int limit = 4096)
    {
        if (!NodeFactRules.IsValidText(family, 128) || tag is not null && !NodeFactRules.IsValidText(tag, 256))
            throw new ArgumentException("Invalid discovery selection.");
        if (limit is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
        SortedSet<string>? bucket;
        if (tag is null) _families.TryGetValue(family, out bucket);
        else _tags.TryGetValue((family, tag), out bucket);
        var keys = new List<ServiceKey>();
        var truncated = false;
        if (bucket is not null)
            foreach (var id in bucket)
            {
                if (keys.Count == limit) { truncated = true; break; }
                var fact = _services[id];
                keys.Add(new(id, fact.Generation));
            }
        return new(keys, truncated, Revision, limit);
    }

    /// <summary>O(1) admission preflight проверяет retained budget до выделения массива candidate keys.</summary>
    internal int CountCandidates(string family, string? tag, int limit)
    {
        SortedSet<string>? bucket;
        if (tag is null) _families.TryGetValue(family, out bucket);
        else _tags.TryGetValue((family, tag), out bucket);
        return Math.Min(bucket?.Count ?? 0, limit);
    }

    /// <summary>Создаёт только необходимый ordinal bucket вместо копирования всех service facts.</summary>
    private static SortedSet<string> Bucket<TKey>(Dictionary<TKey, SortedSet<string>> index, TKey key) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var bucket)) index.Add(key, bucket = new(StringComparer.Ordinal));
        return bucket;
    }

    /// <summary>Удаляет пустые buckets, чтобы churn family/tag не накапливал память.</summary>
    private void RemoveIndexes(ServiceFact fact)
    {
        RemoveBucket(_families, fact.Family, fact.ServiceId);
        if (fact.Tag is { } tag) RemoveBucket(_tags, (fact.Family, tag), fact.ServiceId);
    }

    /// <summary>Сохраняет индекс свободным от пустых retired family/tag значений.</summary>
    private static void RemoveBucket<TKey>(Dictionary<TKey, SortedSet<string>> index, TKey key, string id) where TKey : notnull
    {
        var bucket = index[key];
        bucket.Remove(id);
        if (bucket.Count == 0) index.Remove(key);
    }

    /// <summary>Останавливает исчерпание версии до первой мутации, не допуская wraparound.</summary>
    private void EnsureRevision()
    {
        if (Revision == ulong.MaxValue) throw new InvalidOperationException("Directory revision exhausted.");
    }
}
