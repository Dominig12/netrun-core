using Netrun.Core.Routing;

namespace Netrun.Core.Plugins;

/// <summary>
/// Собирает вклады плагинов во время composition и предоставляет их ядру только для чтения.
/// Экземпляр изменяется исключительно внутри <see cref="INetrunPlugin.Configure"/> и затем читается
/// runtime; повторный вход и многопоточность не поддерживаются, как и у остального ядра.
/// </summary>
public sealed class NetrunPluginRegistry : INetrunPluginRegistry
{
    /// <summary>Метаданные всех учтённых плагинов в порядке их объявления.</summary>
    private readonly List<PluginMetadata> _plugins = [];
    /// <summary>Уникальные идентификаторы плагинов; дубликат является ошибкой состава.</summary>
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    /// <summary>Единственная политика стоимостей на весь набор; null — объявленные веса графа.</summary>
    private IRouteCostPolicy? _routeCostPolicy;
    /// <summary>Нормализаторы дополнительных typed node-фактов, без расширения wire allowlist.</summary>
    private readonly Dictionary<Type, INodeChangeHandler> _nodeHandlers = [];
    /// <summary>Запись разрешена только внутри текущего Configure, в том числе при исключении.</summary>
    private bool _configuring;
    /// <summary>Factories расширений; mutable session state принадлежит отдельному binding.</summary>
    private readonly PluginModuleRegistry _modules = new();
    /// <summary>Закрытый состав модулей для composition owner.</summary>
    internal PluginModuleRegistry Modules => _modules;

    /// <inheritdoc />
    public void RegisterModule(INetrunModuleFactory factory)
    {
        EnsureConfiguring();
        _modules.Register(factory);
    }

    /// <summary>Метаданные учтённых плагинов для диагностики; порядок соответствует объявлению.</summary>
    internal IReadOnlyList<PluginMetadata> Plugins => _plugins.AsReadOnly();
    /// <summary>Выбранная набором политика стоимостей либо null, если ни один плагин её не задал.</summary>
    internal IRouteCostPolicy? RouteCostPolicy => _routeCostPolicy;

    /// <inheritdoc />
    public void SetRouteCostPolicy(IRouteCostPolicy policy)
    {
        EnsureConfiguring();
        ArgumentNullException.ThrowIfNull(policy);
        if (_routeCostPolicy is not null)
            throw new PluginException("duplicate_route_cost_policy");
        _routeCostPolicy = policy;
    }

    /// <inheritdoc />
    public void AddNodeChangeHandler(INodeChangeHandler handler)
    {
        EnsureConfiguring();
        ArgumentNullException.ThrowIfNull(handler);
        var type = handler.ChangeType;
        if (type is null || type.IsAbstract || !typeof(WorldChange).IsAssignableFrom(type) ||
            type == typeof(NodeUpsert) || type == typeof(NodeRemove) || type == typeof(SectorUpsert) ||
            type == typeof(SectorRemove) || type == typeof(ServiceUpsert) || type == typeof(ServiceRemove) ||
            type == typeof(SnapshotBegin) || type == typeof(SnapshotChunk) || type == typeof(SnapshotCommit) ||
            type == typeof(CatchUpDelta) || type == typeof(CatchUpComplete))
            throw new PluginException("plugin_node_handler_invalid");
        if (!_nodeHandlers.TryAdd(type, handler)) throw new PluginException("plugin_node_handler_duplicate");
    }

    /// <summary>Закрывает реестр вне стека Configure; сохранённая плагином ссылка больше не изменяет состав.</summary>
    private void EnsureConfiguring()
    {
        if (!_configuring) throw new PluginException("plugin_registry_closed");
    }

    /// <summary>Нормализует custom факты до общего fingerprint/snapshot пути, сохраняя атомарность всего кадра.</summary>
    internal IReadOnlyList<WorldChange>? Normalize(IReadOnlyList<WorldChange>? entries, int maximum)
    {
        if (entries is null || entries.Count > maximum) return null;
        if (_nodeHandlers.Count == 0) return entries;
        var result = new WorldChange[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var change = entries[i];
            if (change is null) return null;
            if (_nodeHandlers.TryGetValue(change.GetType(), out var handler))
            {
                var mutation = handler.Normalize(change);
                if (mutation is null || !NodeFactRules.IsValidMutation(mutation)) return null;
                result[i] = mutation.Fact is { } fact ? new NodeUpsert(fact) :
                    new NodeRemove(mutation.NodeId, mutation.Generation, mutation.Revision);
            }
            else if (change is SnapshotChunk chunk)
            {
                // WireDigest относится к точным JSON bytes: custom записи допустимы только в binary snapshot.
                if (chunk.Changes is null || chunk.Changes.Any(item => item is null ||
                    item is SnapshotBegin or SnapshotChunk or SnapshotCommit or CatchUpDelta or CatchUpComplete)) return null;
                var normalized = Normalize(chunk.Changes, maximum);
                if (normalized is null || chunk.WireDigest is not null &&
                    chunk.Changes.Any(item => _nodeHandlers.ContainsKey(item.GetType()))) return null;
                result[i] = chunk with { Changes = normalized };
            }
            else if (change is CatchUpDelta delta)
            {
                if (delta.Changes is null || delta.Changes.Any(item => item is null ||
                    item is SnapshotBegin or SnapshotChunk or SnapshotCommit or CatchUpDelta or CatchUpComplete)) return null;
                var normalized = Normalize(delta.Changes, maximum);
                if (normalized is null) return null;
                result[i] = delta with { Changes = normalized };
            }
            else result[i] = change;
        }
        return result;
    }

    /// <summary>
    /// Проверяет набор, по порядку вызывает <see cref="INetrunPlugin.Configure"/> каждого плагина и
    /// возвращает заполненный реестр. Любая ошибка плагина прекращает composition до создания подсистем ядра.
    /// </summary>
    /// <param name="plugins">Объявленный набор плагинов либо null, если расширений нет.</param>
    internal static NetrunPluginRegistry Configure(IReadOnlyList<INetrunPlugin>? plugins)
    {
        var registry = new NetrunPluginRegistry();
        if (plugins is null)
            return registry;
        foreach (var plugin in plugins)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            if (plugin.ApiVersion != NetrunPluginApi.Version) throw new PluginException("plugin_api_incompatible");
            var metadata = plugin.Metadata;
            ArgumentNullException.ThrowIfNull(metadata);
            if (!IsValidId(metadata.Id) || !IsValidText(metadata.DisplayName, 128) || !IsValidText(metadata.Version, 32))
                throw new PluginException("plugin_metadata_invalid");
            if (!registry._ids.Add(metadata.Id))
                throw new PluginException("plugin_duplicate_id");
            registry._plugins.Add(metadata);
            registry._configuring = true;
            try { plugin.Configure(registry); }
            finally { registry._configuring = false; }
        }
        return registry;
    }

    /// <summary>Идентификатор плагина: строчные латинские буквы, цифры, точка, дефис, подчёркивание; 1..64 символа.</summary>
    private static bool IsValidId(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    /// <summary>Непустой диагностический текст без управляющих символов, не длиннее заданного предела.</summary>
    private static bool IsValidText(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && !value.Any(char.IsControl);
}
