[CmdletBinding()]
param(
    [switch]$Integration,
    [string]$LegacyPhpArchive = '',
    [string]$Dotnet = ''
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (!$Dotnet) {
    $Dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $Dotnet)) {
        $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$arguments = @('run', '--project', 'tests/PHPUpdater.Tests', '-c', 'Release')
if ($Integration) {
    if (!$LegacyPhpArchive) {
        $LegacyPhpArchive = Join-Path $PSScriptRoot 'artifacts/fixtures/php-8.2.1.zip'
    }
    if (!(Test-Path -LiteralPath $LegacyPhpArchive)) {
        throw 'Supply -LegacyPhpArchive pointing to the official PHP 8.2.1 x64 NTS ZIP.'
    }
    $arguments += @('--', '--integration',
        (Join-Path $PSScriptRoot 'artifacts/engine/PHPUpdater.exe'),
        (Join-Path $PSScriptRoot 'artifacts/payload'),
        (Resolve-Path -LiteralPath $LegacyPhpArchive).Path)
}
& $Dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'PHPUpdater tests failed' }
