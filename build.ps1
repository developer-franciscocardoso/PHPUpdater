[CmdletBinding()]
param(
    [switch]$UseExistingPayload,
    [string]$Dotnet = '',
    [string]$Iscc = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot
Set-Location -LiteralPath $projectRoot
if (!$Dotnet) {
    $Dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $Dotnet)) {
        $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
if (!$Iscc) {
    $Iscc = Join-Path $projectRoot '.tools/inno/ISCC.exe'
    if (!(Test-Path -LiteralPath $Iscc)) {
        $Iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source
    }
}
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File failed with exit code $LASTEXITCODE"
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Invoke-Checked $Dotnet @('run', '--project', 'tests/PHPUpdater.Tests', '-c', 'Release')
Invoke-Checked $Dotnet @('publish', 'src/PHPUpdater', '-c', 'Release',
    '-r', 'win-x64', '--self-contained', 'true', '-o', 'artifacts/engine',
    '-p:DebugType=None', '-p:DebugSymbols=false')
$engine = Join-Path $projectRoot 'artifacts/engine'
$payload = Join-Path $projectRoot 'artifacts/payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
$runtime = Join-Path $payload 'vc_redist.x64.exe'
if (!$UseExistingPayload) {
    Invoke-WebRequest -UseBasicParsing -Uri `
        'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $runtime
    $signature = Get-AuthenticodeSignature -LiteralPath $runtime
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw 'Microsoft runtime signature verification failed'
    }
    Invoke-Checked (Join-Path $engine 'PHPUpdater.exe') @(
        'bundle', '--directory', $payload, '--runtime', $runtime)
}
$manifestPath = Join-Path $payload 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($package in $manifest.Packages) {
    if ([IO.Path]::GetFileName($package.FileName) -ne $package.FileName) {
        throw 'Unsafe package filename in payload manifest'
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath `
        (Join-Path $payload $package.FileName)).Hash
    if ($hash -ne $package.Sha256) {
        throw "Payload checksum mismatch: $($package.FileName)"
    }
}
$files = [ordered]@{}
Get-ChildItem -LiteralPath $engine -File |
    Where-Object Name -ne 'engine-files.json' |
    Sort-Object Name | ForEach-Object {
        $files[$_.Name] = (Get-FileHash -LiteralPath $_.FullName `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    }
$files | ConvertTo-Json | Set-Content -Encoding UTF8 `
    -LiteralPath (Join-Path $engine 'engine-files.json')
$cleanup = [Collections.Generic.List[string]]::new()
$cleanup.Add('procedure CleanupMaintenance();')
$cleanup.Add('var FileName: String;')
$cleanup.Add('begin')
Get-ChildItem -LiteralPath $engine -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    $cleanup.Add("  FileName := ExpandConstant('{app}\uninstall\engine\$($_.Name)');")
    $cleanup.Add("  if FileExists(FileName) and (CompareText(GetSHA256OfFile(FileName), '$hash') = 0) then DeleteFile(FileName);")
}
$cleanup.Add("  RemoveDir(ExpandConstant('{app}\uninstall\engine'));")
$cleanup.Add('end;')
$cleanup | Set-Content -Encoding ASCII `
    -LiteralPath (Join-Path $projectRoot 'artifacts/engine-cleanup.iss')
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
$runtimeHash = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
& (Join-Path $projectRoot 'tests/Bootstrap.Tests.ps1') -Iscc $Iscc
Invoke-Checked $Iscc @('/Qp', "/DProjectRoot=$projectRoot",
    "/DPayloadManifestHash=$manifestHash", "/DRuntimeHash=$runtimeHash",
    (Join-Path $projectRoot 'installer/PHPUpdater.iss'))
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts/dist') -Filter '*.exe' |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$($hash.ToLowerInvariant())  $($_.Name)" | Set-Content -Encoding ASCII `
            -LiteralPath ($_.FullName + '.sha256')
    }
