# Portable инструменты принадлежат NETRUN: системная установка и изменение PATH не выполняются.
$script:NetrunToolchainRoot = Split-Path -Parent $PSScriptRoot
$script:NetrunZigVersion = '0.17.0'
$script:NetrunDotnetVersion = '10.0.403'
# Версии и hash сверены с https://ziglang.org/download/index.json и official .NET 10 release metadata.
$script:NetrunToolchainPackages = @{
    zig = @{
        Version = $script:NetrunZigVersion
        Url = 'https://ziglang.org/download/0.17.0/zig-x86_64-windows-0.17.0.zip'
        Algorithm = 'SHA256'
        Hash = 'b5663f69581dcf391293fbf16c06cb80d81d806545ce618b4d0bab7f0eb8c428'
        Directory = 'zig-x86_64-windows-0.17.0'
        Executable = 'zig.exe'
    }
    dotnet = @{
        Version = $script:NetrunDotnetVersion
        Url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip'
        Algorithm = 'SHA512'
        Hash = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
        Directory = ''
        Executable = 'dotnet.exe'
    }
}

# В success pipeline возвращаются только пути; диагностические сообщения идут отдельными строками.
function Write-NetrunToolchainProgress {
    param([string]$Message)
    Write-Host "[netrun toolchains] $Message"
}

# Каталог кэша не зависит от расположения checkout или конкретной среды разработки.
function Get-NetrunToolchainCache {
    $cache = $env:NETRUN_TOOLCHAIN_CACHE
    if ([string]::IsNullOrWhiteSpace($cache)) {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            throw 'LOCALAPPDATA is unavailable. Set NETRUN_TOOLCHAIN_CACHE to an absolute writable directory.'
        }
        $cache = Join-Path $env:LOCALAPPDATA 'NETRUN/toolchains'
    }
    if (![IO.Path]::IsPathRooted($cache)) {
        throw 'NETRUN_TOOLCHAIN_CACHE must be an absolute path.'
    }
    return [IO.Path]::GetFullPath($cache)
}

# Перед рекурсивным удалением и перемещением проверяем, что путь находится внутри выбранного кэша.
function Assert-NetrunCacheChild {
    param([string]$Root, [string]$Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (!$fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Toolchain operation escaped the selected cache: $fullPath"
    }
    return $fullPath
}

# Проверка версии ограничена по времени; оба потока читаются одновременно, без блокировки pipe.
function Get-NetrunToolVersion {
    param([string]$Path, [ValidateSet('zig', 'dotnet')][string]$Tool)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Path
    $start.Arguments = if ($Tool -eq 'zig') { 'version' } else { '--version' }
    $start.WorkingDirectory = $script:NetrunToolchainRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw "Cannot start $Tool at $Path" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) {
            $process.Kill()
            throw "$Tool version check timed out: $Path"
        }
        $output = $stdout.GetAwaiter().GetResult().Trim()
        $errorOutput = $stderr.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            throw "$Tool version check failed at $Path (exit $($process.ExitCode)): $errorOutput"
        }
        return $output
    }
    finally { $process.Dispose() }
}

# Политика latestPatch из global.json допускает новый patch, но не другую feature band или prerelease SDK.
function Test-NetrunToolVersion {
    param([string]$Version, [ValidateSet('zig', 'dotnet')][string]$Tool)
    if ($Tool -eq 'zig') { return $Version -eq $script:NetrunZigVersion }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { return $false }
    $actual = [version]$Version
    $required = [version]$script:NetrunDotnetVersion
    return $actual.Major -eq $required.Major -and $actual.Minor -eq $required.Minor -and
        [math]::Floor($actual.Build / 100) -eq [math]::Floor($required.Build / 100) -and $actual -ge $required
}

# Явно заданный путь обязателен: ошибка или неподходящая версия не маскируются загрузкой другого инструмента.
function Resolve-NetrunToolOverride {
    param([string]$Path, [ValidateSet('zig', 'dotnet')][string]$Tool)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Explicit $Tool executable does not exist: $Path" }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    $version = Get-NetrunToolVersion -Path $resolved -Tool $Tool
    if (!(Test-NetrunToolVersion -Version $version -Tool $Tool)) {
        throw "Explicit $Tool executable has incompatible version '$version': $resolved"
    }
    Write-NetrunToolchainProgress "Using $Tool $version from $resolved"
    return $resolved
}

# Архив загружается потоком с ограничениями размера, простоя и общего времени; поддерживается PowerShell 5.1.
function Receive-NetrunToolchainArchiveAttempt {
    param([string]$Url, [string]$Destination)
    Add-Type -AssemblyName System.Net.Http
    $previousTls = [Net.ServicePointManager]::SecurityProtocol
    [Net.ServicePointManager]::SecurityProtocol = $previousTls -bor [Net.SecurityProtocolType]::Tls12
    $client = New-Object Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    $response = $null
    $inputStream = $null
    $outputStream = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        Write-NetrunToolchainProgress "Downloading $Url"
        $request = $client.GetAsync($Url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        if (!$request.Wait(60000)) { throw 'Toolchain download did not return headers within 60 seconds.' }
        $response = $request.GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        $length = $response.Content.Headers.ContentLength
        if ($null -ne $length -and $length -gt 1GB) { throw 'Toolchain archive exceeds the 1 GiB download limit.' }
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] 65536
        $total = 0L
        $lastProgress = -10.0
        while ($true) {
            $read = $inputStream.ReadAsync($buffer, 0, $buffer.Length)
            if (!$read.Wait(60000)) { throw 'Toolchain download stalled for 60 seconds.' }
            $count = $read.GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            $total += $count
            if ($total -gt 1GB -or $clock.Elapsed.TotalMinutes -gt 5) { throw 'Toolchain download exceeded its size or 5-minute time limit.' }
            $outputStream.Write($buffer, 0, $count)
            if ($clock.Elapsed.TotalSeconds - $lastProgress -ge 5) {
                Write-NetrunToolchainProgress ('Downloaded {0:N1} MiB' -f ($total / 1MB))
                $lastProgress = $clock.Elapsed.TotalSeconds
            }
        }
        if ($total -eq 0 -or ($null -ne $length -and $total -ne $length)) { throw 'Toolchain download is incomplete.' }
    }
    finally {
        if ($outputStream) { $outputStream.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        if ($response) { $response.Dispose() }
        $client.Dispose()
        [Net.ServicePointManager]::SecurityProtocol = $previousTls
    }
}

# Повторяем только загрузку: неполный архив удаляется, а проверка hash остаётся обязательной после успеха.
function Receive-NetrunToolchainArchive {
    param([string]$Url, [string]$Destination, [switch]$ZigMirrors)
    if (Test-Path -LiteralPath $Destination) { throw "Download destination already exists: $Destination" }
    $urls = @($Url)
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $sourceUrl = $urls[[math]::Min($attempt - 1, $urls.Count - 1)]
            Receive-NetrunToolchainArchiveAttempt -Url $sourceUrl -Destination $Destination
            return
        }
        catch {
            if (Test-Path -LiteralPath $Destination -PathType Leaf) { [IO.File]::Delete($Destination) }
            if ($attempt -eq 3) { throw }
            Write-NetrunToolchainProgress "Download attempt $attempt failed: $($_.Exception.Message). Retrying..."
            if ($attempt -eq 1 -and $ZigMirrors) {
                # Зеркала получают только уже зафиксированный архив; доверие даёт reviewed SHA-256, не сервер зеркала.
                $urls += @(Get-NetrunZigMirrors -FileName ([IO.Path]::GetFileName(([uri]$Url).AbsolutePath)))
            }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

# Zig рекомендует зеркала для автоматизации; берём до двух HTTPS адресов из опубликованного списка проекта.
function Get-NetrunZigMirrors {
    param([string]$FileName)
    try {
        $list = Invoke-RestMethod -Uri 'https://ziglang.org/download/community-mirrors.txt' -TimeoutSec 20
        if ($list -isnot [string] -or $list.Length -gt 65536) { throw 'Invalid mirror list.' }
        $mirrors = @($list -split '\r?\n' | Where-Object {
            $uri = $null
            [uri]::TryCreate($_, [UriKind]::Absolute, [ref]$uri) -and $uri.Scheme -eq 'https' -and
                !$uri.UserInfo -and !$uri.Query -and !$uri.Fragment
        } | Select-Object -Unique)
        if ($mirrors.Count -eq 0) { throw 'No HTTPS mirrors in the list.' }
        # Fastly доступен и при проблемах маршрута к небольшим хостингам; второй адрес выбирается случайно.
        $fastly = 'https://ziglang.freetls.fastly.net'
        if ($mirrors -contains $fastly) {
            $selected = @($fastly)
            $remaining = @($mirrors | Where-Object { $_ -ne $fastly })
            if ($remaining.Count -gt 0) { $selected += @($remaining | Get-Random -Count 1) }
        }
        else { $selected = @($mirrors | Get-Random -Count ([math]::Min(2, $mirrors.Count))) }
        foreach ($mirror in $selected) { $mirror.TrimEnd('/') + '/' + $FileName + '?source=netrun-core' }
    }
    catch { Write-NetrunToolchainProgress 'Zig mirror list is unavailable; retrying the official download.' }
}

# Распаковка идёт в новый staging: выход за его пределы и чрезмерный размер ZIP отклоняются.
function Expand-NetrunToolchainArchive {
    param([string]$Archive, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastProgress = -10.0
    $total = 0L
    try {
        if ($zip.Entries.Count -gt 100000) { throw 'Toolchain ZIP contains too many entries.' }
        foreach ($entry in $zip.Entries) {
            $total += $entry.Length
            if ($total -gt 6GB -or $clock.Elapsed.TotalMinutes -gt 15) { throw 'Toolchain extraction exceeded its size or 15-minute time limit.' }
            if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains(':')) { throw 'Toolchain ZIP contains an absolute entry path.' }
            $path = Assert-NetrunCacheChild -Root $Destination -Path (Join-Path $Destination $entry.FullName)
            if ([string]::IsNullOrEmpty($entry.Name)) {
                [IO.Directory]::CreateDirectory($path) | Out-Null
                continue
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            $source = $entry.Open()
            $target = $null
            try {
                $target = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $source.CopyTo($target)
            }
            finally {
                if ($target) { $target.Dispose() }
                $source.Dispose()
            }
            if ($clock.Elapsed.TotalSeconds - $lastProgress -ge 5) {
                Write-NetrunToolchainProgress ('Extracted {0:N1} MiB' -f ($total / 1MB))
                $lastProgress = $clock.Elapsed.TotalSeconds
            }
        }
    }
    finally { $zip.Dispose() }
}

# Блокировкой владеет открытый handle: завершение процесса освобождает её даже при прерванной установке.
function Enter-NetrunToolchainLock {
    param([string]$Path)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastProgress = -10.0
    while ($true) {
        try { return [IO.File]::Open($Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ($clock.Elapsed.TotalMinutes -ge 20) { throw "Timed out waiting for another toolchain installation: $Path" }
            if ($clock.Elapsed.TotalSeconds - $lastProgress -ge 10) {
                Write-NetrunToolchainProgress 'Waiting for another toolchain installation to finish...'
                $lastProgress = $clock.Elapsed.TotalSeconds
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

# Повторно используем только завершённую установку нужной версии с неповреждённым executable.
function Test-NetrunCachedToolchain {
    param([string]$Directory, [hashtable]$Package, [string]$Tool)
    $executable = Join-Path $Directory $Package.Executable
    $marker = Join-Path $Directory '.netrun-toolchain.json'
    if (!(Test-Path -LiteralPath $executable -PathType Leaf) -or !(Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    try {
        $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($state.version -ne $Package.Version -or $state.archiveHash -ne $Package.Hash) { return $false }
        if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $state.executableHash) { return $false }
        return Test-NetrunToolVersion -Version (Get-NetrunToolVersion -Path $executable -Tool $Tool) -Tool $Tool
    }
    catch { return $false }
}

# Проверенный пакет публикуется rename в той же файловой системе; installers используют общую блокировку.
function Install-NetrunToolchain {
    param([ValidateSet('zig', 'dotnet')][string]$Tool)
    if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
        throw 'Automatic toolchain downloads support Windows x64. Provide a compatible executable explicitly on other platforms.'
    }
    $package = $script:NetrunToolchainPackages[$Tool]
    $cache = Get-NetrunToolchainCache
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $name = "$Tool-$($package.Version)-windows-x64"
    $directory = Assert-NetrunCacheChild -Root $cache -Path (Join-Path $cache $name)
    $lock = Enter-NetrunToolchainLock -Path (Join-Path $cache "$name.lock")
    $stage = $null
    $previous = $null
    try {
        if (Test-NetrunCachedToolchain -Directory $directory -Package $package -Tool $Tool) {
            Write-NetrunToolchainProgress "Using cached $Tool $($package.Version)"
            return Join-Path $directory $package.Executable
        }
        $stage = Assert-NetrunCacheChild -Root $cache -Path (Join-Path $cache (".stage-$name-" + [guid]::NewGuid().ToString('N')))
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        $archive = Join-Path $stage 'download.zip'
        Receive-NetrunToolchainArchive -Url $package.Url -Destination $archive -ZigMirrors:($Tool -eq 'zig')
        Write-NetrunToolchainProgress "Verifying $Tool archive ($($package.Algorithm))"
        if ((Get-FileHash -LiteralPath $archive -Algorithm $package.Algorithm).Hash -ne $package.Hash) {
            throw "$Tool archive checksum mismatch. The downloaded package will not be executed."
        }
        $extracted = Join-Path $stage 'package'
        Expand-NetrunToolchainArchive -Archive $archive -Destination $extracted
        $payload = if ($package.Directory) { Join-Path $extracted $package.Directory } else { $extracted }
        $executable = Join-Path $payload $package.Executable
        $version = Get-NetrunToolVersion -Path $executable -Tool $Tool
        if ($version -ne $package.Version) { throw "Downloaded $Tool has unexpected version '$version'." }
        $state = [ordered]@{
            version = $package.Version
            archiveHash = $package.Hash
            executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        }
        [IO.File]::WriteAllText((Join-Path $payload '.netrun-toolchain.json'), ($state | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding $false))
        # Предыдущая установка сохраняется, пока проверенная замена не опубликована успешно.
        if (Test-Path -LiteralPath $directory) {
            $previous = Assert-NetrunCacheChild -Root $cache -Path (Join-Path $cache (".invalid-$name-" + [guid]::NewGuid().ToString('N')))
            [IO.Directory]::Move($directory, $previous)
        }
        $payload = Assert-NetrunCacheChild -Root $stage -Path $payload
        [IO.Directory]::Move($payload, $directory)
        Write-NetrunToolchainProgress "Ready: $Tool $version in $directory"
        return Join-Path $directory $package.Executable
    }
    finally {
        try {
            if ($previous -and (Test-Path -LiteralPath $previous)) {
                $previous = Assert-NetrunCacheChild -Root $cache -Path $previous
                if (!(Test-Path -LiteralPath $directory)) { [IO.Directory]::Move($previous, $directory) }
                else { Remove-Item -LiteralPath $previous -Recurse -Force }
            }
            if ($stage -and (Test-Path -LiteralPath $stage)) {
                $stage = Assert-NetrunCacheChild -Root $cache -Path $stage
                Remove-Item -LiteralPath $stage -Recurse -Force
            }
        }
        finally { $lock.Dispose() }
    }
}

# Сначала проверяем явный путь и подходящий PATH; portable пакет загружается только при их отсутствии.
function Resolve-NetrunToolchain {
    param([ValidateSet('zig', 'dotnet')][string]$Tool, [string]$Path)
    if ($Tool -eq 'dotnet') {
        $policy = Get-Content -LiteralPath (Join-Path $script:NetrunToolchainRoot 'global.json') -Raw | ConvertFrom-Json
        if ($policy.sdk.version -ne $script:NetrunDotnetVersion -or $policy.sdk.rollForward -ne 'latestPatch' -or $policy.sdk.allowPrerelease -ne $false) {
            throw 'global.json changed. Update the reviewed .NET toolchain version, policy, URL, and checksum together.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = if ($Tool -eq 'zig') { $env:NETRUN_ZIG } else { $env:NETRUN_DOTNET }
    }
    if (![string]::IsNullOrWhiteSpace($Path)) { return Resolve-NetrunToolOverride -Path $Path -Tool $Tool }
    $commands = @(Get-Command -Name $Tool -CommandType Application -All -ErrorAction SilentlyContinue)
    foreach ($command in $commands) {
        try {
            $version = Get-NetrunToolVersion -Path $command.Source -Tool $Tool
            if (Test-NetrunToolVersion -Version $version -Tool $Tool) {
                Write-NetrunToolchainProgress "Using $Tool $version from PATH: $($command.Source)"
                return $command.Source
            }
        }
        catch { Write-NetrunToolchainProgress "Skipping unusable $Tool on PATH: $($command.Source)" }
    }
    return Install-NetrunToolchain -Tool $Tool
}

# Все native targets собираются одной зафиксированной версией C++ toolchain.
function Ensure-NetrunZig {
    param([string]$ZigPath)
    return Resolve-NetrunToolchain -Tool zig -Path $ZigPath
}

# SDK выбирается согласно global.json этого репозитория.
function Ensure-NetrunDotnet {
    param([string]$DotnetPath)
    return Resolve-NetrunToolchain -Tool dotnet -Path $DotnetPath
}
