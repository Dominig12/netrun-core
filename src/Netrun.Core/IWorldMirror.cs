namespace Netrun.Core;

/// <summary>Последовательный владелец фактов; реализация не предоставляет потокобезопасных mutable-ссылок.</summary>
public interface IWorldMirror
{
    /// <summary>Последний целиком опубликованный пакет.</summary>
    ulong AppliedSequence { get; }
    /// <summary>Игровое время последнего опубликованного пакета в децисекундах.</summary>
    int SimulationTimeDs { get; }
    /// <summary>Read-only live-view для того же потока; отдельный immutable snapshot здесь не создаётся.</summary>
    IReadOnlyDictionary<string, NodeFact> Nodes { get; }
    /// <summary>Проверяет и применяет пакет целиком; ошибки целостности требуют новой сессии.</summary>
    ApplyResult Apply(InputBatch batch);
}
