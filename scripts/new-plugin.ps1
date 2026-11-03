param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Z][A-Za-z0-9]{0,63}$')]
    [string] $Name,
    [string] $OutputDirectory,
    [ValidatePattern('^[a-z0-9._-]{1,64}$')]
    [string] $PluginId,
    [string] $CoreProject = (Join-Path $PSScriptRoot '../src/Netrun.Core/Netrun.Core.csproj')
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot "../plugins/$Name"
}
if (-not $PluginId) {
    $PluginId = $Name.ToLowerInvariant()
}
$pluginTarget = [IO.Path]::GetFullPath($OutputDirectory)
$coreProjectPath = [IO.Path]::GetFullPath($CoreProject)
if (Test-Path -LiteralPath $pluginTarget) {
    throw 'plugin_output_exists'
}
if (-not (Test-Path -LiteralPath $coreProjectPath -PathType Leaf)) {
    throw 'plugin_core_project_missing'
}
$reference = [Security.SecurityElement]::Escape([IO.Path]::GetRelativePath($pluginTarget, $coreProjectPath).Replace('\', '/'))
# Шаблон не добавляет зависимости и не перезаписывает существующие файлы.
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$reference" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
"@
$source = @"
using Netrun.Core.Plugins;

namespace Netrun.Plugins.$Name;

/// <summary>Доверенное расширение NETRUN; регистрирует чистые стратегии до обработки входа.</summary>
public sealed class ${Name}Plugin : INetrunPlugin
{
    /// <inheritdoc />
    public PluginMetadata Metadata { get; } = new("$PluginId", "$Name", "1.0.0");

    /// <inheritdoc />
    public void Configure(INetrunPluginRegistry registry)
    {
        // Здесь доступны SetRouteCostPolicy, AddNodeChangeHandler и RegisterModule.
        // Стратегии должны быть детерминированными и не обращаться к DM или вводу-выводу.
    }
}
"@
$readme = @"
# $Name

Собрать: ``dotnet build $Name.csproj -c Release``.

Передайте полный путь к ``bin/Release/net10.0/$Name.dll`` в явный список PluginLoader.Load.
У Host задайте ``NETRUN_PLUGIN_PATHS`` (список путей с разделителем ``[IO.Path]::PathSeparator``).
Сохраните весь каталог сборки с зависимостями; Netrun.Core предоставляется Host.

Configure выполняется один раз на каждую runtime composition. Плагин должен быть без изменяемого
разделяемого состояния: один загруженный экземпляр может обслуживать несколько runtime-сессий.
После изменения API пересоберите плагин с соответствующей версией Netrun.Core.
"@
[IO.Directory]::CreateDirectory($pluginTarget) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $pluginTarget "$Name.csproj"), $project.Replace("`r`n", "`n") + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $pluginTarget "${Name}Plugin.cs"), $source.Replace("`r`n", "`n") + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $pluginTarget 'README.md'), $readme.Replace("`r`n", "`n") + "`n", $utf8)
Write-Output "Created plugin project: $pluginTarget"
