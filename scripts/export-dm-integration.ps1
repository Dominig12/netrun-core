<#
.SYNOPSIS
Собирает проверяемый DM integration bundle из явно выбранного checkout станции.
#>
[CmdletBinding()]
param(
    [string] $SourceRoot = (Join-Path $PSScriptRoot '../../MOLOT-BlueMoon-Station'),
    [string] $BaseRevision,
    [string] $SourceRevision
)
$ErrorActionPreference = 'Stop'
$stationRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$bundleRoot = Join-Path $PSScriptRoot '../integration/dm'
$manifestPath = Join-Path $bundleRoot 'manifest.json'
$stationGitRoot = (& git -C $stationRoot rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($stationGitRoot) -ne [IO.Path]::GetFullPath($stationRoot)) {
    throw 'SourceRoot must be the root of the station checkout.'
}
# Повторный export после commit сохраняет полную интеграцию относительно прежнего baseline.
if (!$BaseRevision -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    $BaseRevision = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).base_revision
}
if (!$BaseRevision) { $BaseRevision = (& git -C $stationRoot rev-parse HEAD).Trim() }
if ($BaseRevision -notmatch '^[0-9a-fA-F]{40}$') { throw 'BaseRevision must be a full commit SHA.' }
$resolvedBaseRevision = (& git -C $stationRoot rev-parse --verify "$BaseRevision^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the station base commit.' }
$resolvedSourceRevision = $null
if ($SourceRevision) {
    if ($SourceRevision -notmatch '^[0-9a-fA-F]{40}$') { throw 'SourceRevision must be a full commit SHA.' }
    $resolvedSourceRevision = (& git -C $stationRoot rev-parse --verify "$SourceRevision^{commit}").Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the station source commit.' }
}
# Только новые adapter/test файлы; проектный документ пользователя не входит в bundle.
$newFiles = @(
    'code/modules/unit_tests/netrun_core_live.dm',
    'code/modules/unit_tests/netrun_core_recovery.dm',
    'code/modules/unit_tests/netrun_external_core.dm',
    'code/modules/unit_tests/netrun_external_projection.dm',
    'code/modules/unit_tests/netrun_external_queries.dm',
    'code/modules/unit_tests/netrun_fixture_bus.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_core.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_effects.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_projection.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_queries.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_service_discovery.dm',
    'tgui/packages/tgui/interfaces/NetrunTerminal/LiveCoreMap.test.tsx',
    'tools/netrun/EXTERNAL_CORE.md',
    'tools/netrun/build-core-container.sh',
    'tools/netrun/compose.yml',
    'tools/netrun/core-lifecycle.mjs',
    'tools/netrun/core-lifecycle.test.mjs',
    'tools/netrun/core-source.json',
    'tools/netrun/core-source.mjs',
    'tools/netrun/core-source.test.mjs',
    'tools/netrun/docker-lifecycle.mjs',
    'tools/netrun/docker-lifecycle.test.mjs',
    'tools/netrun/docker-station-entrypoint.sh',
    'tools/netrun/docker-tools.mjs',
    'tools/netrun/docker-tools.test.mjs',
    'tools/netrun/download-byond.mjs',
    'tools/netrun/download-byond.test.mjs',
    'tools/netrun/probe-host-lifecycle.mjs',
    'tools/netrun/run-live-core-test.mjs',
    'tools/netrun/run-live-core-test.test.mjs'
)
# Tracked hooks экспортируются обычным diff; существующий игровой код не копируется целиком.
$hookFiles = @(
    '.dockerignore',
    '.github/workflows/ci_suite.yml',
    'Dockerfile',
    'code/__DEFINES/netrun.dm',
    'code/controllers/master.dm',
    'code/game/machinery/telecomms/machine_interactions.dm',
    'code/game/machinery/telecomms/telecomunications.dm',
    'code/game/world.dm',
    'code/modules/modular_computers/file_system/programs/ntmonitor.dm',
    'code/modules/unit_tests/_unit_tests.dm',
    'code/modules/unit_tests/netrun.dm',
    'code/modules/unit_tests/netrun_availability_queue.dm',
    'code/modules/unit_tests/netrun_backbone.dm',
    'code/modules/unit_tests/netrun_carriers.dm',
    'code/modules/unit_tests/netrun_cli_release.dm',
    'code/modules/unit_tests/netrun_client_interactions.dm',
    'code/modules/unit_tests/netrun_combat.dm',
    'code/modules/unit_tests/netrun_credentials.dm',
    'code/modules/unit_tests/netrun_cybernetics.dm',
    'code/modules/unit_tests/netrun_delivery_queue.dm',
    'code/modules/unit_tests/netrun_execution.dm',
    'code/modules/unit_tests/netrun_fortress.dm',
    'code/modules/unit_tests/netrun_geographic.dm',
    'code/modules/unit_tests/netrun_ice_fairness.dm',
    'code/modules/unit_tests/netrun_interaction.dm',
    'code/modules/unit_tests/netrun_map_updates.dm',
    'code/modules/unit_tests/netrun_map_workspace.dm',
    'code/modules/unit_tests/netrun_modsuits.dm',
    'code/modules/unit_tests/netrun_navigation.dm',
    'code/modules/unit_tests/netrun_npc_breach.dm',
    'code/modules/unit_tests/netrun_packets.dm',
    'code/modules/unit_tests/netrun_provider_status.dm',
    'code/modules/unit_tests/netrun_providers.dm',
    'code/modules/unit_tests/netrun_refresh.dm',
    'code/modules/unit_tests/netrun_research_authority.dm',
    'code/modules/unit_tests/netrun_routes.dm',
    'code/modules/unit_tests/netrun_routing_cache.dm',
    'code/modules/unit_tests/netrun_scheduler.dm',
    'code/modules/unit_tests/netrun_security_deliveries.dm',
    'code/modules/unit_tests/netrun_topology.dm',
    'code/modules/unit_tests/netrun_transport.dm',
    'dependencies.sh',
    'modular_bluemoon/code/modules/netrun/DEVELOPMENT.md',
    'modular_bluemoon/code/modules/netrun/README.md',
    'modular_bluemoon/code/modules/netrun/core/apc_bridges.dm',
    'modular_bluemoon/code/modules/netrun/core/apc_prebuild.dm',
    'modular_bluemoon/code/modules/netrun/core/apc_spatial_index.dm',
    'modular_bluemoon/code/modules/netrun/core/api.dm',
    'modular_bluemoon/code/modules/netrun/core/breach.dm',
    'modular_bluemoon/code/modules/netrun/core/console.dm',
    'modular_bluemoon/code/modules/netrun/core/console_ui.dm',
    'modular_bluemoon/code/modules/netrun/core/delivery_tasks.dm',
    'modular_bluemoon/code/modules/netrun/core/execution_limits.dm',
    'modular_bluemoon/code/modules/netrun/core/location.dm',
    'modular_bluemoon/code/modules/netrun/core/machine_routes.dm',
    'modular_bluemoon/code/modules/netrun/core/machine_transport.dm',
    'modular_bluemoon/code/modules/netrun/core/map_view.dm',
    'modular_bluemoon/code/modules/netrun/core/network.dm',
    'modular_bluemoon/code/modules/netrun/core/node.dm',
    'modular_bluemoon/code/modules/netrun/core/requests.dm',
    'modular_bluemoon/code/modules/netrun/core/routes.dm',
    'modular_bluemoon/code/modules/netrun/core/service_discovery.dm',
    'modular_bluemoon/code/modules/netrun/core/service_registry.dm',
    'modular_bluemoon/code/modules/netrun/core/subsystem.dm',
    'modular_bluemoon/code/modules/netrun/core/task_scheduler.dm',
    'modular_bluemoon/code/modules/netrun/core/topology.dm',
    'modular_bluemoon/code/modules/netrun/entity/bots.dm',
    'modular_bluemoon/code/modules/netrun/entity/dive_link.dm',
    'modular_bluemoon/code/modules/netrun/entity/interactions.dm',
    'modular_bluemoon/code/modules/netrun/integration/implant_access.dm',
    'modular_bluemoon/code/modules/netrun/integration/machine_atmos.dm',
    'modular_bluemoon/code/modules/netrun/integration/machine_research.dm',
    'modular_bluemoon/code/modules/netrun/integration/machine_telecomms.dm',
    'modular_bluemoon/code/modules/netrun/items/devices.dm',
    'modular_bluemoon/code/modules/netrun/machines/windows.dm',
    'tgstation.dme',
    'tgui/packages/tgui/interfaces/NetrunTerminal/Content.tsx',
    'tgui/packages/tgui/interfaces/NetrunTerminal/ImplantWorkspace.test.tsx',
    'tools/build/build.js',
    'tools/deploy.sh',
    'tools/netrun/run-tests.mjs',
    'tools/netrun/run-tests.test.mjs'
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
    if ($resolvedSourceRevision) {
        # Hook snapshot можно сохранить до последующих upstream merges; adapters
        # должны побайтово совпадать с выбранным commit, без смешивания ревизий.
        $expectedBlob = (& git -C $stationRoot rev-parse "${resolvedSourceRevision}:$relative").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Source revision is missing adapter: $relative" }
        $actualBlob = (& git -C $stationRoot hash-object --no-filters -- $destination).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualBlob -ne $expectedBlob) { throw "Adapter differs from SourceRevision: $relative" }
    }
    $entries += [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$patchPath = Join-Path $bundleRoot 'station-hooks.patch'
$patchArguments = @('-C', $stationRoot, 'diff', '--binary', "--output=$patchPath", $resolvedBaseRevision)
if ($resolvedSourceRevision) { $patchArguments += $resolvedSourceRevision }
$patchArguments += '--'
$patchArguments += $hookFiles
& git @patchArguments
if ($LASTEXITCODE -ne 0) { throw 'Cannot export the station integration diff.' }
$manifest = [ordered]@{
    format = 1
    base_revision = $resolvedBaseRevision
    files = $entries
    patch_sha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($resolvedSourceRevision) { $manifest['source_revision'] = $resolvedSourceRevision }
$manifestJson = ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8)
Write-Output "Exported $($entries.Count) adapter files and the tracked hook diff. Base: $resolvedBaseRevision"
