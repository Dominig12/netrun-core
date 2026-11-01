namespace Netrun.Core;

/// <summary>Нормализованное намерение модуля: заменить полный факт или сохранить tombstone.</summary>
/// <param name="NodeId">ID объекта, совпадающий с Fact.NodeId при обновлении.</param>
/// <param name="Generation">Положительное поколение, совпадающее с поколением Fact.</param>
/// <param name="Revision">Положительная версия, совпадающая с версией Fact.</param>
/// <param name="Fact">Полный новый факт; null означает удаление.</param>
public sealed record NodeMutation(string NodeId, ulong Generation, ulong Revision, NodeFact? Fact);

/// <summary>
/// Точка расширения модуля узлов. Обработчик только нормализует запись и не изменяет
/// мир, mirror или внешнее состояние: публикацией целого пакета владеет WorldMirror.
/// Результат детерминирован всей записью, без часов/random; Normalize вызывается и при replay.
/// Обработчик является доверенным локальным кодом; повторный вход в Apply запрещён.
/// </summary>
public interface INodeChangeHandler
{
    /// <summary>Точный тип записи для регистрации; reflection и поиск по сборкам не используются.</summary>
    Type ChangeType { get; }

    /// <summary>Возвращает полное намерение или null для недопустимого входа; результат повторно валидируется ядром.</summary>
    NodeMutation? Normalize(WorldChange change);
}

/// <summary>Типизированная база обработчика: один cast после O(1)-поиска по типу записи.</summary>
/// <typeparam name="TChange">Поддерживаемый тип входной записи.</typeparam>
public abstract class NodeChangeHandler<TChange> : INodeChangeHandler where TChange : WorldChange
{
    /// <inheritdoc />
    public Type ChangeType => typeof(TChange);

    /// <inheritdoc />
    public NodeMutation? Normalize(WorldChange change) => Normalize((TChange)change);

    /// <summary>Нормализует запись своего типа без побочных эффектов и зависимости от mutable mirror.</summary>
    protected abstract NodeMutation? Normalize(TChange change);
}

/// <summary>Преобразует штатный upsert в полную замену факта.</summary>
internal sealed class NodeUpsertHandler : NodeChangeHandler<NodeUpsert>
{
    /// <inheritdoc />
    protected override NodeMutation? Normalize(NodeUpsert change) => change.Node is { } node
        ? new NodeMutation(node.NodeId, node.Generation, node.Revision, node) : null;
}

/// <summary>Преобразует штатное удаление в tombstone; новое поколение не переиспользует старую идентичность.</summary>
internal sealed class NodeRemoveHandler : NodeChangeHandler<NodeRemove>
{
    /// <inheritdoc />
    protected override NodeMutation Normalize(NodeRemove change) => new(change.NodeId, change.Generation, change.Revision, null);
}
