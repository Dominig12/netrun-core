param(
    [ValidateSet('zig', 'dotnet', 'all')]
    [string]$Tool = 'all',
    [string]$ZigPath,
    [string]$DotnetPath,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
# UTF-8 позволяет игровому builder корректно читать пути, содержащие кириллицу и другие Unicode символы.
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
$OutputEncoding = [Console]::OutputEncoding
. (Join-Path $PSScriptRoot 'toolchain-functions.ps1')

$result = [ordered]@{ dotnet = $null; zig = $null }
if ($Tool -eq 'dotnet' -or $Tool -eq 'all') { $result.dotnet = Ensure-NetrunDotnet -DotnetPath $DotnetPath }
if ($Tool -eq 'zig' -or $Tool -eq 'all') { $result.zig = Ensure-NetrunZig -ZigPath $ZigPath }
if ($Json) { Write-Output ('NETRUN_TOOLCHAINS:' + ($result | ConvertTo-Json -Compress)) }
else { [pscustomobject]$result }
