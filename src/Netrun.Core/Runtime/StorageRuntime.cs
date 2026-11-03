using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Scheduling;
using Netrun.Core.Storage;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

public sealed partial class NetworkRuntime
{
    /// <summary>Только этот owner меняет файлы/носители; DM хранит native adapters и immutable проекции.</summary>
    private StorageWorld? _storage;
    /// <summary>Scalar outputs ограничены отдельно от retained domain payload.</summary>
    private readonly Queue<TransportEvent> _storageEvents = [];
    /// <summary>Продолжение одного FIFO head после атомарной мутации, без её повтора при output pressure.</summary>
    private EntityWork? _storageHead;
    private StorageResult? _storageResult;
    private RequestKey? _storageKey;
    private ExpireStorageOffers? _storageExpiry;
    private bool _storageExpiryQueued;
    private ulong _storageExpirySequence;

    /// <summary>Скалярная диагностика без публикации файлового содержимого.</summary>
    public int StorageStoreCount => _storage?.StoreCount ?? 0;
    /// <summary>Включает вложенную .bot память и инертные снимки предложений.</summary>
    public int StorageFileCount => _storage?.FileCount ?? 0;
    /// <summary>Queued scalar outputs, domain changes до handoff и один уже вычисленный terminal outcome.</summary>
    public int StorageEventCount => _storageEvents.Count + (_storage?.EventCount ?? 0) + (_storageResult is null ? 0 : 1);

    /// <summary>Epoch-derived UID prefix одинаков при deterministic replay одного binding и не переиспользуется новым Host.</summary>
    private void InitializeStorage()
    {
        var prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_epochs.Engine + "\0" + _epochs.Binding)));
        _storage = new(prefix, _options.Storage);
    }

    /// <summary>Trusted library read возвращает immutable view; wire/UI ACL проверяет SubmitStorageOperation(ReadStorage).</summary>
    public StorageView? ReadStorage(StoreKey store)
    {
        Enter();
        try { return _storage?.ReadStore(store); }
        finally { _busy = false; }
    }

    /// <summary>Пользовательские intent не могут создать control-plane register/seed из произвольного UI JSON.</summary>
    private static bool IsStorageCommand(RuntimeCommand command) => command is RegisterStorage or RemoveStorage or SeedStorageFile or SubmitStorageOperation or ExpireStorageOffers;

    /// <summary>Только enqueue: file copy, grant merge и package mutations выполняются после возврата Apply.</summary>
    private TransportAdmission EnqueueStorage(RuntimeCommand command)
    {
        var key = StorageCommandKey(command);
        if (State != MirrorSyncState.Ready) return new(AdmissionStatus.Rejected, "world_unavailable", key);
        var fingerprint = StorageCommandFingerprint(command);
        if (fingerprint is null) return new(AdmissionStatus.Rejected, "invalid_storage_command", key);
        return _tasks!.Enqueue(key, new EntityWork(command), fingerprint);
    }

    /// <summary>Атомарная mutation выполняется один раз; последующие шаги передают scalar projections в пределах credit/budget.</summary>
    private CoreTaskStep ExecuteStorageTask(EntityWork work, int budget)
    {
        if (_storage is null) return new(1, true, "world_unavailable", false);
        if (_storageEvents.Count >= (_options.Tasks?.MaximumEvents ?? int.MaxValue)) return new(0, false);
        var consumed = 0;
        if (_storageHead is null)
        {
            if ((work.Command is RegisterStorage or RemoveStorage) && _storage.EventCount >= (_options.Storage?.MaxEvents ?? int.MaxValue))
                return new(0, false);
            var key = StorageCommandKey(work.Command);
            var result = work.Command switch
            {
                RegisterStorage register => _storage.RegisterStore(register.Definition)
                    ? new StorageResult(true, "accepted", Store: register.Definition.Store,
                        Revision: _storage.ReadStoreRevision(register.Definition.Store)!.Value)
                    : new(false, "storage_registration_rejected"),
                RemoveStorage remove => _storage.RemoveStore(remove.Store)
                    ? new StorageResult(true, "removed", Store: remove.Store) : new(false, "storage_remove_rejected"),
                SeedStorageFile seed => _storage.SeedFile(seed.Store, seed.Definition, seed.Preload),
                SubmitStorageOperation submit => _storage.Apply(submit.Operation, SimulationTimeDs),
                ExpireStorageOffers => ExpireOneStorageOffer(),
                _ => new StorageResult(false, "invalid_storage_command")
            };
            if (result.WouldBlock) return new(0, false);
            _storageHead = work; _storageResult = result; _storageKey = key;
            consumed++;
        }
        if (_storageHead != work) throw new InvalidOperationException("Storage FIFO head changed before completion.");
        while (consumed < budget && _storageEvents.Count < (_options.Tasks?.MaximumEvents ?? int.MaxValue) &&
            _storage.TryDequeueEvent(out var change))
        {
            _storageEvents.Enqueue(new StorageChangedProjection(_epochs.Binding, _storageKey!, change!));
            consumed++;
        }
        if (_storage.EventCount > 0 || consumed >= budget || _storageEvents.Count >= (_options.Tasks?.MaximumEvents ?? int.MaxValue)) return new(consumed, false);
        var completed = _storageResult!;
        _storageEvents.Enqueue(new StorageOperationCompleted(_epochs.Binding, _storageKey!, completed));
        consumed++;
        _storageHead = null; _storageResult = null; _storageKey = null;
        if (work.Command is ExpireStorageOffers) { _storageExpiry = null; _storageExpiryQueued = false; }
        return new(consumed, true, completed.Code, completed.Accepted);
    }

    /// <summary>Только назначение готового expiry intent; удаление снимка не выполняется вне общей FIFO.</summary>
    private int AdvanceStorage(int nowDs, int budget)
    {
        if (_storage is null) return 0;
        _storage.ExpireOffers(nowDs, 0);
        if (budget == 0 || _tasks is null || State != MirrorSyncState.Ready || _storageExpiryQueued ||
            _storage.NextExpiryDs is not { } due || due > nowDs) return 0;
        _storageExpiry ??= new(new("storage.expiry", ++_storageExpirySequence), due);
        var admission = _tasks.Enqueue(_storageExpiry.Key, new EntityWork(_storageExpiry), StorageCommandFingerprint(_storageExpiry)!);
        if (admission.Status is AdmissionStatus.Accepted or AdmissionStatus.Duplicate) _storageExpiryQueued = true;
        return 1;
    }

    private StorageResult ExpireOneStorageOffer()
    {
        _storage!.ExpireOffers(SimulationTimeDs, 1);
        return new(true, "expired");
    }

    /// <summary>Binding teardown освобождает queued output и continuation без повторения неизвестных эффектов.</summary>
    private void StopStorage()
    {
        _storage = null; _storageEvents.Clear(); _storageHead = null; _storageResult = null; _storageKey = null;
        _storageExpiry = null; _storageExpiryQueued = false;
    }

    private static RequestKey StorageCommandKey(RuntimeCommand command) => command switch
    {
        RegisterStorage register => register.Key,
        RemoveStorage remove => remove.Key,
        SeedStorageFile seed => seed.Key,
        SubmitStorageOperation submit => submit.Operation.Key,
        ExpireStorageOffers expiry => expiry.Key,
        _ => new("invalid.storage", 0)
    };

    /// <summary>Чистая canonical identity; ни file lookup, ни временный StorageWorld, ни native reference не нужны.</summary>
    internal static byte[]? StorageCommandFingerprint(RuntimeCommand command)
    {
        var key = StorageCommandKey(command);
        if (!TransportFingerprint.ValidKey(key)) return null;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
        switch (command)
        {
            case RegisterStorage register:
                if (!StorageWorld.IsValidStoreDefinition(register.Definition)) return null;
                writer.Write((byte)1); writer.Write(key.Id); writer.Write(register.Definition.Store.Id); writer.Write(register.Definition.Store.Generation);
                writer.Write(register.Definition.Capacity); writer.Write((int)register.Definition.Kind);
                writer.Write(register.Definition.Owner is not null);
                if (register.Definition.Owner is { } actor) { writer.Write(actor.Id); writer.Write(actor.Generation); }
                break;
            case SeedStorageFile seed:
                if (!StorageWorld.IsValidFileDefinition(seed.Definition)) return null;
                var seedFingerprint = StorageWorld.FingerprintOperation(new(key, new("storage.bootstrap", 1), new CreateStorageFile(seed.Store, seed.Definition)));
                if (seedFingerprint is null) return null;
                writer.Write((byte)2); writer.Write(seedFingerprint); writer.Write(seed.Preload);
                break;
            case SubmitStorageOperation submit:
                var operation = StorageWorld.FingerprintOperation(submit.Operation);
                if (operation is null) return null;
                writer.Write((byte)3); writer.Write(operation);
                break;
            case ExpireStorageOffers expiry when expiry.AtDs >= 0:
                writer.Write((byte)4); writer.Write(key.Id); writer.Write(expiry.AtDs);
                break;
            case RemoveStorage remove:
                if (!StorageWorld.IsValidStoreDefinition(new(remove.Store, 0))) return null;
                writer.Write((byte)5); writer.Write(key.Id); writer.Write(remove.Store.Id); writer.Write(remove.Store.Generation);
                break;
            default: return null;
        }
        writer.Flush();
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }
}
