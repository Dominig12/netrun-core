using System.Reflection;
using System.Runtime.Loader;

namespace Netrun.Core.Plugins;

/// <summary>
/// Загружает только явно перечисленные сборки доверенных локальных плагинов. Каталоги не сканируются,
/// Configure здесь не вызывается; готовый набор передаётся в RuntimeOptions при composition.
/// </summary>
public static class PluginLoader
{
    /// <summary>Загружает сборки по порядку путей и реализации по полному имени типа внутри каждой сборки.</summary>
    /// <param name="assemblyPaths">Явный список путей DLL; относительные пути разрешаются от текущего каталога.</param>
    /// <returns>Владелец экземпляров и изолированных контекстов; освобождается после всех использующих их runtime.</returns>
    /// <exception cref="PluginException">Безопасный стабильный код ошибки загрузки без путей и исключений плагина.</exception>
    public static LoadedPluginSet Load(IReadOnlyList<string> assemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        var contexts = new List<AssemblyLoadContext>();
        var plugins = new List<INetrunPlugin>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var path in assemblyPaths)
            {
                var fullPath = NormalizePath(path);
                if (!paths.Add(fullPath))
                    throw new PluginException("plugin_duplicate_path");
                if (!File.Exists(fullPath))
                    throw new PluginException("plugin_file_missing");
                PluginLoadContext context;
                Assembly assembly;
                try
                {
                    context = new(fullPath);
                    contexts.Add(context);
                    assembly = context.LoadFromAssemblyPath(fullPath);
                }
                catch (Exception)
                {
                    throw new PluginException("plugin_assembly_load_failed");
                }
                Type[] implementations;
                try
                {
                    implementations = assembly.GetExportedTypes()
                        .Where(type => typeof(INetrunPlugin).IsAssignableFrom(type))
                        .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
                }
                catch (Exception)
                {
                    throw new PluginException("plugin_type_load_failed");
                }
                if (implementations.Length == 0)
                    throw new PluginException("plugin_type_missing");
                foreach (var type in implementations)
                {
                    if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                        throw new PluginException("plugin_type_invalid");
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                        throw new PluginException("plugin_constructor_missing");
                    INetrunPlugin plugin;
                    try
                    {
                        plugin = (INetrunPlugin)Activator.CreateInstance(type)!;
                    }
                    catch (Exception)
                    {
                        throw new PluginException("plugin_constructor_failed");
                    }
                    // До чтения пользовательских свойств экземпляр уже принадлежит rollback-владельцу.
                    plugins.Add(plugin);
                    PluginMetadata metadata;
                    int apiVersion;
                    try
                    {
                        metadata = plugin.Metadata;
                        apiVersion = plugin.ApiVersion;
                    }
                    catch (Exception)
                    {
                        throw new PluginException("plugin_metadata_failed");
                    }
                    if (apiVersion != NetrunPluginApi.Version)
                        throw new PluginException("plugin_api_version_mismatch");
                    if (metadata is null || !ValidId(metadata.Id) || !ValidText(metadata.DisplayName, 128) || !ValidText(metadata.Version, 32))
                        throw new PluginException("plugin_metadata_invalid");
                    if (!ids.Add(metadata.Id))
                        throw new PluginException("plugin_duplicate_id");
                }
            }
            return new(plugins, contexts);
        }
        catch
        {
            LoadedPluginSet.Release(plugins, contexts);
            throw;
        }
    }

    /// <summary>Нормализует путь и скрывает системные исключения, содержащие входной путь.</summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new PluginException("plugin_path_invalid");
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new PluginException("plugin_path_invalid"); }
    }

    /// <summary>Идентификатор совпадает с ограничениями реестра composition.</summary>
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    /// <summary>Метаданные ограничены по длине и не содержат управляющих символов.</summary>
    private static bool ValidText(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);

    /// <summary>У каждой DLL собственные зависимости; контракт Core и системные сборки общие с Host.</summary>
    private sealed class PluginLoadContext(string path) : AssemblyLoadContext("Netrun.Plugin", isCollectible: true)
    {
        /// <summary>Контракт всегда берётся из контекста приложения, чтобы не создавать вторую идентичность типов.</summary>
        private static readonly Assembly SharedCore = typeof(INetrunPlugin).Assembly;
        /// <summary>Системные библиотеки определяются по каталогу активного runtime, без поиска плагинов на диске.</summary>
        private static readonly HashSet<string> FrameworkAssemblies = FrameworkAssemblyNames();
        /// <summary>Зависимости разрешаются относительно конкретной сборки и её deps.json.</summary>
        private readonly AssemblyDependencyResolver _resolver = new(path);

        /// <summary>Контракт Core проверяется до загрузки зависимых типов, остальные DLL принадлежат плагину.</summary>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == SharedCore.GetName().Name)
            {
                var sharedVersion = SharedCore.GetName().Version!;
                if (assemblyName.Version is { } required && (required.Major != sharedVersion.Major || required > sharedVersion))
                    throw new PluginException("plugin_core_version_mismatch");
                return SharedCore;
            }
            if (assemblyName.Name is { } name && FrameworkAssemblies.Contains(name))
                return Default.LoadFromAssemblyName(assemblyName);
            var dependency = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependency is not null
                ? LoadFromAssemblyPath(dependency)
                : throw new FileNotFoundException("plugin_dependency_missing");
        }

        /// <summary>Нативные зависимости также разрешаются только относительно явно подключённого плагина.</summary>
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var dependency = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return dependency is not null
                ? LoadUnmanagedDllFromPath(dependency)
                : throw new DllNotFoundException("plugin_dependency_missing");
        }

        /// <summary>Использует список платформы, оставляя только библиотеки каталога активного runtime.</summary>
        private static HashSet<string> FrameworkAssemblyNames()
        {
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(file => comparer.Equals(Path.GetDirectoryName(file), runtimeDirectory))
                .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// Владеет загруженными экземплярами плагинов и collectible-контекстами. Runtime не владеет этим набором:
/// вызывающий код освобождает его после остановки всех сессий, затем удаляет оставшиеся ссылки на типы плагинов.
/// </summary>
public sealed class LoadedPluginSet : IDisposable
{
    /// <summary>Read-only список не позволяет изменить состав после проверки загрузчиком.</summary>
    private IReadOnlyList<INetrunPlugin> _plugins;
    /// <summary>Все созданные контексты, включая сборки с несколькими реализациями плагинов.</summary>
    private readonly List<AssemblyLoadContext> _contexts;
    /// <summary>Освобождение выполняется единожды, даже при повторном Dispose.</summary>
    private int _disposed;

    /// <summary>Только загрузчик передаёт полностью проверенный состав.</summary>
    internal LoadedPluginSet(List<INetrunPlugin> plugins, List<AssemblyLoadContext> contexts)
    {
        _plugins = Array.AsReadOnly(plugins.ToArray());
        _contexts = contexts;
    }

    /// <summary>Проверенные экземпляры для RuntimeOptions; после Dispose список пуст.</summary>
    public IReadOnlyList<INetrunPlugin> Plugins => _plugins;

    /// <summary>Освобождает IDisposable-плагины в обратном порядке и инициирует выгрузку всех контекстов.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var plugins = _plugins;
        _plugins = Array.Empty<INetrunPlugin>();
        var failed = Release(plugins, _contexts);
        _contexts.Clear();
        if (failed)
            throw new PluginException("plugin_dispose_failed");
    }

    /// <summary>Rollback всегда очищает весь состав; исключения расширений не прерывают освобождение других DLL.</summary>
    internal static bool Release(IReadOnlyList<INetrunPlugin> plugins, IReadOnlyList<AssemblyLoadContext> contexts)
    {
        var failed = false;
        for (var index = plugins.Count - 1; index >= 0; index--)
        {
            try { (plugins[index] as IDisposable)?.Dispose(); }
            catch (Exception) { failed = true; }
        }
        for (var index = contexts.Count - 1; index >= 0; index--)
        {
            try { contexts[index].Unload(); }
            catch (Exception) { failed = true; }
        }
        return failed;
    }
}
