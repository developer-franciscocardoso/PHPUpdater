[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$LegacyPhpArchive,
    [switch]$ConfirmDisposableMachine
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$ConfirmDisposableMachine) {
    throw 'Run only inside a disposable Windows VM, with -ConfirmDisposableMachine.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the VM test elevated from a UAC-enabled administrator account.'
}
$root = Join-Path $env:ProgramFiles 'PHPUpdater'
$data = Join-Path $env:ProgramData 'PHPUpdater'
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $data)) {
    throw 'Use a clean VM without existing PHPUpdater files.'
}
$work = Join-Path $env:TEMP ('PHPUpdater-Smoke-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $work | Out-Null
$original = Join-Path $work 'original-php'
Expand-Archive -LiteralPath $LegacyPhpArchive -DestinationPath $original
$before = @{}
Get-ChildItem -LiteralPath $original -Recurse -File | ForEach-Object {
    $before[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash
}
$originalPath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
[Environment]::SetEnvironmentVariable('Path', "$original;$originalPath", 'Machine')
$env:Path = "$original;$env:Path"
function Run-Setup([string[]]$Options) {
    $process = Start-Process -FilePath $Installer -ArgumentList $Options `
        -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Setup failed with exit code $($process.ExitCode). Logs: $work"
    }
}
Run-Setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/OFFLINE',
    '/PHPBRANCHES=8.2,8.5', '/DEFAULTPHP=8.2', "/LOG=`"$work\install.log`"")
$version = & "$root/bin/php.exe" -r 'echo PHP_VERSION;'
if (!$version.StartsWith('8.2.') -or $version -eq '8.2.1') {
    throw "Wrong default PHP: $version"
}
$parallel = & "$root/bin/php85.exe" -r 'echo PHP_MAJOR_VERSION,chr(46),PHP_MINOR_VERSION;'
if ($parallel -ne '8.5') { throw "Wrong parallel PHP: $parallel" }
Run-Setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/OFFLINE',
    '/PHPBRANCHES=8.2,8.5', '/DEFAULTPHP=8.5', "/LOG=`"$work\repeat.log`"")
$path = [Environment]::GetEnvironmentVariable('Path', 'Machine')
[Environment]::SetEnvironmentVariable('Path', "C:\LaterAddition;$path", 'Machine')
$uninstaller = Get-ChildItem -LiteralPath "$root/uninstall" -Filter 'unins*.exe' |
    Select-Object -First 1
$process = Start-Process -FilePath $uninstaller.FullName -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
    "/LOG=`"$work\uninstall.log`"") -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Uninstall failed. Logs: $work" }
foreach ($file in $before.Keys) {
    if ((Get-FileHash -LiteralPath $file).Hash -ne $before[$file]) {
        throw "Original PHP file changed: $file"
    }
}
$after = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($after -ne "C:\LaterAddition;$original;$originalPath") {
    throw 'Uninstall did not preserve original and subsequent PATH entries.'
}
$restored = & "$original/php.exe" -n -r 'echo PHP_VERSION;'
if ($restored -ne '8.2.1') { throw 'Original PHP is not functional.' }
Write-Output "PASS installer lifecycle. Logs: $work"
