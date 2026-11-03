[CmdletBinding()]
param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
New-Item -ItemType Directory -Force 'artifacts/logs' | Out-Null
function Invoke-Dotnet([string[]]$Arguments, [string]$Log) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $Log
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
Invoke-Dotnet @('--info') 'artifacts/logs/sdk.log'
Invoke-Dotnet @('restore','VIvian.slnx','--locked-mode','-m:1') 'artifacts/logs/restore.log'
Invoke-Dotnet @('build','VIvian.slnx','-m:1','-c','Release','--no-restore') 'artifacts/logs/build.log'
Invoke-Dotnet @('test','tests/VIvian.Tests','-c','Release','--no-build','--logger','trx;LogFileName=m1.trx','--results-directory','artifacts/tests') 'artifacts/logs/test.log'
Invoke-Dotnet @('publish','src/VIvian.App','-m:1','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true','-o','artifacts/portable') 'artifacts/logs/publish.log'
if ($SmokeTest) { & "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) 'artifacts/portable/VIvian.exe') | Tee-Object -FilePath 'artifacts/logs/smoke.log' }
Compress-Archive -Path 'artifacts/portable/*' -DestinationPath 'artifacts/VIvian-M1-win-x64.zip' -Force
Get-FileHash 'artifacts/VIvian-M1-win-x64.zip' -Algorithm SHA256 | Format-List | Out-File 'artifacts/logs/package-hash.log'
