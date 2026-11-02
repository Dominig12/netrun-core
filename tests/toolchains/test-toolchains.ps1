param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/toolchain-functions.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

# Минимальные offline проверки границ доверия bootstrap без Pester и сетевых загрузок.
function Assert-Test {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw "FAILED: $Message" }
}

# Тестовый архив создаётся через ZIP API; опасные имена проверяют защиту от выхода из staging.
function New-TestArchive {
    param([string]$Path, [string]$Entry)
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $stream = $archive.CreateEntry($Entry).Open()
        $writer = New-Object IO.StreamWriter $stream
        try { $writer.Write('test executable content; never executed') }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

# Подменяются только сетевой ввод и запуск программы; проверки hash, ZIP и публикация работают реально.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('netrun-toolchain-tests-' + [guid]::NewGuid().ToString('N'))
$originalCache = $env:NETRUN_TOOLCHAIN_CACHE
$originalPackage = $script:NetrunToolchainPackages.zig
$originalReceive = ${function:Receive-NetrunToolchainArchive}
$originalAttempt = ${function:Receive-NetrunToolchainArchiveAttempt}
$originalMirrors = ${function:Get-NetrunZigMirrors}
$originalProbe = ${function:Get-NetrunToolVersion}
$script:testDownloads = 0
$script:testProbes = 0
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $env:NETRUN_TOOLCHAIN_CACHE = Join-Path $testRoot 'cache'
    Assert-Test (Test-NetrunToolVersion -Version '0.17.0' -Tool zig) 'pinned Zig is accepted'
    Assert-Test (!(Test-NetrunToolVersion -Version '0.18.0' -Tool zig)) 'another Zig release is rejected'
    Assert-Test (Test-NetrunToolVersion -Version '10.0.403' -Tool dotnet) 'pinned SDK is accepted'
    Assert-Test (Test-NetrunToolVersion -Version '10.0.404' -Tool dotnet) 'latestPatch accepts a later patch'
    foreach ($version in @('10.0.402', '10.0.500', '10.1.403', '11.0.403', '10.0.403-preview.1')) {
        Assert-Test (!(Test-NetrunToolVersion -Version $version -Tool dotnet)) "incompatible SDK $version is rejected"
    }

    $traversal = Join-Path $testRoot 'traversal.zip'
    New-TestArchive -Path $traversal -Entry '../escaped.txt'
    $rejected = $false
    try { Expand-NetrunToolchainArchive -Archive $traversal -Destination (Join-Path $testRoot 'unpacked') }
    catch { $rejected = $_.Exception.Message -like '*escaped the selected cache*' }
    Assert-Test $rejected 'ZIP traversal is rejected'
    Assert-Test (!(Test-Path -LiteralPath (Join-Path $testRoot 'escaped.txt'))) 'ZIP traversal writes nothing outside staging'

    $script:testArchive = Join-Path $testRoot 'valid.zip'
    New-TestArchive -Path $script:testArchive -Entry 'zig.exe'
    $script:testAttempts = 0
    $script:testSources = @()
    function Receive-NetrunToolchainArchiveAttempt {
        param([string]$Url, [string]$Destination)
        $script:testAttempts++
        $script:testSources += $Url
        if ($script:testAttempts -eq 1) {
            [IO.File]::WriteAllText($Destination, 'partial download')
            throw 'Simulated network timeout.'
        }
        Copy-Item -LiteralPath $script:testArchive -Destination $Destination
    }
    function Get-NetrunZigMirrors {
        param([string]$FileName)
        return @('https://mirror.invalid/first.zip', 'https://mirror.invalid/second.zip')
    }
    $retryArchive = Join-Path $testRoot 'retry.zip'
    Receive-NetrunToolchainArchive -Url 'https://official.invalid/test.zip' -Destination $retryArchive -ZigMirrors
    Assert-Test ($script:testAttempts -eq 2) 'transient download failure is retried'
    Assert-Test ($script:testSources[1] -eq 'https://mirror.invalid/first.zip') 'official Zig failure switches to a mirror'
    Assert-Test ((Get-FileHash -LiteralPath $retryArchive).Hash -eq (Get-FileHash -LiteralPath $script:testArchive).Hash) 'retry replaces the partial archive'
    $rejected = $false
    try { Receive-NetrunToolchainArchive -Url 'https://official.invalid/test.zip' -Destination $retryArchive }
    catch { $rejected = $_.Exception.Message -like '*already exists*' }
    Assert-Test $rejected 'retry never overwrites a pre-existing destination'
    Set-Item -Path Function:Receive-NetrunToolchainArchiveAttempt -Value $originalAttempt
    Set-Item -Path Function:Get-NetrunZigMirrors -Value $originalMirrors

    $script:NetrunToolchainPackages.zig = @{
        Version = $script:NetrunZigVersion
        Url = 'https://example.invalid/not-used-by-offline-test.zip'
        Algorithm = 'SHA256'
        Hash = ('0' * 64)
        Directory = ''
        Executable = 'zig.exe'
    }
    function Receive-NetrunToolchainArchive {
        param([string]$Url, [string]$Destination)
        $script:testDownloads++
        Copy-Item -LiteralPath $script:testArchive -Destination $Destination
    }
    function Get-NetrunToolVersion {
        param([string]$Path, [string]$Tool)
        $script:testProbes++
        return $script:NetrunZigVersion
    }

    $rejected = $false
    try { Install-NetrunToolchain -Tool zig | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*checksum mismatch*' }
    Assert-Test $rejected 'bad checksum is rejected'
    Assert-Test ($script:testProbes -eq 0) 'unverified executable is never run'
    Assert-Test (@(Get-ChildItem -LiteralPath $env:NETRUN_TOOLCHAIN_CACHE -Directory).Count -eq 0) 'failed installation publishes nothing and cleans staging'

    $script:NetrunToolchainPackages.zig.Hash = (Get-FileHash -LiteralPath $script:testArchive -Algorithm SHA256).Hash
    $resolved = Install-NetrunToolchain -Tool zig
    Assert-Test (Test-Path -LiteralPath $resolved -PathType Leaf) 'verified installation publishes its executable'
    Assert-Test (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $resolved) '.netrun-toolchain.json')) 'completion marker is published together with package'
    $downloads = $script:testDownloads
    Assert-Test ((Install-NetrunToolchain -Tool zig) -eq $resolved) 'warm cache resolves the same executable'
    Assert-Test ($script:testDownloads -eq $downloads) 'warm cache does not download again'

    [IO.File]::AppendAllText($resolved, 'corruption')
    Install-NetrunToolchain -Tool zig | Out-Null
    Assert-Test ($script:testDownloads -eq $downloads + 1) 'corrupted executable is replaced from a verified archive'
    Assert-Test (@(Get-ChildItem -LiteralPath $env:NETRUN_TOOLCHAIN_CACHE -Directory -Filter '.stage-*').Count -eq 0) 'successful installation cleans staging'
    Write-Output 'PASS: version policy, ZIP traversal, bounded retry/mirror fallback, checksum-before-execution, atomic cache, reuse, corruption recovery.'
}
finally {
    $env:NETRUN_TOOLCHAIN_CACHE = $originalCache
    $script:NetrunToolchainPackages.zig = $originalPackage
    Set-Item -Path Function:Receive-NetrunToolchainArchive -Value $originalReceive
    Set-Item -Path Function:Receive-NetrunToolchainArchiveAttempt -Value $originalAttempt
    Set-Item -Path Function:Get-NetrunZigMirrors -Value $originalMirrors
    Set-Item -Path Function:Get-NetrunToolVersion -Value $originalProbe
    # Удаляется только уникальный каталог, созданный этим тестом внутри системного TEMP.
    $safeRoot = Assert-NetrunCacheChild -Root ([IO.Path]::GetTempPath()) -Path $testRoot
    if (Test-Path -LiteralPath $safeRoot) { Remove-Item -LiteralPath $safeRoot -Recurse -Force }
}
