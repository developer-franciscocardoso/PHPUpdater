[CmdletBinding()]
param([string]$Iscc = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$Iscc) {
    $Iscc = Join-Path $projectRoot '.tools/inno/ISCC.exe'
    if (!(Test-Path -LiteralPath $Iscc)) {
        $Iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source
    }
}
$manifest = Join-Path $projectRoot 'artifacts/payload/manifest.json'
$hash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
& $Iscc /Qp "/DProjectRoot=$projectRoot" "/DPayloadManifestHash=$hash" `
    (Join-Path $PSScriptRoot 'Bootstrap.iss')
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap test compilation failed.' }
$report = Join-Path $projectRoot 'artifacts/tests/bootstrap-result.txt'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
$test = Join-Path $projectRoot 'artifacts/tests/Bootstrap-Test.exe'
$process = Start-Process -FilePath $test -WindowStyle Hidden -PassThru `
    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', "/RESULT=`"$report`"")
if (!$process.WaitForExit(60000)) {
    $process.Kill()
    throw 'Bootstrap test timed out.'
}
if (!(Test-Path -LiteralPath $report)) {
    throw 'Bootstrap test did not produce a result.'
}
$result = Get-Content -LiteralPath $report -Raw
if ($result -ne 'PASS') { throw $result }
Write-Output 'PASS Inno temporary extraction and self-contained helper startup'
