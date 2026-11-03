using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Netrun.Core.Entities;
using Netrun.Core.Transport;

namespace Netrun.Core.Storage;

/// <summary>
/// Единоличный владелец сетевых носителей и immutable file payload. Все мутации атомарны на одном Core owner;
/// здесь нет socket/native I/O или собственного планировщика исполнения команд.
/// </summary>
public sealed class StorageWorld
{
    private sealed class Disk(StorageDefinition definition)
    {
        internal StorageDefinition Definition = definition;
        internal ulong Revision;
        internal readonly List<Entry> Files = [];
        internal Entry? CanonicalGrants;
        internal long Used;
    }

    private sealed class Entry(FileKey key, FileDefinition definition)
    {
        internal readonly FileKey Key = key;
        internal FileDefinition Definition = definition with { Children = null };
        internal ulong Revision = 1;
        internal List<Entry>? Children;
        internal int Size;
    }

    private sealed record Offer(string Id, StoreKey Recipient, string SenderName, Entry File, long ExpiresAtDs);
    private sealed record History(byte[] Fingerprint, StorageResult Result);
    private readonly string _uidPrefix;
    private readonly StorageWorldOptions _options;
    private readonly Dictionary<StoreKey, Disk> _stores = [];
    private readonly Dictionary<string, StoreKey> _liveStores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _storeGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<FileKey, Entry> _files = [];
    private readonly Dictionary<(StoreKey Store, ActorKey Actor), StorageRights> _rights = [];
    private readonly Dictionary<string, StorageScopeFact> _scopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<StorageChanged> _events = [];
    private readonly Dictionary<RequestKey, History> _history = [];
    private readonly Queue<RequestKey> _historyOrder = [];
    private readonly Dictionary<string, ulong> _watermarks = new(StringComparer.Ordinal);
    private readonly ProducerAdmissionFence _fence;
    private readonly Dictionary<string, Offer> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<StoreKey, List<string>> _mailboxes = [];
    private readonly SortedSet<(long At, string Id)> _offerDeadlines = [];
    private readonly Dictionary<ActorKey, long> _nextOffers = [];
    private ulong _uid, _offerSerial;
    private long _now;

    /// <summary>UID prefix задаётся epoch владельца; новый Host не выдаёт UID старой инкарнации заново.</summary>
    public StorageWorld(string uidPrefix, StorageWorldOptions? options = null)
    {
        if (!ValidId(uidPrefix) || uidPrefix.Length > 100) throw new ArgumentException("Invalid storage prefix.", nameof(uidPrefix));
        _uidPrefix = uidPrefix;
        _options = options ?? new();
        if (_options.MaxStores < 1 || _options.MaxFiles < 1 || _options.MaxEvents < 1 || _options.MaxProducers < 1 ||
            _options.HistoryCapacity is < 1 or > 4096 || _options.MaxTreeDepth is < 1 or > 64 || _options.MaxTreeFiles < 1)
            throw new ArgumentException("Invalid storage options.", nameof(options));
        _fence = new(_watermarks, _options.MaxProducers);
    }

    /// <summary>Число известных носителей.</summary>
    public int StoreCount => _stores.Count;
    /// <summary>Все принадлежащие файлы, включая вложенные .bot и инертные предложения.</summary>
    public int FileCount => _files.Count;
    /// <summary>Количество непрочитанных storage-fact событий.</summary>
    public int EventCount => _events.Count;
    /// <summary>Центральная очередь Core может поставить один expiry intent на ближайший срок.</summary>
    public long? NextExpiryDs => _offerDeadlines.Count == 0 ? null : _offerDeadlines.Min.At;

    /// <summary>Точный масштаб DM; explicit capacity уже масштабирован и не умножается повторно.</summary>
    public static int DefaultNodeCapacity(string nodeType) => 8 * (nodeType switch
    {
        "ai" => 48,
        "server" => 32,
        "terminal" => 24,
        "gateway" => 16,
        "apc" => 12,
        _ => 8
    });

    /// <summary>Одна ячейка на каждые начатые 500 Unicode code points; минимум одна.</summary>
    public static int TextSize(string content) => Math.Max(1, checked((CountCharacters(content) + 499) / 500));

    /// <summary>Точный netrun_file_name: strip HTML, newline/tab в пробелы, удаление запрещённых знаков, первые 64 символа.</summary>
    public static string NormalizeFileName(string? value, string fallback = "document.txt")
    {
        if (!ValidText(value, NetworkValue.MaxTextLength)) return fallback;
        var clean = Regex.Replace(value!, "<[^>]*>", "", RegexOptions.NonBacktracking).Replace('\n', ' ').Replace('\t', ' ');
        var result = new StringBuilder();
        var count = 0;
        foreach (var rune in clean.EnumerateRunes())
        {
            if (rune.IsAscii && "/\\?%*:|\"<>".Contains((char)rune.Value)) continue;
            if (++count > 64) break;
            result.Append(rune);
        }
        var name = result.ToString().Trim();
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>Применяет world fact; уменьшение capacity не удаляет и не вытесняет существующие файлы.</summary>
    public bool RegisterStore(StorageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsValidStoreDefinition(definition)) return false;
        if (_stores.TryGetValue(definition.Store, out var disk))
        {
            if (disk.Definition == definition) return true;
            if (_events.Count >= _options.MaxEvents) return false;
            disk.Definition = definition;
            Publish(disk);
            return true;
        }
        if (_stores.Count >= _options.MaxStores || _events.Count >= _options.MaxEvents ||
            _storeGenerations.TryGetValue(definition.Store.Id, out var generation) && definition.Store.Generation <= generation)
            return false;
        // У одного opaque ID не бывает двух одновременно живых поколений.
        if (_liveStores.ContainsKey(definition.Store.Id)) return false;
        _storeGenerations[definition.Store.Id] = definition.Store.Generation;
        disk = new(definition);
        _stores.Add(definition.Store, disk);
        _liveStores.Add(definition.Store.Id, definition.Store);
        Publish(disk);
        return true;
    }

    /// <summary>Trusted факт физического удаления; заимствовавшие UI не являются владельцами файлов.</summary>
    public bool RemoveStore(StoreKey key)
    {
        if (_events.Count >= _options.MaxEvents || !_stores.Remove(key, out var disk)) return false;
        _liveStores.Remove(key.Id);
        foreach (var entry in disk.Files) Destroy(entry);
        foreach (var rights in _rights.Keys.Where(pair => pair.Store == key).ToArray()) _rights.Remove(rights);
        if (_mailboxes.TryGetValue(key, out var offers)) foreach (var id in offers.ToArray()) RemoveOffer(id, destroy: true);
        _mailboxes.Remove(key);
        _events.Enqueue(new(key, disk.Revision + 1, new([]), Removed: true));
        return true;
    }

    /// <summary>ACL публикует trusted authority owner; пользовательские storage-команды не могут выдать эти права.</summary>
    public void Grant(StoreKey store, ActorKey actor, StorageRights rights)
    {
        if (!ValidStore(store) || !ValidActor(actor) || (rights & ~(StorageRights.All | StorageRights.CreateSpecial)) != 0)
            throw new ArgumentException("Invalid storage grant.");
        if (rights == StorageRights.None) _rights.Remove((store, actor)); else _rights[(store, actor)] = rights;
    }

    /// <summary>Обновляет живой stamp, не переписывая старые сохранённые acquired grants.</summary>
    public void UpdateScope(StorageScopeFact fact)
    {
        if (!ValidId(fact.ScopeId) || !ValidId(fact.Domain) || fact.Generation == 0 || fact.Epoch == 0)
            throw new ArgumentException("Invalid storage scope.", nameof(fact));
        _scopes[fact.ScopeId] = fact;
    }

    /// <summary>Отзывает authority области при удалении, смене домена или ключа.</summary>
    public void RemoveScope(string scopeId) => _scopes.Remove(scopeId);

    /// <summary>Факт погружения: private snapshot допустим только пока исходная session жива.</summary>
    public void SetSessionLive(string sessionId, bool live)
    {
        if (!ValidId(sessionId)) throw new ArgumentException("Invalid storage session.", nameof(sessionId));
        if (live) _sessions.Add(sessionId); else _sessions.Remove(sessionId);
    }

    /// <summary>Trusted bootstrap/create из validated Core-модуля; preload может превышать capacity, но не capabilities.</summary>
    public StorageResult SeedFile(StoreKey store, FileDefinition definition, bool preload = true)
    {
        if (!_stores.TryGetValue(store, out var disk)) return Failure("store_unavailable");
        if (!ValidateDefinition(definition, out var size, out var count) || !CanStoreTree(definition, disk)) return Failure("invalid_file");
        if (_files.Count > (long)_options.MaxFiles - count) return Failure("storage_capacity");
        if (_events.Count >= _options.MaxEvents) return Failure("storage_output_pressure");
        if (!preload && !CanFit(disk, definition.Kind, size)) return Failure("insufficient_storage");
        var entry = Materialize(definition);
        var stored = Insert(disk, entry, out var replacements);
        Publish(disk, replacements);
        return Success(disk, stored.Key);
    }

    /// <summary>Последовательная команда: replay сохраняет только SHA256 и scalar outcome, не исходный payload.</summary>
    public StorageResult Apply(StorageOperation operation, long nowDs)
    {
        if (!AdvanceClock(nowDs)) return Failure("clock_regressed");
        var fingerprint = Fingerprint(operation);
        if (fingerprint is null) return Failure("invalid_storage_operation");
        if (_history.TryGetValue(operation.Key, out var previous))
            return CryptographicOperations.FixedTimeEquals(fingerprint, previous.Fingerprint)
                ? previous.Result with { Duplicate = true } : Failure("request_conflict");
        if (_watermarks.TryGetValue(operation.Key.ProducerId, out var watermark) && operation.Key.Sequence <= watermark) return Failure("request_expired");
        if (_fence.Blocks(operation.Key)) return Block(operation.Key, "producer_backpressure");
        if (!_watermarks.ContainsKey(operation.Key.ProducerId) && _watermarks.Count >= _options.MaxProducers) return Block(operation.Key, "producer_capacity");
        var requiredEvents = RequiredEvents(operation.Action);
        StorageResult result;
        // Невозможная при этой конфигурации atomic reservation — terminal отказ. Иначе FIFO head
        // навсегда удерживал бы delete/restore задачи, которые могли освободить прочие ресурсы.
        if (requiredEvents > _options.MaxEvents) result = Failure("storage_event_capacity");
        else
        {
            if (_events.Count > (long)_options.MaxEvents - requiredEvents) return Block(operation.Key, "storage_output_pressure");
            var source = Source(operation.Action);
            if (!_stores.TryGetValue(source, out var disk)) result = Failure("store_unavailable");
            else if (operation.ExpectedRevision is { } expected && disk.Revision != expected) result = Failure("storage_revision_changed");
            else result = Execute(operation.Actor, operation.Action, disk);
        }
        if (result.WouldBlock) return Block(operation.Key, result.Code);
        _watermarks[operation.Key.ProducerId] = operation.Key.Sequence;
        _history.Add(operation.Key, new(fingerprint, result));
        _historyOrder.Enqueue(operation.Key);
        if (_historyOrder.Count > _options.HistoryCapacity) _history.Remove(_historyOrder.Dequeue());
        _fence.Complete(new(result.Accepted ? AdmissionStatus.Accepted : AdmissionStatus.Rejected, result.Code, operation.Key));
        return result;
    }

    /// <summary>Одна единица budget истекает одно предложение; вызов ставится обычным intent центральной очереди Core.</summary>
    public int ExpireOffers(long nowDs, int budget)
    {
        if (!AdvanceClock(nowDs) || budget < 0) throw new ArgumentOutOfRangeException(nameof(nowDs));
        var work = 0;
        while (work < budget && _offerDeadlines.Count > 0 && _offerDeadlines.Min.At <= _now)
        {
            if (_events.Count >= _options.MaxEvents) break;
            var offer = _offers[_offerDeadlines.Min.Id];
            RemoveOffer(offer.Id, destroy: true);
            if (_stores.TryGetValue(offer.Recipient, out var disk)) Publish(disk);
            work++;
        }
        return work;
    }

    /// <summary>Чтение — неизменяемая проекция без consolidation или скрытого удаления файлов.</summary>
    public StorageView? ReadStore(StoreKey key) => !_stores.TryGetValue(key, out var disk) ? null :
        new(key, disk.Revision, disk.Definition.Capacity, disk.Used, disk.Definition.Kind, disk.Definition.Owner,
            new(disk.Files.Select(View), int.MaxValue));

    /// <summary>Scalar revision lookup не материализует recursive file view при bootstrap/capacity обновлении.</summary>
    public ulong? ReadStoreRevision(StoreKey key) => _stores.TryGetValue(key, out var disk) ? disk.Revision : null;

    /// <summary>Trusted внутренний lookup; права пользовательского чтения проверяются Apply(ReadStorage).</summary>
    public StoredFileView? ReadFile(FileKey key) => _files.TryGetValue(key, out var entry) ? View(entry) : null;

    /// <summary>Проверка capability использования включает живую session и точное владение, а не один известный UID.</summary>
    public bool CanUseFile(ActorKey actor, StoreKey store, FileKey file) => _stores.TryGetValue(store, out var disk) &&
        Has(disk, actor, StorageRights.Read) && Top(disk, file) is { } entry && CanStore(entry.Definition, disk);

    /// <summary>Старые stamps не становятся действующими после копирования, перемещения или слияния ключа.</summary>
    public ValueList<FileGrant> ReadCurrentGrants(ActorKey actor, StoreKey store, FileKey file) =>
        CanUseFile(actor, store, file) && _files[file].Definition.Grants is { } grants
            ? new(grants.Where(ValidGrantNow), int.MaxValue) : new([]);

    /// <summary>Видимые, ещё не истекшие предложения; чтение само по себе не меняет mailbox.</summary>
    public ValueList<StorageOfferView> ReadOffers(StoreKey store)
    {
        if (!_stores.TryGetValue(store, out var disk) || !_mailboxes.TryGetValue(store, out var ids)) return new([]);
        return new(ids.Select(id => _offers[id]).Where(offer => offer.ExpiresAtDs > _now).Select(offer =>
            new StorageOfferView(offer.Id, store, offer.SenderName, offer.File.Definition.Name,
                offer.File.Definition.Kind, offer.File.Size, offer.ExpiresAtDs,
                CanStore(offer.File.Definition, disk) && CanFit(disk, offer.File.Definition.Kind, offer.File.Size))), 8);
    }

    /// <summary>Факт одного полностью завершённого изменения; не выполняет callback наблюдателя inline.</summary>
    public bool TryDequeueEvent(out StorageChanged? change) => _events.TryDequeue(out change);

    private StorageResult Execute(ActorKey actor, StorageAction action, Disk disk)
    {
        if (action is ReadStorage) return Has(disk, actor, StorageRights.Read) ? Success(disk) : Failure("storage_access_denied");
        return action switch
        {
            CreateStorageFile create => Create(actor, disk, create.Definition),
            TransferStorageFile transfer => Transfer(actor, disk, transfer),
            DeleteStorageFile deletion => Delete(actor, disk, deletion.File),
            EditStorageText edit => Edit(actor, disk, edit),
            RenameStorageFile rename => Rename(actor, disk, rename),
            PackAgentMemory pack => Pack(actor, disk, pack),
            RestoreAgentMemory restore => Restore(actor, disk, restore),
            OfferStorageFile offer => OfferFile(actor, disk, offer),
            RespondStorageOffer respond => Respond(actor, disk, respond),
            _ => Failure("unknown_storage_action")
        };
    }

    private StorageResult Create(ActorKey actor, Disk disk, FileDefinition definition)
    {
        if (!Has(disk, actor, StorageRights.Write)) return Failure("storage_access_denied");
        if (!Has(disk, actor, StorageRights.CreateSpecial) && (definition.Kind != "text" || definition.FirmwareManual ||
            definition.BoundStore is not null || definition.SessionId is not null || definition.Children is not null ||
            definition.Grants is { Count: > 0 } || definition.Capabilities != FileCapabilities.Ordinary)) return Failure("special_file_creation_denied");
        definition = definition with { Name = NormalizeFileName(definition.Name) };
        if (definition.Kind == "text" && !definition.FirmwareManual)
        {
            if (!ValidText(definition.Content, 10000)) return Failure("text_too_large");
            definition = definition with { Size = TextSize(definition.Content) };
        }
        return SeedFile(disk.Definition.Store, definition, preload: false);
    }

    private StorageResult Transfer(ActorKey actor, Disk source, TransferStorageFile transfer)
    {
        var file = Top(source, transfer.File);
        if (file is null) return Failure("file_unavailable");
        if (!_stores.TryGetValue(transfer.Destination, out var destination)) return Failure("destination_unavailable");
        if (!Has(source, actor, StorageRights.Read | StorageRights.Transfer) || !Has(destination, actor, StorageRights.Write) ||
            transfer.Move && !Has(source, actor, StorageRights.Delete)) return Failure("storage_access_denied");
        var denial = TransferDenial(source, destination, file, transfer.Move);
        if (denial is not null) return Failure(denial);
        if (!transfer.Move && _files.Count > (long)_options.MaxFiles - TreeCount(file)) return Failure("storage_capacity");
        var result = transfer.Move ? file : Clone(file);
        if (transfer.Move) { source.Files.Remove(file); source.Used -= file.Size; if (source.CanonicalGrants == file) source.CanonicalGrants = null; }
        var stored = Insert(destination, result, out var replacements);
        // Все изменения ownership уже завершены до первого observable storage факта.
        Publish(destination, replacements);
        if (transfer.Move) Publish(source);
        return Success(destination, stored.Key);
    }

    private StorageResult Delete(ActorKey actor, Disk disk, FileKey key)
    {
        var file = Top(disk, key);
        if (file is null) return Failure("file_unavailable");
        var privateDeletion = file.Definition.SessionId is { } session && _sessions.Contains(session) && file.Definition.BoundStore == disk.Definition.Store;
        if (!Has(disk, actor, StorageRights.Delete) || !privateDeletion &&
            (disk.Definition.Capacity <= 0 || !file.Definition.Capabilities.HasFlag(FileCapabilities.Delete))) return Failure("file_delete_denied");
        disk.Files.Remove(file); disk.Used -= file.Size;
        if (disk.CanonicalGrants == file) disk.CanonicalGrants = null;
        Destroy(file);
        Publish(disk);
        return Success(disk);
    }

    private StorageResult Edit(ActorKey actor, Disk disk, EditStorageText edit)
    {
        var file = Top(disk, edit.File);
        if (file is null) return Failure("file_unavailable");
        if (!Has(disk, actor, StorageRights.Write) || disk.Definition.Capacity <= 0 || file.Definition.FirmwareManual ||
            file.Definition.Kind != "text" || !file.Definition.Capabilities.HasFlag(FileCapabilities.Edit)) return Failure("file_edit_denied");
        if (!ValidText(edit.Content, 10000)) return Failure("text_too_large");
        var size = TextSize(edit.Content);
        if (disk.Used - file.Size + size > disk.Definition.Capacity) return Failure("insufficient_storage");
        disk.Used += size - file.Size;
        file.Size = size; file.Definition = file.Definition with { Content = edit.Content, Size = size }; file.Revision++;
        Publish(disk);
        return Success(disk, file.Key);
    }

    private StorageResult Rename(ActorKey actor, Disk disk, RenameStorageFile rename)
    {
        var file = Top(disk, rename.File);
        if (file is null) return Failure("file_unavailable");
        if (!Has(disk, actor, StorageRights.Write) || disk.Definition.Capacity <= 0 ||
            !file.Definition.Capabilities.HasFlag(FileCapabilities.Edit) || string.IsNullOrWhiteSpace(rename.Name)) return Failure("file_rename_denied");
        file.Definition = file.Definition with { Name = NormalizeFileName(rename.Name, file.Definition.Name) }; file.Revision++;
        Publish(disk);
        return Success(disk, file.Key);
    }

    private StorageResult Pack(ActorKey actor, Disk memory, PackAgentMemory pack)
    {
        if (!_stores.TryGetValue(pack.PackageStore, out var packageStore) || Top(packageStore, pack.Package) is not { } package || package.Definition.Kind != "bot")
            return Failure("agent_package_unavailable");
        if (memory == packageStore || !Has(memory, actor, StorageRights.Read | StorageRights.Delete | StorageRights.Transfer) ||
            !Has(packageStore, actor, StorageRights.Write) || memory.Definition.Capacity <= 0 || packageStore.Definition.Capacity <= 0 ||
            !package.Definition.Capabilities.HasFlag(FileCapabilities.Edit) || !pack.Metadata.Validate()) return Failure("storage_access_denied");
        if (memory.Files.Any(file => !TransferAllowed(file, packageStore, move: true))) return Failure("file_transfer_denied");
        if (package.Children is { Count: > 0 }) return Failure("agent_package_not_empty");
        var size = (long)package.Definition.Size + memory.Used;
        if (size > int.MaxValue || packageStore.Used - package.Size + size > packageStore.Definition.Capacity) return Failure("insufficient_storage");
        var oldSize = package.Size;
        var children = memory.Files.ToList();
        package.Children = children; package.Size = (int)size;
        package.Definition = package.Definition with { Metadata = pack.Metadata }; package.Revision++;
        packageStore.Used += package.Size - oldSize;
        memory.Files.Clear(); memory.CanonicalGrants = null; memory.Used = 0;
        Publish(packageStore); Publish(memory);
        return Success(packageStore, package.Key);
    }

    private StorageResult Restore(ActorKey actor, Disk packageStore, RestoreAgentMemory restore)
    {
        var package = Top(packageStore, restore.Package);
        if (package is null || package.Definition.Kind != "bot" || !_stores.TryGetValue(restore.Memory, out var memory)) return Failure("agent_package_unavailable");
        if (memory == packageStore || !Has(packageStore, actor, StorageRights.Read | StorageRights.Write) || !Has(memory, actor, StorageRights.Write | StorageRights.Delete) ||
            memory.Definition.Capacity <= 0 || packageStore.Definition.Capacity <= 0 ||
            !package.Definition.Capabilities.HasFlag(FileCapabilities.Edit)) return Failure("storage_access_denied");
        if (package.Children is null) return Success(packageStore, package.Key);
        var required = package.Children.Sum(file => (long)file.Size);
        if (required > memory.Definition.Capacity) return Failure("insufficient_storage");
        if (package.Children.Any(file => !CanStoreEntryTree(file, memory)) ||
            memory.Files.Any(file => !file.Definition.Capabilities.HasFlag(FileCapabilities.Delete))) return Failure("file_restore_denied");
        var restored = package.Children;
        foreach (var file in memory.Files) Destroy(file);
        memory.Files.Clear(); memory.Used = 0; memory.CanonicalGrants = null;
        var replacements = new List<FileReplacement>();
        foreach (var file in restored)
        {
            Insert(memory, file, out var merged);
            replacements.AddRange(merged);
        }
        packageStore.Used -= package.Size - package.Definition.Size;
        package.Size = package.Definition.Size; package.Children = null; package.Revision++;
        Publish(memory, replacements); Publish(packageStore);
        return Success(packageStore, package.Key);
    }

    private StorageResult OfferFile(ActorKey actor, Disk source, OfferStorageFile offer)
    {
        if (!_stores.TryGetValue(offer.Recipient, out var destination) || Top(source, offer.File) is not { } original) return Failure("file_unavailable");
        if (!Has(source, actor, StorageRights.Read | StorageRights.Transfer) || _now < _nextOffers.GetValueOrDefault(actor) ||
            !ValidName(offer.SenderName)) return Failure("file_offer_denied");
        // Истёкшие письма освобождают snapshots до admission нового письма, даже если expiry intent ещё в FIFO.
        if (_mailboxes.TryGetValue(offer.Recipient, out var incoming))
            foreach (var id in incoming.ToArray()) if (_offers[id].ExpiresAtDs <= _now) RemoveOffer(id, destroy: true);
        if (incoming is { Count: >= 8 } || destination.Definition.Capacity < CopySize(original) ||
            !TransferAllowed(original, destination, move: false) || !CanCopyTree(original)) return Failure("file_offer_denied");
        if (_files.Count > (long)_options.MaxFiles - TreeCount(original)) return Failure("storage_capacity");
        var file = Clone(original);
        var idText = "offer-" + (++_offerSerial).ToString(CultureInfo.InvariantCulture);
        var snapshot = new Offer(idText, offer.Recipient, offer.SenderName, file, _now + 1200);
        _offers.Add(idText, snapshot);
        if (incoming is null) _mailboxes.Add(offer.Recipient, incoming = []);
        incoming.Add(idText);
        _offerDeadlines.Add((snapshot.ExpiresAtDs, idText));
        _nextOffers[actor] = _now + 20;
        Publish(destination);
        return new(true, "offered", file.Key, offer.Recipient, destination.Revision, OfferId: idText);
    }

    private StorageResult Respond(ActorKey actor, Disk disk, RespondStorageOffer respond)
    {
        if (!Has(disk, actor, StorageRights.Write) || !_offers.TryGetValue(respond.OfferId, out var offer) ||
            offer.Recipient != disk.Definition.Store || offer.ExpiresAtDs <= _now) return Failure("offer_unavailable");
        if (!respond.Accept) { RemoveOffer(offer.Id, destroy: true); Publish(disk); return Success(disk); }
        if (!CanStore(offer.File.Definition, disk) || !CanFit(disk, offer.File.Definition.Kind, offer.File.Size)) return Failure("insufficient_storage");
        RemoveOffer(offer.Id, destroy: false);
        var stored = Insert(disk, offer.File, out var replacements);
        Publish(disk, replacements);
        return Success(disk, stored.Key);
    }

    private void RemoveOffer(string id, bool destroy)
    {
        if (!_offers.Remove(id, out var offer)) return;
        _offerDeadlines.Remove((offer.ExpiresAtDs, id));
        if (_mailboxes.TryGetValue(offer.Recipient, out var incoming)) incoming.Remove(id);
        if (destroy) Destroy(offer.File);
    }

    private string? TransferDenial(Disk source, Disk destination, Entry file, bool move)
    {
        if (move && source == destination) return "same_storage";
        if (move && source.Definition.Capacity <= 0) return "source_read_only";
        if (!TransferAllowed(file, destination, move) || !move && !CanCopyTree(file)) return "file_transfer_denied";
        if (destination.Definition.Capacity <= 0) return "destination_read_only";
        return CanFit(destination, file.Definition.Kind, move ? file.Size : CopySize(file)) ? null : "insufficient_storage";
    }

    private bool TransferAllowed(Entry file, Disk destination, bool move) =>
        file.Definition.Capabilities.HasFlag(FileCapabilities.Transfer) && CanStore(file.Definition, destination) &&
        (move || file.Definition.Capabilities.HasFlag(FileCapabilities.Copy)) &&
        (file.Children is null || file.Children.All(child => TransferAllowed(child, destination, move)));

    private bool CanStore(FileDefinition definition, Disk disk) => definition.Capabilities.HasFlag(FileCapabilities.Store) &&
        (definition.BoundStore is null || definition.BoundStore == disk.Definition.Store) &&
        (definition.SessionId is null || _sessions.Contains(definition.SessionId));

    private bool CanStoreTree(FileDefinition definition, Disk disk) => CanStore(definition, disk) &&
        (definition.Children is null || definition.Children.All(child => CanStoreTree(child, disk)));

    private bool CanStoreEntryTree(Entry entry, Disk disk) => CanStore(entry.Definition, disk) &&
        (entry.Children is null || entry.Children.All(child => CanStoreEntryTree(child, disk)));

    private static bool CanFit(Disk disk, string kind, int size) => disk.Definition.Capacity > 0 && disk.Used <= disk.Definition.Capacity &&
        (kind == "grants" && Canonical(disk) is not null || disk.Used + size <= disk.Definition.Capacity);

    private bool Has(Disk disk, ActorKey actor, StorageRights required)
    {
        var rights = _rights.GetValueOrDefault((disk.Definition.Store, actor));
        if (disk.Definition.Owner == actor) rights |= StorageRights.All;
        return (rights & required) == required;
    }

    private Entry Insert(Disk disk, Entry file, out List<FileReplacement> replacements)
    {
        replacements = [];
        if (file.Definition.Kind == "grants" && Canonical(disk) is { } canonical)
        {
            var grants = (canonical.Definition.Grants ?? new([])).ToDictionary(grant => grant.ScopeId, StringComparer.Ordinal);
            foreach (var grant in file.Definition.Grants ?? new([]))
            {
                if (!ValidGrantNow(grant)) continue;
                if (grants.TryGetValue(grant.ScopeId, out var previous) && ValidGrantNow(previous) && previous.Level >= grant.Level) continue;
                grants[grant.ScopeId] = grant;
            }
            canonical.Definition = canonical.Definition with { Grants = new(grants.Values, int.MaxValue), Size = 1 };
            canonical.Revision++;
            replacements.Add(new(file.Key, canonical.Key));
            Destroy(file);
            return canonical;
        }
        disk.Files.Add(file); disk.Used += file.Size;
        if (file.Definition.Kind == "grants") disk.CanonicalGrants = file;
        return file;
    }

    private bool ValidGrantNow(FileGrant grant) => _scopes.TryGetValue(grant.ScopeId, out var scope) &&
        grant.Generation == scope.Generation && grant.Domain == scope.Domain && grant.Epoch == scope.Epoch && grant.Level is >= 1 and <= 3;

    private static Entry? Canonical(Disk disk) => disk.CanonicalGrants ??= disk.Files.Find(file => file.Definition.Kind == "grants");
    private static Entry? Top(Disk disk, FileKey key) => disk.Files.Find(file => file.Key == key);
    private static int TreeCount(Entry entry) => 1 + (entry.Children?.Sum(TreeCount) ?? 0);
    private static bool CanCopyTree(Entry entry) => entry.Definition.Capabilities.HasFlag(FileCapabilities.Copy) &&
        (entry.Children is null || entry.Children.All(CanCopyTree));
    private static int CopySize(Entry entry) => checked((entry.Definition.FirmwareManual ? TextSize(entry.Definition.Content) : entry.Definition.Size) +
        (entry.Children?.Sum(CopySize) ?? 0));

    private Entry Materialize(FileDefinition definition)
    {
        var entry = new Entry(new(_uidPrefix + ":" + (++_uid).ToString(CultureInfo.InvariantCulture), 1), definition);
        if (definition.Children is not null) entry.Children = definition.Children.Select(Materialize).ToList();
        entry.Size = checked(definition.Size + (entry.Children?.Sum(child => child.Size) ?? 0));
        _files.Add(entry.Key, entry);
        return entry;
    }

    private Entry Clone(Entry file)
    {
        var definition = file.Definition.FirmwareManual ? file.Definition with { FirmwareManual = false, Size = TextSize(file.Definition.Content) } : file.Definition;
        var copy = new Entry(new(_uidPrefix + ":" + (++_uid).ToString(CultureInfo.InvariantCulture), 1), definition);
        if (file.Children is not null) copy.Children = file.Children.Select(Clone).ToList();
        copy.Size = checked(definition.Size + (copy.Children?.Sum(child => child.Size) ?? 0));
        _files.Add(copy.Key, copy);
        return copy;
    }

    private void Destroy(Entry entry)
    {
        _files.Remove(entry.Key);
        if (entry.Children is not null) foreach (var child in entry.Children) Destroy(child);
    }

    private void Publish(Disk disk, List<FileReplacement>? replacements = null)
    {
        disk.Revision++;
        _events.Enqueue(new(disk.Definition.Store, disk.Revision, new(replacements ?? [], int.MaxValue)));
    }

    private static StorageResult Success(Disk disk, FileKey? file = null) => new(true, "accepted", file, disk.Definition.Store, disk.Revision);
    private static StorageResult Failure(string code) => new(false, code,
        WouldBlock: code is "storage_output_pressure" or "producer_capacity" or "producer_backpressure");
    private StorageResult Block(RequestKey key, string code)
    {
        _fence.Complete(new(AdmissionStatus.WouldBlock, code, key));
        return Failure(code);
    }
    private bool AdvanceClock(long now)
    {
        if (now < _now || now < 0 || now > long.MaxValue - 1200) return false;
        _now = now; return true;
    }

    private static StoredFileView View(Entry file) => new(file.Key, file.Revision, file.Definition.Name,
        file.Definition.Kind, file.Definition.Content, file.Size, file.Definition.Size, CopySize(file),
        file.Definition.Capabilities, file.Definition.FirmwareManual, file.Definition.BoundStore, file.Definition.SessionId,
        file.Definition.Metadata ?? NetworkValue.Null,
        file.Children is null ? null : new(file.Children.Select(View), int.MaxValue), file.Definition.Grants ?? new([]));

    private bool ValidateDefinition(FileDefinition? definition, out int size, out int count)
        => ValidateDefinition(definition, _options, out size, out count);

    private static bool ValidateDefinition(FileDefinition? definition, StorageWorldOptions options, out int size, out int count)
    {
        long total = 0;
        var nodes = 0;
        bool Walk(FileDefinition? file, int depth)
        {
            if (file is null || depth > options.MaxTreeDepth || ++nodes > options.MaxTreeFiles || !ValidName(file.Name) ||
                !ValidId(file.Kind) || !ValidText(file.Content, NetworkValue.MaxTextLength) || file.Size < 0 ||
                (file.Capabilities & ~FileCapabilities.Ordinary) != 0 || file.Metadata is { } metadata && !metadata.Validate() ||
                file.BoundStore is not null && !ValidStore(file.BoundStore) || file.SessionId is not null && !ValidId(file.SessionId) ||
                file.Children is not null && file.Kind != "bot" || file.FirmwareManual && (file.Kind != "text" || file.Size != 0) ||
                file.Kind == "grants" && file.Size != 1) return false;
            if (file.Grants is { } grants)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var grant in grants)
                    if (grant is null || !ValidId(grant.ScopeId) || !ValidId(grant.Domain) || grant.Generation == 0 || grant.Epoch == 0 ||
                        grant.Level is < 1 or > 3 || !seen.Add(grant.ScopeId)) return false;
                if (grants.Count > 0 && file.Kind != "grants") return false;
            }
            total += file.Size;
            if (total > int.MaxValue) return false;
            return file.Children is null || file.Children.All(child => Walk(child, depth + 1));
        }
        var valid = Walk(definition, 1);
        size = valid ? (int)total : 0; count = nodes;
        return valid;
    }

    private static int CountCharacters(string value)
    {
        var count = 0;
        foreach (var rune in value.EnumerateRunes()) count++;
        return count;
    }
    private static bool ValidText(string? value, int maximum)
    {
        if (value is null) return false;
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsSurrogate(value[index]))
            {
                if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                index++;
            }
            if (++count > maximum) return false;
        }
        return true;
    }
    private static bool ValidId(string? value) => value is { Length: >= 1 and <= 128 } && !value.Any(char.IsControl);
    private static bool ValidName(string? value) => value is not null && ValidText(value, 64) && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static bool ValidStore(StoreKey? store) => store is { Generation: > 0 } && ValidId(store.Id);
    private static bool ValidActor(ActorKey? actor) => actor is { Generation: > 0 } && ValidId(actor.Id);

    /// <summary>Чистая shape-проверка bootstrap факта без открытия runtime или обращения к существующему Store state.</summary>
    public static bool IsValidStoreDefinition(StorageDefinition? definition) => definition is not null &&
        ValidStore(definition.Store) && definition.Capacity >= 0 && Enum.IsDefined(definition.Kind) &&
        (definition.Owner is null || ValidActor(definition.Owner));

    /// <summary>Чистая проверка immutable file tree; capabilities конкретного носителя проверяет доменный owner при Seed/Apply.</summary>
    public static bool IsValidFileDefinition(FileDefinition? definition, StorageWorldOptions? options = null) =>
        ValidateDefinition(definition, options ?? new(), out _, out _);

    private static int RequiredEvents(StorageAction action) => action switch
    {
        TransferStorageFile transfer => transfer.Move ? 2 : 1,
        PackAgentMemory or RestoreAgentMemory => 2,
        ReadStorage => 0,
        _ => 1
    };

    private static StoreKey Source(StorageAction action) => action switch
    {
        CreateStorageFile create => create.Store,
        TransferStorageFile transfer => transfer.Source,
        DeleteStorageFile deletion => deletion.Store,
        EditStorageText edit => edit.Store,
        RenameStorageFile rename => rename.Store,
        PackAgentMemory pack => pack.Memory,
        RestoreAgentMemory restore => restore.PackageStore,
        OfferStorageFile offer => offer.Source,
        RespondStorageOffer respond => respond.Store,
        ReadStorage read => read.Store,
        _ => new("invalid", 0)
    };

    /// <summary>Использует ту же чистую canonical реализацию с локальными bounds этого владельца.</summary>
    public byte[]? Fingerprint(StorageOperation? operation) => FingerprintOperation(operation, _options);

    /// <summary>Чистый semantic fingerprint для Protocol/runtime causal ledger без mutable store state и временного owner.</summary>
    public static byte[]? FingerprintOperation(StorageOperation? operation, StorageWorldOptions? options = null)
    {
        if (operation is null || !TransportFingerprint.ValidKey(operation.Key) || !ValidActor(operation.Actor) || operation.Action is null || !ValidStore(Source(operation.Action))) return null;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
        writer.Write("NETRUN-STORAGE-1"); writer.Write(operation.Key.Id); writer.Write(operation.Actor.Id); writer.Write(operation.Actor.Generation);
        writer.Write(operation.ExpectedRevision.HasValue); if (operation.ExpectedRevision is { } revision) writer.Write(revision);
        void Store(StoreKey store) { writer.Write(store.Id); writer.Write(store.Generation); }
        void File(FileKey file) { writer.Write(file.Id); writer.Write(file.Generation); }
        void Definition(FileDefinition file)
        {
            writer.Write(file.Name); writer.Write(file.Kind); writer.Write(file.Content); writer.Write(file.Size); writer.Write((int)file.Capabilities);
            writer.Write(file.FirmwareManual); writer.Write(file.BoundStore is not null); if (file.BoundStore is { } bound) Store(bound);
            writer.Write(file.SessionId is not null); if (file.SessionId is { } session) writer.Write(session);
            TransportFingerprint.WriteValue(writer, file.Metadata ?? NetworkValue.Null);
            writer.Write(file.Children is not null); if (file.Children is { } children) { writer.Write(children.Count); foreach (var child in children) Definition(child); }
            var grants = file.Grants ?? new([]); writer.Write(grants.Count);
            foreach (var grant in grants) { writer.Write(grant.ScopeId); writer.Write(grant.Generation); writer.Write(grant.Domain); writer.Write(grant.Epoch); writer.Write(grant.Level); }
        }
        switch (operation.Action)
        {
            case CreateStorageFile create:
                if (create.Definition is null || !ValidText(create.Definition.Name, NetworkValue.MaxTextLength)) return null;
                var normalized = create.Definition with { Name = NormalizeFileName(create.Definition.Name) };
                if (!ValidateDefinition(normalized, options ?? new(), out _, out _)) return null;
                writer.Write((byte)1); Store(create.Store); Definition(normalized); break;
            case TransferStorageFile transfer:
                if (!ValidStore(transfer.Destination) || transfer.File is not { Generation: > 0 } || !ValidId(transfer.File.Id)) return null;
                writer.Write((byte)2); Store(transfer.Source); Store(transfer.Destination); File(transfer.File); writer.Write(transfer.Move); break;
            case DeleteStorageFile deletion:
                if (deletion.File is not { Generation: > 0 } || !ValidId(deletion.File.Id)) return null;
                writer.Write((byte)3); Store(deletion.Store); File(deletion.File); break;
            case EditStorageText edit:
                if (edit.File is not { Generation: > 0 } || !ValidId(edit.File.Id) || !ValidText(edit.Content, 10000)) return null;
                writer.Write((byte)4); Store(edit.Store); File(edit.File); writer.Write(edit.Content); break;
            case RenameStorageFile rename:
                if (rename.File is not { Generation: > 0 } || !ValidId(rename.File.Id) || !ValidText(rename.Name, NetworkValue.MaxTextLength)) return null;
                writer.Write((byte)5); Store(rename.Store); File(rename.File); writer.Write(rename.Name); break;
            case PackAgentMemory pack:
                if (!ValidStore(pack.PackageStore) || pack.Package is not { Generation: > 0 } || !ValidId(pack.Package.Id) || pack.Metadata is null || !pack.Metadata.Validate()) return null;
                writer.Write((byte)6); Store(pack.Memory); Store(pack.PackageStore); File(pack.Package); TransportFingerprint.WriteValue(writer, pack.Metadata); break;
            case RestoreAgentMemory restore:
                if (!ValidStore(restore.Memory) || restore.Package is not { Generation: > 0 } || !ValidId(restore.Package.Id)) return null;
                writer.Write((byte)7); Store(restore.PackageStore); File(restore.Package); Store(restore.Memory); break;
            case OfferStorageFile offer:
                if (!ValidStore(offer.Recipient) || offer.File is not { Generation: > 0 } || !ValidId(offer.File.Id) || !ValidName(offer.SenderName)) return null;
                writer.Write((byte)8); Store(offer.Source); Store(offer.Recipient); File(offer.File); writer.Write(offer.SenderName); break;
            case RespondStorageOffer respond:
                if (!ValidId(respond.OfferId)) return null;
                writer.Write((byte)9); Store(respond.Store); writer.Write(respond.OfferId); writer.Write(respond.Accept); break;
            case ReadStorage read: writer.Write((byte)10); Store(read.Store); break;
            default: return null;
        }
        writer.Flush();
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }
}
