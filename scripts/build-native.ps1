param(
    [ValidateSet('windows-x86', 'windows-x64', 'linux-x86', 'all')]
    [string]$Target = 'windows-x86',
    [string]$ZigPath
)

$ErrorActionPreference = 'Stop'
$netrunRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain-functions.ps1')
# Явный путь и PATH проверяются по версии; при отсутствии Zig загружается проверенный portable пакет.
$ZigPath = Ensure-NetrunZig -ZigPath $ZigPath
$netrunSource = Join-Path $netrunRoot 'native/netrun_bridge.cpp'
$netrunExports = Join-Path $netrunRoot 'native/netrun_bridge.def'
$netrunLinuxExports = Join-Path $netrunRoot 'native/netrun_bridge.map'
$netrunOutput = Join-Path $netrunRoot 'native/bin'
New-Item -ItemType Directory -Path $netrunOutput -Force | Out-Null
$netrunTargets = if ($Target -eq 'all') { @('windows-x86', 'windows-x64', 'linux-x86') } else { @($Target) }

foreach ($netrunTarget in $netrunTargets) {
    # C++ runtime статически поставляется Zig; внешних NuGet/Rust/production пакетов здесь нет.
    $netrunArguments = @('c++', '-std=c++17', '-shared', '-O2', '-Wall', '-Wextra', '-Werror', '-fvisibility=hidden')
    switch ($netrunTarget) {
        'windows-x86' {
            $netrunArguments += @('-target', 'x86-windows-gnu', $netrunSource, $netrunExports,
                '-lws2_32', '-o', (Join-Path $netrunOutput 'netrun_bridge.dll'))
        }
        'windows-x64' {
            $netrunArguments += @('-target', 'x86_64-windows-gnu', $netrunSource, $netrunExports,
                '-lws2_32', '-o', (Join-Path $netrunOutput 'netrun_bridge-x64.dll'))
        }
        'linux-x86' {
            $netrunArguments += @('-target', 'x86-linux-gnu.2.17', $netrunSource, '-pthread', '-ldl', "-Wl,--version-script=$netrunLinuxExports",
                '-o', (Join-Path $netrunOutput 'libnetrun_bridge.so'))
        }
    }
    Write-Output "Building $netrunTarget"
    & $ZigPath @netrunArguments
    if ($LASTEXITCODE -ne 0) { throw "Native build failed: $netrunTarget" }
}
