using System.Security.Cryptography;
using System.Text;

namespace Netrun.Core;

/// <summary>
/// Последовательный владелец offline mirror. Сначала проверяет весь пакет, затем
/// публикует только затронутые записи. Не строит граф и не исполняет игровые эффекты.
/// </summary>
public sealed class WorldMirror : IWorldMirror
{
    /// <summary>Неизменяемая идентичность одной сессии; resync создаёт новый mirror.</summary>
    private readonly Epochs _epochs;
    /// <summary>Границы памяти и работы одного вызова.</summary>
    private readonly CoreLimits _limits;
    /// <summary>Полный реестр ID, включая tombstones; не очищается по TTL.</summary>
    private readonly VersionedFactTable<NodeFact> _nodeTable = new();
    /// <summary>Версионированная связность этажей; read-only обёртка не копирует словарь при чтении.</summary>
    private readonly VersionedFactTable<SectorFact> _sectorTable = new();
    /// <summary>Объявления сервисов версионируются независимо от host и публикуются в общей транзакции.</summary>
    private readonly VersionedFactTable<ServiceFact> _serviceTable = new();
    /// <summary>Явно зарегистрированные обработчики типов; состав фиксирован на время сессии.</summary>
    private readonly Dictionary<Type, INodeChangeHandler> _handlers = [];
    /// <summary>Ограниченное окно semantic fingerprints уже применённых пакетов.</summary>
    private readonly AppliedReplayWindow _recent;
    /// <summary>Защёлка потери целостности; дальнейшая публикация запрещена.</summary>
    private bool _requiresResync;
    /// <summary>Защищает координатор от повторного входа через ошибочный локальный обработчик; не заменяет single-owner.</summary>
    private bool _applying;

    /// <inheritdoc />
    public ulong AppliedSequence { get; private set; }
    /// <inheritdoc />
    public int SimulationTimeDs { get; private set; }
    /// <inheritdoc />
    public IReadOnlyDictionary<string, NodeFact> Nodes => _nodeTable.Live;
    /// <inheritdoc />
    public IReadOnlyDictionary<string, SectorFact> Sectors => _sectorTable.Live;
    /// <inheritdoc />
    public IReadOnlyDictionary<string, ServiceFact> Services => _serviceTable.Live;
    /// <summary>Число ID с учётом tombstones для диагностики координатора синхронизации.</summary>
    internal int TrackedCount => _nodeTable.TrackedCount;

    /// <summary>Создаёт пустой mirror и фиксирует зависимости; сетевой handshake выполняется вне ядра.</summary>
    /// <param name="epochs">Идентичность offline сессии.</param>
    /// <param name="limits">Лимиты, либо консервативные значения разработки.</param>
    /// <param name="additionalHandlers">Чистые нормализаторы новых записей модуля узлов; штатные типы заменять нельзя.</param>
    public WorldMirror(Epochs epochs, CoreLimits? limits = null, IEnumerable<INodeChangeHandler>? additionalHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        if (!NodeFactRules.IsValidText(epochs.World, 128) || !NodeFactRules.IsValidText(epochs.Engine, 128) ||
            !NodeFactRules.IsValidText(epochs.Binding, 128))
            throw new ArgumentException("Invalid epochs.", nameof(epochs));
        _limits = limits ?? new CoreLimits();
        if (_limits.MaxEntries is < 1 or > 128 || _limits.MaxTrackedNodes < 1 || _limits.ReplayWindow < 1 ||
            _limits.MaxTrackedSectors < 1 || _limits.MaxTrackedServices < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _epochs = epochs;
        _recent = new AppliedReplayWindow(_limits.ReplayWindow);
        Register(new NodeUpsertHandler());
        Register(new NodeRemoveHandler());
        if (additionalHandlers is not null)
            foreach (var handler in additionalHandlers)
                Register(handler);
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

        var mutations = new NodeMutation?[batch.Entries.Count];
        for (var i = 0; i < batch.Entries.Count; i++)
        {
            var change = batch.Entries[i];
            if (change is SectorUpsert or SectorRemove or ServiceUpsert or ServiceRemove)
            {
                if (!CatalogRules.ValidChange(change))
                    return Result(ApplyStatus.Rejected, "invalid_batch");
                continue;
            }
            if (change is null || !_handlers.TryGetValue(change.GetType(), out var handler))
                return Result(ApplyStatus.Rejected, "unsupported_change");
            var mutation = handler.Normalize(change);
            if (!NodeFactRules.IsValidMutation(mutation))
                return Result(ApplyStatus.Rejected, "invalid_batch");
            mutations[i] = mutation!;
        }

        var fingerprint = Fingerprint(batch, mutations);
        if (batch.Sequence <= AppliedSequence)
        {
            if (_recent.Matches(batch.Sequence, fingerprint))
                return Result(ApplyStatus.Duplicate, "duplicate");
            return RequireResync("replay_conflict_or_expired");
        }
        if (AppliedSequence == ulong.MaxValue || batch.Sequence != AppliedSequence + 1)
            return RequireResync("sequence_gap");
        if (batch.SimulationTimeDs < SimulationTimeDs)
            return Result(ApplyStatus.Rejected, "time_regression");

        // Overlay растёт пропорционально пакету, а не всему миру. Tombstones
        // учитываются в capacity и живут до закрытия сессии/binding.
        var nodes = _nodeTable.Prepare(_limits.MaxTrackedNodes);
        var sectors = _sectorTable.Prepare(_limits.MaxTrackedSectors);
        var services = _serviceTable.Prepare(_limits.MaxTrackedServices);
        for (var i = 0; i < mutations.Length; i++)
        {
            string? error;
            if (mutations[i] is { } mutation)
            {
                error = nodes.Stage(mutation.NodeId, mutation.Generation, mutation.Revision, mutation.Fact);
                if (error == "catalog_capacity")
                    error = "mirror_capacity";
            }
            else
                error = batch.Entries[i] switch
                {
                    SectorUpsert upsert => sectors.Stage(upsert.Sector.SectorId, upsert.Sector.Generation, upsert.Sector.Revision, upsert.Sector),
                    SectorRemove remove => sectors.Stage(remove.SectorId, remove.Generation, remove.Revision, null),
                    ServiceUpsert upsert => services.Stage(upsert.Service.ServiceId, upsert.Service.Generation, upsert.Service.Revision, upsert.Service),
                    ServiceRemove remove => services.Stage(remove.ServiceId, remove.Generation, remove.Revision, null),
                    _ => throw new InvalidOperationException("Validated catalog change is unsupported.")
                };
            if (error is not null)
                return Result(ApplyStatus.Rejected, error);
        }
        nodes.Commit();
        sectors.Commit();
        services.Commit();
        AppliedSequence = batch.Sequence;
        SimulationTimeDs = batch.SimulationTimeDs;
        _recent.Record(batch.Sequence, fingerprint);
        return Result(ApplyStatus.Applied, "applied");
    }

    /// <summary>Регистрирует один тип один раз; конфигурационная ошибка обнаруживается до обработки входов.</summary>
    private void Register(INodeChangeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler.ChangeType is null || !typeof(WorldChange).IsAssignableFrom(handler.ChangeType) ||
            handler.ChangeType == typeof(SectorUpsert) || handler.ChangeType == typeof(SectorRemove) ||
            handler.ChangeType == typeof(ServiceUpsert) || handler.ChangeType == typeof(ServiceRemove) ||
            !_handlers.TryAdd(handler.ChangeType, handler))
            throw new ArgumentException("Invalid or duplicate change handler.", nameof(handler));
    }

    /// <summary>Снимок безопасной диагностики; не сохраняет ссылку на входной пакет.</summary>
    private ApplyResult Result(ApplyStatus status, string code) =>
        new(status, code, AppliedSequence, SimulationTimeDs, Nodes.Count, _nodeTable.TrackedCount);

    /// <summary>Закрывает admission данной сессии после gap, несовпадающего или слишком старого replay.</summary>
    private ApplyResult RequireResync(string code)
    {
        _requiresResync = true;
        return Result(ApplyStatus.ResyncRequired, code);
    }

    /// <summary>Хэш нормализованных эффектов и причинного порядка; JSON whitespace не влияет на replay.</summary>
    private static byte[] Fingerprint(InputBatch batch, IReadOnlyList<NodeMutation?> mutations)
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
            for (var i = 0; i < mutations.Count; i++)
            {
                if (mutations[i] is { } mutation)
                    WorldFactEncoding.WriteNode(writer, mutation);
                else
                    WorldFactEncoding.WriteCatalog(writer, batch.Entries[i]);
            }
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }
}
