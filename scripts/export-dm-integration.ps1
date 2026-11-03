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
    'code/datums/innate_features.dm',
    'code/modules/surgery/bodyparts/naga_tail.dm',
    'code/modules/unit_tests/bodypart_leg_layers.dm',
    'code/modules/unit_tests/bugreports_2026_11.dm',
    'code/modules/unit_tests/flamethrower_fuel.dm',
    'code/modules/unit_tests/inducer_magazine.dm',
    'code/modules/unit_tests/lewd_summon.dm',
    'code/modules/unit_tests/multiz_station_transparent_space.dm',
    'code/modules/unit_tests/netrun_core_live.dm',
    'code/modules/unit_tests/netrun_core_recovery.dm',
    'code/modules/unit_tests/netrun_external_core.dm',
    'code/modules/unit_tests/netrun_external_projection.dm',
    'code/modules/unit_tests/netrun_external_queries.dm',
    'code/modules/unit_tests/netrun_fixture_bus.dm',
    'code/modules/unit_tests/pull_layer_ordering.dm',
    'code/modules/unit_tests/resize_health_hud.dm',
    'code/modules/unit_tests/supporting_tail.dm',
    'code/modules/unit_tests/vanguard_mission_remains.dm',
    'docs/innate_features.md',
    'html/changelogs/archive/2026-11.yml',
    'modular_bluemoon/code/modules/netrun/ASYNC_EXTERNAL.md',
    'modular_bluemoon/code/modules/netrun/integration/external_core.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_effects.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_projection.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_queries.dm',
    'modular_bluemoon/code/modules/netrun/integration/external_service_discovery.dm',
    'tgui/packages/tgui/interfaces/CharacterSetup/tabs/Innate.test.tsx',
    'tgui/packages/tgui/interfaces/CharacterSetup/tabs/Innate.tsx',
    'tgui/packages/tgui/interfaces/HealthanalyzerInnate.test.tsx',
    'tgui/packages/tgui/interfaces/NetrunTerminal/LiveCoreMap.test.tsx',
    'tools/netrun/EXTERNAL_CORE.md',
    'tools/netrun/build-core-container.sh',
    'tools/netrun/compose.yml',
    'tools/netrun/core-lifecycle.mjs',
    'tools/netrun/core-lifecycle.test.mjs',
    'tools/netrun/core-source.json',
    'tools/netrun/core-source.mjs',
    'tools/netrun/core-source.test.mjs',
    'tools/netrun/debug-host.mjs',
    'tools/netrun/debug-host.test.mjs',
    'tools/netrun/docker-lifecycle.mjs',
    'tools/netrun/docker-lifecycle.test.mjs',
    'tools/netrun/docker-station-entrypoint.sh',
    'tools/netrun/docker-tools.mjs',
    'tools/netrun/docker-tools.test.mjs',
    'tools/netrun/download-byond.mjs',
    'tools/netrun/download-byond.test.mjs',
    'tools/netrun/probe-host-lifecycle.mjs',
    'tools/netrun/run-live-core-test.mjs',
    'tools/netrun/run-live-core-test.test.mjs',
    'tools/netrun/setup-vscode.mjs',
    'tools/netrun/setup-vscode.test.mjs',
    'tools/netrun/vscode-launch.json',
    'tools/netrun/vscode-tasks.json'
)
# Tracked hooks экспортируются обычным diff; существующий игровой код не копируется целиком.
$hookFiles = @(
    '.dockerignore',
    '.github/workflows/ci_suite.yml',
    '.vscode/launch.json',
    '.vscode/tasks.json',
    'Dockerfile',
    '_maps/map_files/BoxStations/SyndicateBoxStation.dmm',
    '_maps/map_files/Mining/Lavaland.dmm',
    'code/__DEFINES/hud.dm',
    'code/__DEFINES/loadout.dm',
    'code/__DEFINES/misc.dm',
    'code/__DEFINES/mobs.dm',
    'code/__DEFINES/netrun.dm',
    'code/__DEFINES/research.dm',
    'code/__HELPERS/do_after.dm',
    'code/__HELPERS/type2type.dm',
    'code/_onclick/hud/human.dm',
    'code/_onclick/hud/screen_objects.dm',
    'code/_onclick/hud/screen_objects/storage.dm',
    'code/_rendering/atom_huds/data_huds.dm',
    'code/controllers/master.dm',
    'code/controllers/subsystem/research.dm',
    'code/datums/components/embedded.dm',
    'code/datums/components/footstep.dm',
    'code/datums/components/riding.dm',
    'code/datums/diseases/anaphylaxis.dm',
    'code/datums/dna.dm',
    'code/datums/elements/_element.dm',
    'code/datums/elements/turf_transparency.dm',
    'code/datums/martial/cqc.dm',
    'code/datums/martial/krav_maga.dm',
    'code/datums/martial/psychotic_brawl.dm',
    'code/datums/martial/rising_bass.dm',
    'code/datums/martial/sleeping_carp.dm',
    'code/datums/martial/wrestling.dm',
    'code/datums/mind.dm',
    'code/datums/ruins/space.dm',
    'code/datums/status_effects/debuffs.dm',
    'code/datums/status_effects/wound_effects.dm',
    'code/datums/wounds/_wounds.dm',
    'code/datums/wounds/bones.dm',
    'code/datums/wounds/slash.dm',
    'code/game/machinery/limbgrower.dm',
    'code/game/machinery/recharger.dm',
    'code/game/machinery/telecomms/machine_interactions.dm',
    'code/game/machinery/telecomms/telecomunications.dm',
    'code/game/objects/items/devices/scanners.dm',
    'code/game/objects/items/flamethrower.dm',
    'code/game/objects/items/handcuffs.dm',
    'code/game/objects/items/holy_weapons.dm',
    'code/game/objects/items/hot_potato.dm',
    'code/game/objects/items/inducer.dm',
    'code/game/objects/items/stacks/medical.dm',
    'code/game/objects/items/tools/weldingtool.dm',
    'code/game/objects/structures/crates_lockers/closets.dm',
    'code/game/turfs/space/space.dm',
    'code/game/world.dm',
    'code/modules/antagonists/bloodsucker/powers/veil.dm',
    'code/modules/client/preferences.dm',
    'code/modules/client/preferences_savefile.dm',
    'code/modules/client/preferences_tgui_char.dm',
    'code/modules/clothing/clothing.dm',
    'code/modules/clothing/shoes/miscellaneous.dm',
    'code/modules/clothing/suits/armor.dm',
    'code/modules/mapping/map_config.dm',
    'code/modules/mob/dead/new_player/new_player.dm',
    'code/modules/mob/dead/new_player/sprite_accessories/legs_and_taurs.dm',
    'code/modules/mob/living/carbon/carbon_update_icons.dm',
    'code/modules/mob/living/carbon/human/examine.dm',
    'code/modules/mob/living/carbon/human/handling_bodyparts_procs.dm',
    'code/modules/mob/living/carbon/human/human.dm',
    'code/modules/mob/living/carbon/human/human_defense.dm',
    'code/modules/mob/living/carbon/human/human_mobility.dm',
    'code/modules/mob/living/carbon/human/human_movement.dm',
    'code/modules/mob/living/carbon/human/human_update_icons.dm',
    'code/modules/mob/living/carbon/human/innate_abilities/coiling.dm',
    'code/modules/mob/living/carbon/human/innate_abilities/customization.dm',
    'code/modules/mob/living/carbon/human/inventory.dm',
    'code/modules/mob/living/carbon/human/species.dm',
    'code/modules/mob/living/carbon/human/species_types/jellypeople.dm',
    'code/modules/mob/living/carbon/inventory.dm',
    'code/modules/mob/living/emote.dm',
    'code/modules/mob/living/living.dm',
    'code/modules/mob/living/living_defines.dm',
    'code/modules/mob/living/living_mobility.dm',
    'code/modules/mob/mob_helpers.dm',
    'code/modules/mod/_mod_misc/mod_misc_procs.dm',
    'code/modules/mod/mod_activation.dm',
    'code/modules/mod/mod_control.dm',
    'code/modules/mod/modules/_module.dm',
    'code/modules/mod/modules/modules.dm',
    'code/modules/mod/modules/modules_antag.dm',
    'code/modules/mod/modules/modules_engineering.dm',
    'code/modules/mod/modules/modules_general.dm',
    'code/modules/mod/modules/modules_science.dm',
    'code/modules/mod/modules/modules_service.dm',
    'code/modules/modular_computers/computers/item/pda.dm',
    'code/modules/modular_computers/file_system/programs/messenger/messenger_program.dm',
    'code/modules/modular_computers/file_system/programs/ntmonitor.dm',
    'code/modules/power/cable.dm',
    'code/modules/projectiles/boxes_magazines/external/rechargable.dm',
    'code/modules/reagents/chemistry/reagents/toxin_reagents.dm',
    'code/modules/research/designs/limbgrower_designs.dm',
    'code/modules/research/machinery/_production.dm',
    'code/modules/research/rdconsole.dm',
    'code/modules/research/server.dm',
    'code/modules/surgery/amputation.dm',
    'code/modules/surgery/bodyparts/_bodyparts.dm',
    'code/modules/surgery/bodyparts/dismemberment.dm',
    'code/modules/surgery/bodyparts/helpers.dm',
    'code/modules/surgery/bodyparts/parts.dm',
    'code/modules/surgery/helpers.dm',
    'code/modules/surgery/limb_augmentation.dm',
    'code/modules/surgery/organic_steps.dm',
    'code/modules/surgery/prosthetic_replacement.dm',
    'code/modules/surgery/remove_embedded_object.dm',
    'code/modules/surgery/surgery.dm',
    'code/modules/surgery/surgery_step.dm',
    'code/modules/unit_tests/_unit_tests.dm',
    'code/modules/unit_tests/bugreport_fixes_2026_10.dm',
    'code/modules/unit_tests/ghost_role_limbs.dm',
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
    'code/modules/unit_tests/netrun_recycling.dm',
    'code/modules/unit_tests/netrun_refresh.dm',
    'code/modules/unit_tests/netrun_research_authority.dm',
    'code/modules/unit_tests/netrun_routes.dm',
    'code/modules/unit_tests/netrun_routing_cache.dm',
    'code/modules/unit_tests/netrun_scheduler.dm',
    'code/modules/unit_tests/netrun_security_deliveries.dm',
    'code/modules/unit_tests/netrun_topology.dm',
    'code/modules/unit_tests/netrun_transport.dm',
    'code/modules/unit_tests/preferences_single_pref_coalescing.dm',
    'code/modules/unit_tests/space_ruin_planetary_turfs.dm',
    'code/modules/unit_tests/techweb_machine_initialization.dm',
    'code/modules/vehicles/ridden.dm',
    'code/modules/vehicles/scooter.dm',
    'config/spaceRuinBlacklist.txt',
    'dependencies.sh',
    'icons/mob/naga_healthdoll.dmi',
    'icons/obj/naga_tail_item.dmi',
    'modular_bluemoon/code/_HELPERS/_bm_helpers.dm',
    'modular_bluemoon/code/datums/dna.dm',
    'modular_bluemoon/code/datums/elements/holder_micro.dm',
    'modular_bluemoon/code/game/objects/items/summon_chalk.dm',
    'modular_bluemoon/code/modules/antagonists/antag_opt_in/antag_objective_style.dm',
    'modular_bluemoon/code/modules/antagonists/heretic/lock.dm',
    'modular_bluemoon/code/modules/clothing/nanosuit/nanosuit.dm',
    'modular_bluemoon/code/modules/clothing/syndie_edits/syndie_edits.dm',
    'modular_bluemoon/code/modules/lobby/lobby_preferences.dm',
    'modular_bluemoon/code/modules/mob/dead/new_player/sprite_accesories/legs_and_taurs.dm',
    'modular_bluemoon/code/modules/mob/emotes.dm',
    'modular_bluemoon/code/modules/netrun/DEVELOPMENT.md',
    'modular_bluemoon/code/modules/netrun/PERFORMANCE.md',
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
    'modular_bluemoon/code/modules/shift_invites/shift_invites.dm',
    'modular_bluemoon/fluffs/code/melee.dm',
    'modular_bluemoon/fluffs/code/modules/catcrin/code/catcrin_stuff.dm',
    'modular_bluemoon/fluffs/code/modules/catcrin/icons/weapons/hands/guns_left.dmi',
    'modular_bluemoon/fluffs/code/modules/catcrin/icons/weapons/hands/guns_right.dmi',
    'modular_bluemoon/fluffs/code/modules/catcrin/icons/weapons/icons/magweapon.dmi',
    'modular_bluemoon/fluffs/code/suit.dm',
    'modular_bluemoon/icons/mob/mam_taur.dmi',
    'modular_bluemoon/sound/emotes/meow8.ogg',
    'modular_bluemoon/sound/emotes/mrrp_mischief.ogg',
    'modular_bluemoon/white_steel/_subsystem/zclear.dm',
    'modular_bluemoon/white_steel/exploration/fulton_beacon.dm',
    'modular_bluemoon/white_steel/inner/super_cruise/orbital_poi_generator/_orbital_objective.dm',
    'modular_citadel/icons/mob/mam_taur.dmi',
    'modular_sand/code/datums/interactions/lewd_definitions.dm',
    'modular_sand/code/modules/client/preferences_savefile.dm',
    'modular_splurt/_maps/away_mission/AGRComplexClock.dmm',
    'modular_splurt/_maps/away_mission/AGRComplexCult.dmm',
    'modular_splurt/code/datums/traits/neutral_quirks/werewolf.dm',
    'modular_splurt/code/game/objects/items/lewd_items/rope.dm',
    'modular_splurt/code/game/objects/items/lewd_items/shibola.dm',
    'modular_splurt/code/modules/awaymissions/mission_code/AGRComplex.dm',
    'modular_splurt/code/modules/mob/dead/new_player/sprite_accesories/legs_and_taurs.dm',
    'modular_splurt/code/modules/mob/living/emotes.dm',
    'modular_splurt/icons/mob/mam_taur.dmi',
    'tgstation.dme',
    'tgui/packages/tgui/components/ColorPicker.tsx',
    'tgui/packages/tgui/interfaces/CharacterSetup/index.tsx',
    'tgui/packages/tgui/interfaces/CharacterSetup/tabs/Modifications.tsx',
    'tgui/packages/tgui/interfaces/ColorInputModal.tsx',
    'tgui/packages/tgui/interfaces/Healthanalyzer.tsx',
    'tgui/packages/tgui/interfaces/NetrunTerminal/Content.tsx',
    'tgui/packages/tgui/interfaces/NetrunTerminal/ImplantWorkspace.test.tsx',
    'tgui/packages/tgui/styles/interfaces/Healthanalyzer.scss',
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
