using System.Diagnostics;
using System.Runtime.Loader;
using System.Security;
using System.Text.Json;
using Netrun.Core;
using Netrun.Core.Plugins;
using Netrun.Core.Runtime;
using Xunit;

namespace Netrun.Tests;

/// <summary>Проверяет настоящий loader на отдельных DLL, собранных установленным SDK без сторонних зависимостей.</summary>
public sealed class PluginLoaderTests(PluginAssemblyFixture fixture) : IClassFixture<PluginAssemblyFixture>
{
    /// <summary>Без явного списка нет файлового поиска; ошибки входа не раскрывают путь.</summary>
    [Fact]
    public void EmptySetAndInvalidPathsArePredictable()
    {
        using var empty = PluginLoader.Load([]);
        Assert.Empty(empty.Plugins);
        Assert.Equal("plugin_path_invalid", Assert.Throws<PluginException>(() => PluginLoader.Load([" "])).Code);
        var missing = Path.Combine(fixture.Root, "missing-private-path.dll");
        var error = Assert.Throws<PluginException>(() => PluginLoader.Load([missing]));
        Assert.Equal("plugin_file_missing", error.Code);
        Assert.DoesNotContain(missing, error.ToString());
        var badImage = Path.Combine(fixture.Root, "invalid.dll");
        File.WriteAllText(badImage, "not an assembly");
        Assert.Equal("plugin_assembly_load_failed", Assert.Throws<PluginException>(() => PluginLoader.Load([badImage])).Code);
    }

    /// <summary>Core разделяет идентичность типов с Host; собственные зависимости одинакового имени изолированы.</summary>
    [Fact]
    public void ExplicitAssembliesUseSharedCoreAndIsolatedDependencies()
    {
        var paths = fixture.IsolatedPlugins();
        using var loaded = PluginLoader.Load(paths);
        Assert.Equal(new[] { "fixture.first", "fixture.second" }, loaded.Plugins.Select(plugin => plugin.Metadata.Id));
        var firstContext = AssemblyLoadContext.GetLoadContext(loaded.Plugins[0].GetType().Assembly);
        var secondContext = AssemblyLoadContext.GetLoadContext(loaded.Plugins[1].GetType().Assembly);
        Assert.NotNull(firstContext);
        Assert.True(firstContext.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, firstContext);
        Assert.NotSame(firstContext, secondContext);
        // Настоящий runtime принимает загруженный INetrunPlugin без второй копии контракта.
        using var runtime = new NetworkRuntime(new Epochs("world", "engine", "binding"), new RuntimeOptions(Plugins: loaded.Plugins));
        Assert.Equal(2, runtime.Plugins.Count);
        var list = Assert.IsAssignableFrom<IList<INetrunPlugin>>(loaded.Plugins);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    /// <summary>Повтор пути нормализуется до загрузки второй копии; повтор Id проверяется между сборками.</summary>
    [Fact]
    public void DuplicatePathsAndIdsAreRejected()
    {
        var plugin = fixture.BuildPlugin("Duplicate", "public PluginMetadata Metadata => new(\"fixture.same\", \"Same\", \"1\");");
        var alias = Path.Combine(Path.GetDirectoryName(plugin)!, ".", Path.GetFileName(plugin));
        Assert.Equal("plugin_duplicate_path", Assert.Throws<PluginException>(() => PluginLoader.Load([plugin, alias])).Code);
        var second = fixture.BuildPlugin("DuplicateOther", "public PluginMetadata Metadata => new(\"fixture.same\", \"Same\", \"1\");");
        Assert.Equal("plugin_duplicate_id", Assert.Throws<PluginException>(() => PluginLoader.Load([plugin, second])).Code);
    }

    /// <summary>Несовместимый API и недопустимые типы/метаданные отвергаются до Configure.</summary>
    [Theory]
    [InlineData("Version", "public int ApiVersion => 999;", "public sealed class Plugin : INetrunPlugin", "plugin_api_version_mismatch")]
    [InlineData("Abstract", "", "public abstract class Plugin : INetrunPlugin", "plugin_type_invalid")]
    [InlineData("Generic", "", "public sealed class Plugin<T> : INetrunPlugin", "plugin_type_invalid")]
    [InlineData("Constructor", "public Plugin(int value) { }", "public sealed class Plugin : INetrunPlugin", "plugin_constructor_missing")]
    [InlineData("Throwing", "public Plugin() => throw new Exception(\"private-constructor-detail\");", "public sealed class Plugin : INetrunPlugin", "plugin_constructor_failed")]
    [InlineData("Metadata", "public PluginMetadata Metadata => throw new Exception(\"private-metadata-detail\");", "public sealed class Plugin : INetrunPlugin", "plugin_metadata_failed")]
    [InlineData("BadId", "public PluginMetadata Metadata => new(\"Invalid Id\", \"Name\", \"1\");", "public sealed class Plugin : INetrunPlugin", "plugin_metadata_invalid")]
    public void InvalidPluginsFailWithSafeCodes(string name, string members, string declaration, string expected)
    {
        var path = fixture.BuildPlugin(name, members, declaration);
        var error = Assert.Throws<PluginException>(() => PluginLoader.Load([path]));
        Assert.Equal(expected, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-", error.ToString());
    }

    /// <summary>Сборка без реализации и отсутствующая dependency не превращаются в случайную загрузку из Host.</summary>
    [Fact]
    public void MissingImplementationAndDependencyFailClosed()
    {
        var plain = fixture.Build("Plain", "public sealed class Plain { }");
        Assert.Equal("plugin_type_missing", Assert.Throws<PluginException>(() => PluginLoader.Load([plain])).Code);
        var dependency = fixture.Build("AbsentDependency", "public class Base { }");
        var plugin = fixture.BuildPlugin("NeedsDependency", "", "public sealed class Plugin : Base, INetrunPlugin", dependency);
        File.Delete(Path.Combine(Path.GetDirectoryName(plugin)!, "AbsentDependency.dll"));
        var error = Assert.Throws<PluginException>(() => PluginLoader.Load([plugin]));
        Assert.Equal("plugin_type_load_failed", error.Code);
    }

    /// <summary>Ошибка последующей сборки освобождает уже созданные экземпляры; исходная ошибка сохраняется.</summary>
    [Fact]
    public void FailedLoadRollsBackOwnedPlugins()
    {
        var marker = Path.Combine(fixture.Root, "rollback.txt");
        var plugin = fixture.BuildPlugin("Rollback", $"public void Dispose() => File.AppendAllText({JsonSerializer.Serialize(marker)}, \"disposed\\n\");",
            "public sealed class Plugin : INetrunPlugin, IDisposable");
        var error = Assert.Throws<PluginException>(() => PluginLoader.Load([plugin, Path.Combine(fixture.Root, "absent.dll")]));
        Assert.Equal("plugin_file_missing", error.Code);
        Assert.Equal("disposed\n", File.ReadAllText(marker));
    }

    /// <summary>Ошибка Dispose не препятствует очистке остальных плагинов; повторное освобождение безопасно.</summary>
    [Fact]
    public void DisposeReleasesAllPluginsOnceAndReportsSafeFailure()
    {
        var marker = Path.Combine(fixture.Root, "dispose.txt");
        var plugin = fixture.BuildPlugin("DisposeFailure",
            $"public void Dispose() {{ File.AppendAllText({JsonSerializer.Serialize(marker)}, \"disposed\\n\"); throw new Exception(\"private-dispose-detail\"); }}",
            "public sealed class Plugin : INetrunPlugin, IDisposable");
        var loaded = PluginLoader.Load([plugin]);
        var error = Assert.Throws<PluginException>(loaded.Dispose);
        Assert.Equal("plugin_dispose_failed", error.Code);
        Assert.Null(error.InnerException);
        Assert.Empty(loaded.Plugins);
        loaded.Dispose();
        Assert.Equal("disposed\n", File.ReadAllText(marker));
    }
}

/// <summary>Изолированная временная сборка SDK-only фикстур; NuGet-источники отключены.</summary>
public sealed class PluginAssemblyFixture : IDisposable
{
    /// <summary>Каждый тестовый класс получает свой каталог без влияния на checkout и bin основной сборки.</summary>
    public string Root { get; } = Directory.CreateTempSubdirectory("netrun-plugin-tests-").FullName;
    /// <summary>Собранные сценарии повторно используются только внутри этого класса.</summary>
    private readonly Dictionary<string, string> _built = new(StringComparer.Ordinal);

    /// <summary>Собирает плагин с явными членами и стандартными метаданными, если сценарий не задаёт свои.</summary>
    public string BuildPlugin(string name, string members, string declaration = "public sealed class Plugin : INetrunPlugin", string? dependency = null)
    {
        var metadata = members.Contains("PluginMetadata Metadata", StringComparison.Ordinal) ? "" :
            $"public PluginMetadata Metadata => new(\"fixture.{name.ToLowerInvariant()}\", \"Fixture\", \"1\");";
        return Build(name, $$"""
            using System;
            using System.IO;
            using Netrun.Core.Plugins;
            {{declaration}}
            {
                {{metadata}}
                {{members}}
                public void Configure(INetrunPluginRegistry registry) { }
            }
            """, dependency);
    }

    /// <summary>Одноимённые зависимости с разным содержимым проверяют изоляцию контекстов.</summary>
    public string[] IsolatedPlugins()
    {
        var firstDependency = Build("FirstDependency", "public static class SharedValue { public static string Id => \"fixture.first\"; }", assemblyName: "SharedValue");
        var secondDependency = Build("SecondDependency", "public static class SharedValue { public static string Id => \"fixture.second\"; }", assemblyName: "SharedValue");
        var members = "public PluginMetadata Metadata => new(SharedValue.Id, \"Isolated\", \"1\");";
        return [BuildPlugin("IsolatedFirst", members, dependency: firstDependency), BuildPlugin("IsolatedSecond", members, dependency: secondDependency)];
    }

    /// <summary>Использует только платформенные reference assemblies и уже собранный Core, без пакетов и сети.</summary>
    public string Build(string name, string source, string? dependency = null, string? assemblyName = null)
    {
        if (_built.TryGetValue(name, out var built))
            return built;
        assemblyName ??= name;
        var directory = Path.Combine(Root, name);
        Directory.CreateDirectory(directory);
        var escapedCore = SecurityElement.Escape(typeof(INetrunPlugin).Assembly.Location);
        var reference = dependency is null ? "" : $"<Reference Include=\"{Path.GetFileNameWithoutExtension(dependency)}\"><HintPath>{SecurityElement.Escape(dependency)}</HintPath></Reference>";
        File.WriteAllText(Path.Combine(directory, "Fixture.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{{assemblyName}}</AssemblyName>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <EnableDynamicLoading>true</EnableDynamicLoading>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Netrun.Core"><HintPath>{{escapedCore}}</HintPath><Private>false</Private></Reference>
                {{reference}}
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Plugin.cs"), source);
        File.WriteAllText(Path.Combine(directory, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "build", "Fixture.csproj", "--nologo", "--disable-build-servers", "-v:q" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("SDK plugin fixture build exceeded one minute.");
        }
        Task.WaitAll(output, errors);
        Assert.True(process.ExitCode == 0, $"SDK fixture failed: {output.Result}{errors.Result}");
        built = Path.Combine(directory, "bin", "Debug", "net10.0", assemblyName + ".dll");
        _built.Add(name, built);
        return built;
    }

    /// <summary>Collectible-сборки освобождают handles после GC; временный каталог удаляется целиком.</summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        var target = Path.GetFullPath(Root);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFileName(target).StartsWith("netrun-plugin-tests-", StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(target), temporaryRoot, comparison))
            throw new InvalidOperationException("Refusing to remove a fixture outside its temporary directory.");
        Directory.Delete(target, recursive: true);
    }
}
