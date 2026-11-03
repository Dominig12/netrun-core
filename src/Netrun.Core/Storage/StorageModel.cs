using Netrun.Core.Entities;
using Netrun.Core.Transport;

namespace Netrun.Core.Storage;

/// <summary>Точное поколение носителя; смена физического диска не переиспользует старые capabilities.</summary>
public sealed record StoreKey(string Id, ulong Generation);
/// <summary>Идентификатор принадлежащего Core файла; копия всегда имеет новый UID.</summary>
public sealed record FileKey(string Id, ulong Generation);
/// <summary>Вид носителя; node/shard/local/session остаются отдельными владельцами.</summary>
public enum StorageMediumKind { Node, Shard, Local, Session, Salvage }

/// <summary>Доступ actor к целому носителю; права не выводятся из известного UID.</summary>
[Flags]
public enum StorageRights
{
    /// <summary>Нет прав.</summary>
    None = 0,
    /// <summary>Читать файлы и использовать источник копирования.</summary>
    Read = 1,
    /// <summary>Вставлять, редактировать и переименовывать.</summary>
    Write = 2,
    /// <summary>Удалять или перемещать исходные файлы.</summary>
    Delete = 4,
    /// <summary>Отправлять файлы с этого носителя.</summary>
    Transfer = 8,
    /// <summary>Trusted Core-модуль может создавать специальные executable/capability файлы; обычное владение этого не даёт.</summary>
    CreateSpecial = 16,
    /// <summary>Все штатные права; владение выдаёт их только точному ActorKey.</summary>
    All = Read | Write | Delete | Transfer
}

/// <summary>Свойства файла независимы от ACL целого носителя.</summary>
[Flags]
public enum FileCapabilities
{
    /// <summary>Никакие пользовательские действия не разрешены.</summary>
    None = 0,
    /// <summary>Можно создавать независимые копии.</summary>
    Copy = 1,
    /// <summary>Можно передавать через границу носителя.</summary>
    Transfer = 2,
    /// <summary>Можно хранить на разрешённом носителе.</summary>
    Store = 4,
    /// <summary>Можно удалить при writable носителе.</summary>
    Delete = 8,
    /// <summary>Можно менять имя; редактирование дополнительно требует kind=text.</summary>
    Edit = 16,
    /// <summary>Обычный переносимый сетевой файл.</summary>
    Ordinary = Copy | Transfer | Store | Delete | Edit
}

/// <summary>Immutable факт носителя. Capacity=0 означает read-only; preload может сознательно переполнять диск.</summary>
public sealed record StorageDefinition(StoreKey Store, int Capacity, StorageMediumKind Kind = StorageMediumKind.Local,
    ActorKey? Owner = null);
/// <summary>Точная область authority; оригинальные stamps переносятся копированием без повышения прав.</summary>
public sealed record FileGrant(string ScopeId, ulong Generation, string Domain, ulong Epoch, int Level);
/// <summary>Живой stamp области; он поступает через факты, а не через native callback при копировании.</summary>
public sealed record StorageScopeFact(string ScopeId, ulong Generation, string Domain, ulong Epoch);

/// <summary>
/// Шаблон файла. Size — базовая оболочка; размер .bot включает Children ровно один раз.
/// Metadata содержит immutable фото/techweb/agent persona/behavior данные, без native объекта.
/// Children=null у .bot сохраняет штатный loadout при запуске, пустой список явно задаёт пустую память.
/// </summary>
public sealed record FileDefinition(string Name, string Kind = "text", string Content = "", int Size = 1,
    FileCapabilities Capabilities = FileCapabilities.Ordinary, bool FirmwareManual = false,
    StoreKey? BoundStore = null, string? SessionId = null, NetworkValue? Metadata = null,
    ValueList<FileDefinition>? Children = null, ValueList<FileGrant>? Grants = null);

/// <summary>Неизменяемая полная проекция одного принадлежащего Core файла.</summary>
public sealed record StoredFileView(FileKey File, ulong Revision, string Name, string Kind, string Content, int Size,
    int BaseSize, int CopySize, FileCapabilities Capabilities, bool FirmwareManual, StoreKey? BoundStore,
    string? SessionId, NetworkValue Metadata, ValueList<StoredFileView>? Children, ValueList<FileGrant> Grants);
/// <summary>Версионированная проекция носителя; чтение не консолидирует ключи и не мутирует содержимое.</summary>
public sealed record StorageView(StoreKey Store, ulong Revision, int Capacity, long Used,
    StorageMediumKind Kind, ActorKey? Owner, ValueList<StoredFileView> Files);
/// <summary>Старый acquired-key UID был объединён с каноническим survivor этого носителя.</summary>
public sealed record FileReplacement(FileKey Previous, FileKey Current);
/// <summary>Структура носителя уже полностью изменена до публикации одного факта.</summary>
public sealed record StorageChanged(StoreKey Store, ulong Revision, ValueList<FileReplacement> Replacements, bool Removed = false);

/// <summary>Закрытый набор пользовательских операций; не содержит операций с физической файловой системой ОС.</summary>
public abstract record StorageAction;
/// <summary>Создаёт сетевой файл после проверки capacity и file capabilities.</summary>
public sealed record CreateStorageFile(StoreKey Store, FileDefinition Definition) : StorageAction;
/// <summary>Копирование создаёт новые UID всему дереву; MOVE сохраняет каждый оригинальный UID.</summary>
public sealed record TransferStorageFile(StoreKey Source, StoreKey Destination, FileKey File, bool Move = false) : StorageAction;
/// <summary>Удаление принадлежит исходному носителю.</summary>
public sealed record DeleteStorageFile(StoreKey Store, FileKey File) : StorageAction;
/// <summary>Только обычный text; 10000 Unicode символов, округление capacity вверх по 500.</summary>
public sealed record EditStorageText(StoreKey Store, FileKey File, string Content) : StorageAction;
/// <summary>Меняет имя конкретного исходного файла.</summary>
public sealed record RenameStorageFile(StoreKey Store, FileKey File, string Name) : StorageAction;
/// <summary>Сохраняет всю существующую память агента внутри его .bot, без копирования или частичного удаления.</summary>
public sealed record PackAgentMemory(StoreKey Memory, StoreKey PackageStore, FileKey Package, NetworkValue Metadata) : StorageAction;
/// <summary>Заменяет loadout атомарно, сохраняя оригинальные UID вложенной памяти и metadata оболочки.</summary>
public sealed record RestoreAgentMemory(StoreKey PackageStore, FileKey Package, StoreKey Memory) : StorageAction;
/// <summary>Создаёт инертный снимок предложения; не монтирует файл и не забирает оригинал отправителя.</summary>
public sealed record OfferStorageFile(StoreKey Source, StoreKey Recipient, FileKey File, string SenderName) : StorageAction;
/// <summary>Отказ уничтожает только снимок; отсутствие места при принятии сохраняет предложение.</summary>
public sealed record RespondStorageOffer(StoreKey Store, string OfferId, bool Accept) : StorageAction;
/// <summary>Выполняет ACL-проверку без изменения revision.</summary>
public sealed record ReadStorage(StoreKey Store) : StorageAction;

/// <summary>Монотонная identity запроса; revision необязательна только для доверенных серверных intent.</summary>
public sealed record StorageOperation(RequestKey Key, ActorKey Actor, StorageAction Action, ulong? ExpectedRevision = null);
/// <summary>Scalar outcome для replay ledger без удержания первоначального содержимого или file tree.</summary>
public sealed record StorageResult(bool Accepted, string Code, FileKey? File = null, StoreKey? Store = null,
    ulong Revision = 0, bool Duplicate = false, string? OfferId = null, bool WouldBlock = false);
/// <summary>Входящее предложение скрывает payload до принятия; expires — игровое время в децисекундах.</summary>
public sealed record StorageOfferView(string Id, StoreKey Store, string SenderName, string Name, string Kind,
    int Size, long ExpiresAtDs, bool CanAccept);
/// <summary>Локальные пределы формата/памяти не создают scheduler; команды вызывает центральная очередь Core.</summary>
public sealed record StorageWorldOptions(int MaxStores = int.MaxValue, int MaxFiles = int.MaxValue,
    int MaxEvents = int.MaxValue, int MaxProducers = int.MaxValue, int HistoryCapacity = 256,
    int MaxTreeDepth = 32, int MaxTreeFiles = 4096);
