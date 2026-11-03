namespace Netrun.Core.Access;

/// <summary>Уровень сетевой authority; совпадает с существующими NETRUN NONE/GUEST/USER/ROOT.</summary>
public enum AccessLevel
{
    /// <summary>Прав нет.</summary>
    None,
    /// <summary>Гостевое чтение; не тождественно праву перемещения.</summary>
    Guest,
    /// <summary>Управление узлом и допуск перемещения.</summary>
    User,
    /// <summary>Администрирование узла.</summary>
    Root
}

/// <summary>Поколение домена authority; повторный сетевой Id не восстанавливает права прежнего native-объекта.</summary>
public sealed record NetworkKey(string Id, ulong Generation);

/// <summary>Факты текущего native principal и его реальных credential sources; derived node levels сюда не входят.</summary>
public sealed record EntityPrincipalFacts(string SessionId, string PrincipalId, ValueList<int> AccessCodes,
    string? NativeOwnerId = null, string? FactionId = null, string? FactionFamily = null,
    bool IsEntity = true, bool Sanctioned = false, bool Locked = false,
    string? MasterSessionId = null, string? MasterPrincipalId = null, string? PersonalFactionId = null);

/// <summary>Нативные требования узла; null RequiredAccess сохраняет отличие от явно пустого списка ANY.</summary>
public sealed record NodeAccessPolicy(NodeKey Node, NetworkKey? Network, ulong KeyEpoch,
    ValueList<int>? RequiredAccess = null, bool IsObject = true, ValueList<int>? RequireAll = null,
    ValueList<int>? RequireAny = null, ValueList<string>? NativeOwners = null, FortressAccessPolicy? Fortress = null);

/// <summary>Факты домена, его отзываемого ключа и допустимых ID для легального гостевого чтения.</summary>
public sealed record NetworkAccessPolicy(NetworkKey Key, string KeyEpoch, ValueList<int> GuestAccess);

/// <summary>Факты крепости для вычисления gate friendship; native adapter не вычисляет результат доступа.</summary>
public sealed record FortressAccessPolicy(bool HasActiveGate, string FactionId, string? FactionFamily = null,
    string? OwnerFactionId = null, string? OwnerSessionId = null, string? OwnerPrincipalId = null,
    string? OwnerFactionFamily = null);

/// <summary>
/// Полученный node grant. Полный scope проверяет exact домен, его key stamp и epoch узла;
/// три null scope поля означают существующий unsigned direct/residency grant текущей сессии.
/// </summary>
public sealed record NodeAccessGrant(NodeKey Node, AccessLevel Level, NetworkKey? ScopeNetwork = null,
    string? ScopeKeyEpoch = null, ulong? ScopeNodeEpoch = null);

/// <summary>Полученный LAN grant; null scope stamp допустим только для существующего session-local direct grant.</summary>
public sealed record NetworkAccessGrant(NetworkKey Network, AccessLevel Level, string? ScopeKeyEpoch = null);

/// <summary>Реально присутствующий обычный LAN key: исходный Id и stamp содержимого, без вычисленного уровня.</summary>
public sealed record NetworkKeyCredential(string NetworkId, string KeyEpoch);

/// <summary>Immutable snapshot сырых policy/credential facts одной сущности; authority вычисляется исключительно в Core.</summary>
public sealed record EntityAccessSnapshot(EntityPrincipalFacts Principal, ValueList<NodeAccessPolicy> Nodes,
    ValueList<NetworkAccessPolicy> Networks, ValueList<NodeAccessGrant> NodeGrants,
    ValueList<NetworkAccessGrant> NetworkGrants, ValueList<NetworkKeyCredential> NetworkKeys);
