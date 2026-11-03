using System.Security.Cryptography;
using System.Text;

namespace Netrun.Core.Access;

/// <summary>
/// Владелец индексов raw policy/credential snapshot. Переносит node/network/traversal и fortress friendship
/// правила DM без чтения native мира, кэширования готовых DM levels или неявного открытия гостевой ЛВС.
/// </summary>
public sealed class EntityAccessAuthority
{
    /// <summary>Максимум policies и grants в одной форме snapshot; не лимит количества узлов всего runtime.</summary>
    public const int MaximumPolicies = 65_536;
    /// <summary>Максимум реальных credential keys одного snapshot.</summary>
    public const int MaximumCredentials = 4096;
    /// <summary>Максимум числовых ID codes одного native source/requirement.</summary>
    public const int MaximumAccessCodes = 4096;
    /// <summary>Максимум физических владельцев одного устройства.</summary>
    public const int MaximumOwners = 64;
    /// <summary>Существующий native ACCESS_CAPTAIN.</summary>
    private const int CaptainAccess = 20;
    /// <summary>Существующий native ACCESS_NETWORK; даёт гостя в LAN и ROOT после успешного USER admission.</summary>
    private const int NetworkAccess = 67;

    /// <summary>Immutable факты живут только до завершения incremental indexing.</summary>
    private EntityAccessSnapshot? _snapshot;
    /// <summary>Скалярные native identity/capability facts одной сущности.</summary>
    private readonly EntityPrincipalFacts _principal;
    /// <summary>Опциональная authority текущего Core graph; native policy не может выдать себя за прежнюю LAN привязку.</summary>
    private readonly Func<NodeKey, string?>? _expectedNetworkId;
    /// <summary>Фаза: codes, nodes, networks, node grants, network grants, keys.</summary>
    private int _phase;
    /// <summary>Следующий элемент текущей фазы.</summary>
    private int _cursor;
    /// <summary>Raw native ID codes, не сетевые уровни.</summary>
    private readonly HashSet<int> _codes = [];
    /// <summary>Поколения policies для O(1) анализа соседа.</summary>
    private readonly Dictionary<NodeKey, NodeAccessPolicy> _nodes = [];
    /// <summary>Exact network policies.</summary>
    private readonly Dictionary<NetworkKey, NetworkAccessPolicy> _networks = [];
    /// <summary>Ordinary keys адресуются native network Id; current generation выбирается из policy snapshot.</summary>
    private readonly Dictionary<string, NetworkAccessPolicy> _networkIds = new(StringComparer.Ordinal);
    /// <summary>Сильнейшие grants только после проверки их исходных stamps.</summary>
    private readonly Dictionary<NodeKey, AccessLevel> _nodeGrants = [];
    /// <summary>Сильнейшие валидные acquired LAN grants; не смешиваются с legit ID guest.</summary>
    private readonly Dictionary<NetworkKey, AccessLevel> _networkGrants = [];
    /// <summary>Домены, которым соответствует реальный ordinary key snapshot.</summary>
    private readonly HashSet<NetworkKey> _keys = [];

    /// <summary>Все индексы готовы; до этого любое чтение authority fail-closed.</summary>
    public bool Ready { get; private set; }
    /// <summary>Безопасный отказ конфликтующих policies; никакой partial authority после него не публикуется.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Принимает immutable raw facts; индексы строятся только в Advance.</summary>
    public EntityAccessAuthority(EntityAccessSnapshot snapshot, Func<NodeKey, string?>? expectedNetworkId = null)
    {
        if (!IsValidSnapshot(snapshot)) throw new ArgumentException("Invalid entity access snapshot.", nameof(snapshot));
        _snapshot = snapshot;
        _principal = snapshot.Principal;
        _expectedNetworkId = expectedNetworkId;
    }

    /// <summary>Индексирует не больше workBudget отдельных фактов; ни одна фаза не сканирует весь граф за unit.</summary>
    public int Advance(int workBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workBudget);
        var work = 0;
        while (work < workBudget && !Ready && ErrorCode is null)
        {
            var snapshot = _snapshot!;
            switch (_phase)
            {
                case 0:
                    if (_cursor < snapshot.Principal.AccessCodes.Count) _codes.Add(snapshot.Principal.AccessCodes[_cursor++]);
                    else NextPhase();
                    break;
                case 1:
                    if (_cursor < snapshot.Nodes.Count)
                    {
                        var fact = snapshot.Nodes[_cursor++];
                        if (_expectedNetworkId is not null && _expectedNetworkId(fact.Node) != fact.Network?.Id)
                            ErrorCode = "access_domain_changed";
                        else if (!_nodes.TryAdd(fact.Node, fact)) ErrorCode = "access_policy_conflict";
                    }
                    else NextPhase();
                    break;
                case 2:
                    if (_cursor < snapshot.Networks.Count)
                    {
                        var fact = snapshot.Networks[_cursor++];
                        if (!_networks.TryAdd(fact.Key, fact) || !_networkIds.TryAdd(fact.Key.Id, fact)) ErrorCode = "access_policy_conflict";
                    }
                    else NextPhase();
                    break;
                case 3:
                    if (_cursor < snapshot.NodeGrants.Count) AddGrant(snapshot.NodeGrants[_cursor++]);
                    else NextPhase();
                    break;
                case 4:
                    if (_cursor < snapshot.NetworkGrants.Count)
                    {
                        var grant = snapshot.NetworkGrants[_cursor++];
                        if (_networks.TryGetValue(grant.Network, out var policy) &&
                            (grant.ScopeKeyEpoch is null || grant.ScopeKeyEpoch == policy.KeyEpoch))
                            _networkGrants[grant.Network] = Max(_networkGrants.GetValueOrDefault(grant.Network), grant.Level);
                    }
                    else NextPhase();
                    break;
                case 5:
                    if (_cursor < snapshot.NetworkKeys.Count)
                    {
                        var key = snapshot.NetworkKeys[_cursor++];
                        if (_networkIds.TryGetValue(key.NetworkId, out var policy) && key.KeyEpoch == policy.KeyEpoch) _keys.Add(policy.Key);
                    }
                    else { Ready = true; _snapshot = null; }
                    break;
            }
            work++;
        }
        if (ErrorCode is not null) _snapshot = null;
        return work;
    }

    /// <summary>Повторяет can_traverse_node: USER, либо не вражеские врата и acquired LAN GUEST/ordinary key.</summary>
    public bool CanTraverse(NodeKey node, NodeKey source, bool physicallyReachable = true)
    {
        if (!Ready || _principal.Locked || !_nodes.TryGetValue(node, out var policy) ||
            policy.Network is null || !_networks.ContainsKey(policy.Network)) return false;
        if (NodeLevel(node, source, physicallyReachable) >= AccessLevel.User) return true;
        if (policy.Fortress is { HasActiveGate: true } fortress && !Friendly(policy, fortress)) return false;
        return _networkGrants.GetValueOrDefault(policy.Network) >= AccessLevel.Guest || _keys.Contains(policy.Network);
    }

    /// <summary>Повторяет compute_node_level; физическую достижимость caller доказывает Core-графом, а не native boolean.</summary>
    public AccessLevel NodeLevel(NodeKey node, NodeKey source, bool physicallyReachable = true)
    {
        if (!Ready || _principal.Locked || !_nodes.TryGetValue(node, out var policy) ||
            policy.Network is null || !_networks.TryGetValue(policy.Network, out var network)) return AccessLevel.None;
        var owner = OwnerLevel(policy);
        var acquired = _nodeGrants.GetValueOrDefault(node);
        var net = physicallyReachable ? Max(_networkGrants.GetValueOrDefault(network.Key), LegitNetworkLevel(network.Key)) : AccessLevel.None;
        if (physicallyReachable && _nodes.TryGetValue(source, out var origin) && origin.Network == network.Key) net = Max(net, AccessLevel.Guest);
        if (net < AccessLevel.Guest && node != source &&
            (owner == AccessLevel.None && acquired == AccessLevel.None || !physicallyReachable)) return AccessLevel.None;
        return Max(AccessLevel.Guest, Max(owner, Max(net, Max(acquired, LegitNodeLevel(node)))));
    }

    /// <summary>Легальные native ID/owner rights без acquired grants и physical reachability.</summary>
    public AccessLevel LegitNodeLevel(NodeKey node)
    {
        if (!Ready || !_nodes.TryGetValue(node, out var policy) || policy.Network is null || !_networks.ContainsKey(policy.Network)) return AccessLevel.None;
        var level = Max(LegitNetworkLevel(policy.Network), OwnerLevel(policy));
        if (_codes.Count == 0) return level;
        var user = policy.RequiredAccess is { } required ? required.Any(_codes.Contains) : policy.IsObject &&
            (policy.RequireAll is null || policy.RequireAll.All(_codes.Contains) &&
                (policy.RequireAny is not { Count: > 0 } any || any.Any(_codes.Contains)));
        if (!user) return level;
        return _codes.Contains(CaptainAccess) || _codes.Contains(NetworkAccess) ? AccessLevel.Root : Max(level, AccessLevel.User);
    }

    /// <summary>Ordinary key или native guest IDs дают только GUEST; отсутствие guest требований означает общедоступное чтение.</summary>
    public AccessLevel LegitNetworkLevel(NetworkKey network)
    {
        if (!Ready || !_networks.TryGetValue(network, out var policy)) return AccessLevel.None;
        return _keys.Contains(network) || policy.GuestAccess.Count == 0 || _codes.Contains(NetworkAccess) ||
            policy.GuestAccess.Any(_codes.Contains) ? AccessLevel.Guest : AccessLevel.None;
    }

    /// <summary>Исходный epoch сравнивается с актуальной policy без автоматического освежения старого scope.</summary>
    private void AddGrant(NodeAccessGrant grant)
    {
        if (!_nodes.TryGetValue(grant.Node, out var node)) return;
        if (grant.ScopeNetwork is not null && (node.Network != grant.ScopeNetwork ||
            node.KeyEpoch != grant.ScopeNodeEpoch || !_networks.TryGetValue(grant.ScopeNetwork, out var network) ||
            network.KeyEpoch != grant.ScopeKeyEpoch)) return;
        _nodeGrants[grant.Node] = Max(_nodeGrants.GetValueOrDefault(grant.Node), grant.Level);
    }

    /// <summary>Native owner relation остаётся фактом физического adapter; ROOT и network-level вычисляет Core.</summary>
    private AccessLevel OwnerLevel(NodeAccessPolicy node) => _principal.NativeOwnerId is { } owner &&
        node.NativeOwners?.Contains(owner) == true ? AccessLevel.Root : AccessLevel.None;

    /// <summary>Повторяет is_owner/is_friendly для personal, station и rogue крепостей.</summary>
    private bool Friendly(NodeAccessPolicy node, FortressAccessPolicy fortress)
    {
        var personal = _principal.PersonalFactionId ?? _principal.FactionId;
        if (fortress.OwnerFactionId is { } ownerFaction && ownerFaction != "owner" &&
            Family(ownerFaction, fortress.OwnerFactionFamily) == "owner")
        {
            if (ownerFaction == personal) return true;
        }
        else if (Same(fortress.OwnerSessionId, _principal.SessionId) || Same(fortress.OwnerPrincipalId, _principal.PrincipalId) ||
            _principal.IsEntity && (Same(fortress.OwnerSessionId, _principal.MasterSessionId) ||
                Same(fortress.OwnerPrincipalId, _principal.MasterPrincipalId))) return true;
        if (fortress.FactionId != "owner" && Family(fortress.FactionId, fortress.FactionFamily) == "owner")
            return fortress.FactionId == personal;
        return fortress.FactionId switch
        {
            "rogue" => _principal.IsEntity && _principal.FactionId == "rogue",
            "station" => _principal.IsEntity && _principal.Sanctioned || LegitNodeLevel(node.Node) >= AccessLevel.User,
            _ => false
        };
    }

    /// <summary>Переходит к следующему typed набору без сканирования пустых списков.</summary>
    private void NextPhase() { _phase++; _cursor = 0; }
    /// <summary>Максимум enum-уровней, уже проверенных на границе snapshot.</summary>
    private static AccessLevel Max(AccessLevel first, AccessLevel second) => first > second ? first : second;
    /// <summary>Null identity никогда не доказывает ownership.</summary>
    private static bool Same(string? first, string? second) => first is not null && second is not null && first == second;
    /// <summary>Сохраняет native fallback family для personal owner IDs после удаления registry entry.</summary>
    private static string Family(string id, string? declared) => declared ?? (id.StartsWith("owner:", StringComparison.Ordinal) ? "owner" : id);

    /// <summary>Проверяет форму всех raw facts, включая nullable различия requirements и полноту signed scope.</summary>
    public static bool IsValidSnapshot(EntityAccessSnapshot? snapshot) => snapshot is not null &&
        snapshot.Principal is { } principal && Text(principal.SessionId) && Text(principal.PrincipalId) && Codes(principal.AccessCodes) &&
        Optional(principal.NativeOwnerId) && Optional(principal.FactionId) && Optional(principal.FactionFamily) &&
        Optional(principal.MasterSessionId) && Optional(principal.MasterPrincipalId) && Optional(principal.PersonalFactionId) &&
        snapshot.Nodes is { Count: <= MaximumPolicies } && snapshot.Nodes.All(ValidNodePolicy) &&
        snapshot.Networks is { Count: <= MaximumPolicies } && snapshot.Networks.All(policy => policy is not null &&
            ValidNetwork(policy.Key) && Text(policy.KeyEpoch) && Codes(policy.GuestAccess)) &&
        snapshot.NodeGrants is { Count: <= MaximumPolicies } && snapshot.NodeGrants.All(grant => grant is not null &&
            ValidNode(grant.Node) && Enum.IsDefined(grant.Level) &&
            (grant.ScopeNetwork is null ? grant.ScopeKeyEpoch is null && grant.ScopeNodeEpoch is null :
                ValidNetwork(grant.ScopeNetwork) && Text(grant.ScopeKeyEpoch) && grant.ScopeNodeEpoch.HasValue)) &&
        snapshot.NetworkGrants is { Count: <= MaximumPolicies } && snapshot.NetworkGrants.All(grant => grant is not null &&
            ValidNetwork(grant.Network) && Enum.IsDefined(grant.Level) && Optional(grant.ScopeKeyEpoch)) &&
        snapshot.NetworkKeys is { Count: <= MaximumCredentials } && snapshot.NetworkKeys.All(key => key is not null && Text(key.NetworkId) && Text(key.KeyEpoch));

    /// <summary>Проверяет один policy без построения authority индексов.</summary>
    private static bool ValidNodePolicy(NodeAccessPolicy? node) => node is not null && ValidNode(node.Node) &&
        (node.Network is null || ValidNetwork(node.Network)) && OptionalCodes(node.RequiredAccess) &&
        OptionalCodes(node.RequireAll) && OptionalCodes(node.RequireAny) &&
        (node.NativeOwners is null || node.NativeOwners.Count <= MaximumOwners && node.NativeOwners.All(Text)) &&
        (node.Fortress is null || Text(node.Fortress.FactionId) && Optional(node.Fortress.FactionFamily) &&
            Optional(node.Fortress.OwnerFactionId) && Optional(node.Fortress.OwnerSessionId) &&
            Optional(node.Fortress.OwnerPrincipalId) && Optional(node.Fortress.OwnerFactionFamily));
    /// <summary>Положительный exact node generation.</summary>
    private static bool ValidNode(NodeKey? node) => node is { Generation: > 0 } && Text(node.NodeId);
    /// <summary>Положительное exact поколение домена authority.</summary>
    private static bool ValidNetwork(NetworkKey? network) => network is { Generation: > 0 } && Text(network.Id);
    /// <summary>Общая bounded native opaque identity, без control characters.</summary>
    private static bool Text(string? text) => NodeFactRules.IsValidText(text, 128);
    /// <summary>Опциональная identity не допускает пустое значение вместо null.</summary>
    private static bool Optional(string? text) => text is null || Text(text);
    /// <summary>Числовые ID codes не могут содержать NaN/дробные значения либо сетевые уровни в строках.</summary>
    private static bool Codes(ValueList<int>? codes) => codes is { Count: <= MaximumAccessCodes } && codes.All(code => code > 0);
    /// <summary>Null requirements — отдельное native значение, не пустой список.</summary>
    private static bool OptionalCodes(ValueList<int>? codes) => codes is null || Codes(codes);

    /// <summary>SHA-256 каждого raw поля, включая source scope epochs и nullable requirements; не удерживает snapshot в replay ledger.</summary>
    public static byte[]? Fingerprint(EntityAccessSnapshot? snapshot)
    {
        if (!IsValidSnapshot(snapshot)) return null;
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-ENTITY-ACCESS-1");
            var principal = snapshot!.Principal;
            writer.Write(principal.SessionId); writer.Write(principal.PrincipalId); WriteCodes(writer, principal.AccessCodes);
            WriteText(writer, principal.NativeOwnerId); WriteText(writer, principal.FactionId); WriteText(writer, principal.FactionFamily);
            writer.Write(principal.IsEntity); writer.Write(principal.Sanctioned); writer.Write(principal.Locked);
            WriteText(writer, principal.MasterSessionId); WriteText(writer, principal.MasterPrincipalId); WriteText(writer, principal.PersonalFactionId);
            writer.Write(snapshot.Nodes.Count);
            foreach (var node in snapshot.Nodes)
            {
                WriteNode(writer, node.Node); WriteNetwork(writer, node.Network); writer.Write(node.KeyEpoch);
                WriteCodes(writer, node.RequiredAccess); writer.Write(node.IsObject); WriteCodes(writer, node.RequireAll); WriteCodes(writer, node.RequireAny);
                writer.Write(node.NativeOwners is not null);
                if (node.NativeOwners is { } owners) { writer.Write(owners.Count); foreach (var owner in owners) writer.Write(owner); }
                writer.Write(node.Fortress is not null);
                if (node.Fortress is { } fortress)
                {
                    writer.Write(fortress.HasActiveGate); writer.Write(fortress.FactionId); WriteText(writer, fortress.FactionFamily);
                    WriteText(writer, fortress.OwnerFactionId); WriteText(writer, fortress.OwnerSessionId);
                    WriteText(writer, fortress.OwnerPrincipalId); WriteText(writer, fortress.OwnerFactionFamily);
                }
            }
            writer.Write(snapshot.Networks.Count);
            foreach (var network in snapshot.Networks) { WriteNetwork(writer, network.Key); writer.Write(network.KeyEpoch); WriteCodes(writer, network.GuestAccess); }
            writer.Write(snapshot.NodeGrants.Count);
            foreach (var grant in snapshot.NodeGrants)
            {
                WriteNode(writer, grant.Node); writer.Write((int)grant.Level); WriteNetwork(writer, grant.ScopeNetwork);
                WriteText(writer, grant.ScopeKeyEpoch); writer.Write(grant.ScopeNodeEpoch.HasValue);
                if (grant.ScopeNodeEpoch is { } epoch) writer.Write(epoch);
            }
            writer.Write(snapshot.NetworkGrants.Count);
            foreach (var grant in snapshot.NetworkGrants) { WriteNetwork(writer, grant.Network); writer.Write((int)grant.Level); WriteText(writer, grant.ScopeKeyEpoch); }
            writer.Write(snapshot.NetworkKeys.Count);
            foreach (var key in snapshot.NetworkKeys) { writer.Write(key.NetworkId); writer.Write(key.KeyEpoch); }
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    /// <summary>Nullable presence сохраняется даже для пустых списков.</summary>
    private static void WriteCodes(BinaryWriter writer, ValueList<int>? codes)
    {
        writer.Write(codes is not null);
        if (codes is null) return;
        writer.Write(codes.Count);
        foreach (var code in codes) writer.Write(code);
    }
    /// <summary>Nullable строка кодируется с отдельным признаком присутствия.</summary>
    private static void WriteText(BinaryWriter writer, string? value) { writer.Write(value is not null); if (value is not null) writer.Write(value); }
    /// <summary>Обе части native generation имеют отдельную типизированную запись.</summary>
    private static void WriteNode(BinaryWriter writer, NodeKey node) { writer.Write(node.NodeId); writer.Write(node.Generation); }
    /// <summary>Nullable домен сохраняет id и generation без коллизий конкатенации.</summary>
    private static void WriteNetwork(BinaryWriter writer, NetworkKey? network)
    { writer.Write(network is not null); if (network is not null) { writer.Write(network.Id); writer.Write(network.Generation); } }
}
