using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Netrun.Core;

/// <summary>
/// Последовательный владелец offline mirror. Сначала проверяет весь пакет, затем
/// публикует только затронутые записи. Не строит граф и не исполняет игровые эффекты.
/// </summary>
public sealed class WorldMirror : IWorldMirror
{
    /// <summary>Последняя версия ID; null Fact сохраняет запрет воскрешения удалённого поколения.</summary>
    private sealed record VersionedNode(ulong Generation, ulong Revision, NodeFact? Fact);

    /// <summary>Строгий Unicode исключает неоднозначный fingerprint из-за замены повреждённых surrogate.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>Неизменяемая идентичность одной сессии; resync создаёт новый mirror.</summary>
    private readonly Epochs _epochs;
    /// <summary>Границы памяти и работы одного вызова.</summary>
    private readonly CoreLimits _limits;
    /// <summary>Полный реестр ID, включая tombstones; не очищается по TTL.</summary>
    private readonly Dictionary<string, VersionedNode> _versions = new(StringComparer.Ordinal);
    /// <summary>Только существующие узлы; read-only обёртка не копирует словарь на каждом чтении.</summary>
    private readonly Dictionary<string, NodeFact> _nodes = new(StringComparer.Ordinal);
    /// <summary>Явно зарегистрированные обработчики типов; состав фиксирован на время сессии.</summary>
    private readonly Dictionary<Type, INodeChangeHandler> _handlers = [];
    /// <summary>Ограниченное окно semantic fingerprints уже применённых пакетов.</summary>
    private readonly Dictionary<ulong, byte[]> _recent = [];
    /// <summary>Порядок вытеснения fingerprint за O(1), без сканирования истории.</summary>
    private readonly Queue<ulong> _recentOrder = [];
    /// <summary>Защёлка потери целостности; дальнейшая публикация запрещена.</summary>
    private bool _requiresResync;
    /// <summary>Защищает координатор от повторного входа через ошибочный локальный обработчик; не заменяет single-owner.</summary>
    private bool _applying;

    /// <inheritdoc />
    public ulong AppliedSequence { get; private set; }
    /// <inheritdoc />
    public int SimulationTimeDs { get; private set; }
    /// <inheritdoc />
    public IReadOnlyDictionary<string, NodeFact> Nodes { get; }

    /// <summary>Создаёт пустой mirror и фиксирует зависимости; сетевой handshake выполняется вне ядра.</summary>
    /// <param name="epochs">Идентичность offline сессии.</param>
    /// <param name="limits">Лимиты, либо консервативные значения разработки.</param>
    /// <param name="additionalHandlers">Чистые нормализаторы новых записей модуля узлов; штатные типы заменять нельзя.</param>
    public WorldMirror(Epochs epochs, CoreLimits? limits = null, IEnumerable<INodeChangeHandler>? additionalHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        if (!ValidText(epochs.World, 128) || !ValidText(epochs.Engine, 128) || !ValidText(epochs.Binding, 128))
            throw new ArgumentException("Invalid epochs.", nameof(epochs));
        _limits = limits ?? new CoreLimits();
        if (_limits.MaxEntries is < 1 or > 128 || _limits.MaxTrackedNodes < 1 || _limits.ReplayWindow < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _epochs = epochs;
        Register(new NodeUpsertHandler());
        Register(new NodeRemoveHandler());
        if (additionalHandlers is not null)
            foreach (var handler in additionalHandlers)
                Register(handler);
        Nodes = new ReadOnlyDictionary<string, NodeFact>(_nodes);
    }

    /// <inheritdoc />
    public ApplyResult Apply(InputBatch batch)
    {
        if (_applying)
            throw new InvalidOperationException("Reentrant mirror application is not supported.");
        _applying = true;
        try { return ApplyCore(batch); }
        finally { _applying = false; }
    }

    /// <summary>Выполняет проверку причинности, нормализацию, staging и commit под защитой от reentrancy.</summary>
    private ApplyResult ApplyCore(InputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_requiresResync)
            return Result(ApplyStatus.ResyncRequired, "resync_required");
        if (batch.Epochs != _epochs)
            return Result(ApplyStatus.Rejected, "epoch_mismatch");
        if (batch.Sequence == 0 || batch.SimulationTimeDs < 0 || batch.Entries is null ||
            batch.Entries.Count > _limits.MaxEntries)
            return Result(ApplyStatus.Rejected, "invalid_batch");

        var mutations = new NodeMutation[batch.Entries.Count];
        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var change = batch.Entries[i];
            if (change is null || !_handlers.TryGetValue(change.GetType(), out var handler))
                return Result(ApplyStatus.Rejected, "unsupported_change");
            var mutation = handler.Normalize(change);
            if (!ValidMutation(mutation))
                return Result(ApplyStatus.Rejected, "invalid_batch");
            mutations[i] = mutation!;
        }

        var fingerprint = Fingerprint(batch, mutations);
        if (batch.Sequence <= AppliedSequence)
        {
            if (_recent.TryGetValue(batch.Sequence, out var previous) &&
                CryptographicOperations.FixedTimeEquals(previous, fingerprint))
                return Result(ApplyStatus.Duplicate, "duplicate");
            return RequireResync("replay_conflict_or_expired");
        }
        if (AppliedSequence == ulong.MaxValue || batch.Sequence != AppliedSequence + 1)
            return RequireResync("sequence_gap");
        if (batch.SimulationTimeDs < SimulationTimeDs)
            return Result(ApplyStatus.Rejected, "time_regression");

        // Overlay растёт пропорционально пакету, а не всему миру. Tombstones
        // учитываются в capacity и живут до закрытия сессии/binding.
        var staged = new Dictionary<string, VersionedNode>(StringComparer.Ordinal);
        var tracked = _versions.Count;
        foreach (var mutation in mutations)
        {
            var (id, generation, revision, fact) = mutation;
            if (!staged.TryGetValue(id, out var previous))
                _versions.TryGetValue(id, out previous);
            if (previous is not null)
            {
                if (generation < previous.Generation || generation == previous.Generation && revision < previous.Revision)
                    continue;
                if (generation == previous.Generation)
                {
                    if (revision == previous.Revision)
                    {
                        if (fact != previous.Fact)
                            return Result(ApplyStatus.Rejected, "revision_conflict");
                        continue;
                    }
                    if (previous.Fact is null && fact is not null)
                        return Result(ApplyStatus.Rejected, "generation_retired");
                }
            }
            else if (++tracked > _limits.MaxTrackedNodes)
                return Result(ApplyStatus.Rejected, "mirror_capacity");
            staged[id] = new VersionedNode(generation, revision, fact);
        }

        foreach (var (id, version) in staged)
        {
            _versions[id] = version;
            if (version.Fact is null)
                _nodes.Remove(id);
            else
                _nodes[id] = version.Fact;
        }
        AppliedSequence = batch.Sequence;
        SimulationTimeDs = batch.SimulationTimeDs;
        _recent.Add(batch.Sequence, fingerprint);
        _recentOrder.Enqueue(batch.Sequence);
        if (_recentOrder.Count > _limits.ReplayWindow)
            _recent.Remove(_recentOrder.Dequeue());
        return Result(ApplyStatus.Applied, "applied");
    }

    /// <summary>Регистрирует один тип один раз; конфигурационная ошибка обнаруживается до обработки входов.</summary>
    private void Register(INodeChangeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler.ChangeType is null || !typeof(WorldChange).IsAssignableFrom(handler.ChangeType) ||
            !_handlers.TryAdd(handler.ChangeType, handler))
            throw new ArgumentException("Invalid or duplicate change handler.", nameof(handler));
    }

    /// <summary>Снимок безопасной диагностики; не сохраняет ссылку на входной пакет.</summary>
    private ApplyResult Result(ApplyStatus status, string code) =>
        new(status, code, AppliedSequence, SimulationTimeDs, _nodes.Count, _versions.Count);

    /// <summary>Закрывает admission данной сессии после gap, несовпадающего или слишком старого replay.</summary>
    private ApplyResult RequireResync(string code)
    {
        _requiresResync = true;
        return Result(ApplyStatus.ResyncRequired, code);
    }

    /// <summary>Проверяет длину UTF-16, управляющие символы и корректность Unicode до hashing.</summary>
    private static bool ValidText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            return false;
        try { _ = StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    /// <summary>Повторно проверяет нормализатор: расширение не может обойти общие инварианты факта.</summary>
    private static bool ValidMutation(NodeMutation? mutation)
    {
        if (mutation is null || !ValidText(mutation.NodeId, 128) || mutation.Generation == 0 || mutation.Revision == 0)
            return false;
        if (mutation.Fact is null)
            return true;
        var node = mutation.Fact;
        return node.NodeId == mutation.NodeId && node.Generation == mutation.Generation && node.Revision == mutation.Revision &&
            node.State is { Position: not null } state && ValidText(state.Address, 256) &&
            ValidText(state.NodeType, 256) && ValidText(state.SectorId, 256) &&
            state.Position.X is >= 1 and <= 32767 && state.Position.Y is >= 1 and <= 32767 &&
            state.Position.Z is >= 1 and <= 4096;
    }

    /// <summary>Хэш нормализованных эффектов и причинного порядка; JSON whitespace не влияет на replay.</summary>
    private static byte[] Fingerprint(InputBatch batch, IReadOnlyList<NodeMutation> mutations)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(batch.Epochs.World);
            writer.Write(batch.Epochs.Engine);
            writer.Write(batch.Epochs.Binding);
            writer.Write(batch.Sequence);
            writer.Write(batch.SimulationTimeDs);
            writer.Write(mutations.Count);
            foreach (var mutation in mutations)
            {
                writer.Write(mutation.NodeId);
                writer.Write(mutation.Generation);
                writer.Write(mutation.Revision);
                writer.Write(mutation.Fact is not null);
                if (mutation.Fact is { } node)
                {
                    writer.Write(node.State.Address);
                    writer.Write(node.State.NodeType);
                    writer.Write(node.State.Position.X);
                    writer.Write(node.State.Position.Y);
                    writer.Write(node.State.Position.Z);
                    writer.Write(node.State.SectorId);
                    writer.Write(node.State.Online);
                }
            }
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }
}
