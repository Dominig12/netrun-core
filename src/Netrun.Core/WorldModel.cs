namespace Netrun.Core;

/// <summary>Инкарнации мира, процесса ядра и выбранного владельца симуляции.</summary>
/// <param name="World">Меняется при новом запуске DM-мира.</param>
/// <param name="Engine">Меняется при перезапуске Host.</param>
/// <param name="Binding">Меняется при полном resync или передаче владения.</param>
public sealed record Epochs(string World, string Engine, string Binding);

/// <summary>Координаты факта. Не определяют связность секторов или порядок этажей.</summary>
/// <param name="X">Координата тайла: 1..32767.</param>
/// <param name="Y">Координата тайла: 1..32767.</param>
/// <param name="Z">Номер уровня: 1..4096; не расстояние между этажами.</param>
public sealed record Position(int X, int Y, int Z);

/// <summary>Полное состояние узла в первом подмножестве mirror; фактов для построения графа пока недостаточно.</summary>
/// <param name="Address">Сетевой адрес, независимый от идентичности физического объекта.</param>
/// <param name="NodeType">Объявленный вид узла; не имя DM-proc или разрешение на исполнение.</param>
/// <param name="Position">Текущее физическое положение.</param>
/// <param name="SectorId">Непрозрачный идентификатор сектора из адаптера мира.</param>
/// <param name="Online">Последний экспортированный факт доступности.</param>
public sealed record NodeState(string Address, string NodeType, Position Position, string SectorId, bool Online);

/// <summary>Неизменяемый факт об одной инкарнации узла.</summary>
/// <param name="NodeId">Стабильный логический ID, не DM-ссылка.</param>
/// <param name="Generation">Положительное поколение физического объекта.</param>
/// <param name="Revision">Положительная версия полного факта внутри поколения.</param>
/// <param name="State">Полное заменяемое состояние данной версии.</param>
public sealed record NodeFact(string NodeId, ulong Generation, ulong Revision, NodeState State);

/// <summary>Типизированная запись входного пакета. Новые виды регистрируются через обработчики модуля.</summary>
public abstract record WorldChange;
/// <summary>Объявляет полное новое состояние узла; частичные patches не поддерживаются.</summary>
/// <param name="Node">Факт, который требуется применить.</param>
public sealed record NodeUpsert(NodeFact Node) : WorldChange;
/// <summary>Удаляет узел и сохраняет tombstone до завершения binding.</summary>
/// <param name="NodeId">Логический ID удаляемого объекта.</param>
/// <param name="Generation">Удаляемое поколение; не затрагивает более новое.</param>
/// <param name="Revision">Версия удаления внутри поколения.</param>
public sealed record NodeRemove(string NodeId, ulong Generation, ulong Revision) : WorldChange;

/// <summary>Один атомарный упорядоченный набор фактов; вызывающий код не изменяет Entries во время Apply.</summary>
/// <param name="Epochs">Идентичность сессии, которой принадлежат факты.</param>
/// <param name="Sequence">Входная последовательность от 1; повтор сохраняет содержимое.</param>
/// <param name="SimulationTimeDs">Монотонное игровое время в децисекундах, 0..Int32.MaxValue.</param>
/// <param name="Entries">Порядок причинности внутри пакета; не более 128 записей.</param>
public sealed record InputBatch(Epochs Epochs, ulong Sequence, int SimulationTimeDs, IReadOnlyList<WorldChange> Entries);

/// <summary>Результат применения; при отказе cumulative ACK не продвигается.</summary>
public enum ApplyStatus
{
    /// <summary>Пакет целиком опубликован.</summary>
    Applied,
    /// <summary>Содержательно идентичный повтор уже применённого пакета.</summary>
    Duplicate,
    /// <summary>Пакет отклонён без изменения mirror; исправленный пакет может сохранить sequence.</summary>
    Rejected,
    /// <summary>Целостность потока потеряна; требуется новая сессия mirror.</summary>
    ResyncRequired
}

/// <summary>Безопасная диагностика и граница полностью применённых входов.</summary>
/// <param name="Status">Категория результата.</param>
/// <param name="Code">Код причины без пользовательского содержимого.</param>
/// <param name="AppliedSequence">Cumulative ACK применения, а не чтения байтов.</param>
/// <param name="SimulationTimeDs">Последнее принятое игровое время в децисекундах.</param>
/// <param name="NodeCount">Число существующих узлов.</param>
/// <param name="TrackedCount">Узлы вместе с tombstones; используется для контроля памяти.</param>
public sealed record ApplyResult(ApplyStatus Status, string Code, ulong AppliedSequence,
    int SimulationTimeDs, int NodeCount, int TrackedCount);

/// <summary>Локальные пределы памяти и работы; пока не являются согласованными production-лимитами.</summary>
/// <param name="MaxEntries">Максимум записей одного пакета, 1..128.</param>
/// <param name="MaxTrackedNodes">Лимит ID, включая tombstones, в одной сессии.</param>
/// <param name="ReplayWindow">Число хранимых fingerprint; более старые повторы требуют resync.</param>
/// <param name="MaxSnapshotChunks">Предельное число chunks одного снимка, включая пустые.</param>
public sealed record CoreLimits(int MaxEntries = 128, int MaxTrackedNodes = 10_000, int ReplayWindow = 64,
    int MaxSnapshotChunks = 10_000);
