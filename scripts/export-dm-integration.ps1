<#
.SYNOPSIS
Собирает проверяемый DM integration bundle из явно выбранного checkout станции.
#>
[CmdletBinding()]
param(
    [string] $SourceRoot = (Join-Path $PSScriptRoot '../../MOLOT-BlueMoon-Station')
)
$ErrorActionPreference = 'Stop'
$stationRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$bundleRoot = Join-Path $PSScriptRoot '../integration/dm'
$stationGitRoot = (& git -C $stationRoot rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($stationGitRoot) -ne [IO.Path]::GetFullPath($stationRoot)) {
    throw 'SourceRoot must be the root of the station checkout.'
}
# Только новые adapter/test файлы; проектный документ пользователя не входит в bundle.
$newFiles = @(
    'modular_bluemoon/code/modules/netrun/integration/external_core.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_effects.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_queries.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_service_discovery.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_projection.dm',
    'code/modules/unit_tests/netrun_external_core.dm',
    'code/modules/unit_tests/netrun_external_queries.dm',
    'code/modules/unit_tests/netrun_external_projection.dm',
    'code/modules/unit_tests/netrun_core_live.dm',
    'code/modules/unit_tests/netrun_core_recovery.dm',
    'tools/netrun/core-lifecycle.mjs',
    'tools/netrun/core-lifecycle.test.mjs',
    'tools/netrun/docker-lifecycle.mjs',
    'tools/netrun/docker-lifecycle.test.mjs',
    'tools/netrun/build-core-container.sh',
    'tools/netrun/docker-station-entrypoint.sh',
    'tools/netrun/compose.yml',
    'tools/netrun/probe-host-lifecycle.mjs',
    'tools/netrun/run-live-core-test.mjs',
    'tools/netrun/run-live-core-test.test.mjs',
    'tools/netrun/EXTERNAL_CORE.md'
)
# Tracked hooks экспортируются обычным diff; существующий игровой код не копируется целиком.
$hookFiles = @(
    'tgstation.dme', 'code/modules/unit_tests/_unit_tests.dm',
    'code/game/world.dm',
    'Dockerfile', '.dockerignore', '.github/workflows/ci_suite.yml',
    'tools/build/build.js', 'tools/netrun/run-tests.mjs', 'tools/netrun/run-tests.test.mjs',
    'modular_bluemoon/code/modules/netrun/core/machine_transport.dm',
    'modular_bluemoon/code/modules/netrun/core/machine_routes.dm',
    'modular_bluemoon/code/modules/netrun/core/service_registry.dm',
    'modular_bluemoon/code/modules/netrun/core/subsystem.dm',
    'modular_bluemoon/code/modules/netrun/core/node.dm',
    'modular_bluemoon/code/modules/netrun/core/topology.dm',
    'modular_bluemoon/code/modules/netrun/core/routes.dm',
    'modular_bluemoon/code/modules/netrun/core/task_scheduler.dm',
    'modular_bluemoon/code/modules/netrun/core/console.dm',
    'modular_bluemoon/code/modules/netrun/core/map_view.dm',
    'modular_bluemoon/code/modules/netrun/integration/machine_telecomms.dm',
    'code/game/machinery/telecomms/machine_interactions.dm',
    'code/game/machinery/telecomms/telecomunications.dm',
    'code/modules/modular_computers/file_system/programs/ntmonitor.dm'
)
foreach ($relative in $newFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $stationRoot $relative) -PathType Leaf)) {
        throw "Required adapter file is missing: $relative"
    }
}
New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
$entries = @()
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
foreach ($relative in $newFiles) {
    $source = Join-Path $stationRoot $relative
    $destination = Join-Path $bundleRoot "files/$relative"
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    # Bundle следует .gitattributes Core: hashes должны сохраниться после Linux/Windows Git checkout.
    $content = [IO.File]::ReadAllText($source, $utf8).Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText($destination, $content, $utf8)
    $entries += [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$patchPath = Join-Path $bundleRoot 'station-hooks.patch'
& git -C $stationRoot diff --binary "--output=$patchPath" HEAD -- @hookFiles
if ($LASTEXITCODE -ne 0) { throw 'Cannot export the station integration diff.' }
$baseRevision = (& git -C $stationRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the station base revision.' }
$manifest = [ordered]@{
    format = 1
    base_revision = $baseRevision
    files = $entries
    patch_sha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifestJson = ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $bundleRoot 'manifest.json'), $manifestJson, $utf8)
Write-Output "Exported $($entries.Count) adapter files and the tracked hook diff. Base: $baseRevision"
