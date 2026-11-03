using System.Collections.ObjectModel;

namespace Netrun.Core;

/// <summary>Таблица одного вида фактов; тип не знает ни JSON, ни чужих доменных модулей.</summary>
internal sealed class VersionedFactTable<T> where T : class
{
    /// <summary>Сохранённая версия, в том числе tombstone без живого факта.</summary>
    internal sealed record Entry(ulong Generation, ulong Revision, T? Fact);
    /// <summary>Полная история последних версий ID в пределах binding.</summary>
    private readonly Dictionary<string, Entry> _versions = new(StringComparer.Ordinal);
    /// <summary>Только живые факты для read-only представления.</summary>
    private readonly Dictionary<string, T> _live = new(StringComparer.Ordinal);
    /// <summary>Не копирует таблицу при чтении; вызывается тем же последовательным владельцем.</summary>
    internal IReadOnlyDictionary<string, T> Live { get; }
    /// <summary>Число занятых ID с tombstones для ограничений памяти.</summary>
    internal int TrackedCount => _versions.Count;

    /// <summary>Создаёт пустую таблицу и неизменяемый интерфейс чтения.</summary>
    internal VersionedFactTable() => Live = new ReadOnlyDictionary<string, T>(_live);

    /// <summary>Создаёт overlay только затронутых ID; публикация выполняется после проверки всех модулей пакета.</summary>
    internal Transaction Prepare(int maximum) => new(this, maximum);

    /// <summary>Локальная транзакция таблицы; не изменяет владельца при Stage или отказе другого модуля.</summary>
    internal sealed class Transaction(VersionedFactTable<T> owner, int maximum)
    {
        /// <summary>Последние намерения пакета с причинным порядком повторных изменений одного ID.</summary>
        private readonly Dictionary<string, Entry> _overlay = new(StringComparer.Ordinal);
        /// <summary>Количество новых ID относительно исходной таблицы.</summary>
        private int _added;

        /// <summary>Проверяет поколения, immutable revisions и общий capacity; null означает успех.</summary>
        internal string? Stage(string id, ulong generation, ulong revision, T? fact)
        {
            if (!_overlay.TryGetValue(id, out var previous))
                owner._versions.TryGetValue(id, out previous);
            if (previous is not null)
            {
                if (generation < previous.Generation || generation == previous.Generation && revision < previous.Revision)
                    return null;
                if (generation == previous.Generation)
                {
                    if (revision == previous.Revision)
                        return Equals(fact, previous.Fact) ? null : "revision_conflict";
                    if (previous.Fact is null && fact is not null)
                        return "generation_retired";
                }
            }
            else if ((long)owner._versions.Count + ++_added > maximum)
                return "catalog_capacity";
            _overlay[id] = new Entry(generation, revision, fact);
            return null;
        }

        /// <summary>Публикует только подготовленные записи; ожидаемых validation-отказов здесь больше нет.</summary>
        internal void Commit()
        {
            foreach (var (id, entry) in _overlay)
            {
                owner._versions[id] = entry;
                if (entry.Fact is null)
                    owner._live.Remove(id);
                else
                    owner._live[id] = entry.Fact;
            }
        }
    }
}
