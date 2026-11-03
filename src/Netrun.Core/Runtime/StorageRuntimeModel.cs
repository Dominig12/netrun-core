using Netrun.Core.Storage;
using Netrun.Core.Transport;

namespace Netrun.Core.Runtime;

/// <summary>Trusted bootstrap/world fact носителя; UI adapter не принимает этот control-plane intent от пользователя.</summary>
public sealed record RegisterStorage(RequestKey Key, StorageDefinition Definition) : RuntimeCommand;
/// <summary>Trusted факт удаления физического носителя отзывает exact generation вместе со всеми его файлами.</summary>
public sealed record RemoveStorage(RequestKey Key, StoreKey Store) : RuntimeCommand;
/// <summary>Trusted bootstrap существующего сетевого payload; Preload сохраняет авторские переполненные носители.</summary>
public sealed record SeedStorageFile(RequestKey Key, StoreKey Store, FileDefinition Definition, bool Preload = true) : RuntimeCommand;
/// <summary>Пользовательское файловое намерение поступает в единую FIFO очередь с actor/mod задачами.</summary>
public sealed record SubmitStorageOperation(StorageOperation Operation) : RuntimeCommand;

/// <summary>Конечный scalar outcome; не содержит recursive дерева файлов или binary/photo payload.</summary>
public sealed record StorageOperationCompleted(string BindingEpoch, RequestKey Key, StorageResult Result)
    : TransportEvent(BindingEpoch, Key);
/// <summary>Инвалидация версии носителя; большие данные читаются через отдельную read-model проекцию.</summary>
public sealed record StorageChangedProjection(string BindingEpoch, RequestKey Key, StorageChanged Change)
    : TransportEvent(BindingEpoch, Key);

/// <summary>Внутренний expiry intent: после deadline попадает в хвост той же FIFO, wire-kind отсутствует.</summary>
internal sealed record ExpireStorageOffers(RequestKey Key, long AtDs) : RuntimeCommand;
