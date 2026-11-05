[CmdletBinding()]
param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
New-Item -ItemType Directory -Force 'artifacts/logs' | Out-Null
function Invoke-Dotnet([string[]]$Arguments, [string]$Log) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $Log
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
# The exact source commit is stamped into the program (InformationalVersion "x.y.z-dev+<sha>") and BUILD-INFO.txt.
# Pull-request builds check out a temporary merge commit; the workflow passes the branch head separately.
$checkedOut = (& git rev-parse HEAD 2>$null)
if (-not $checkedOut) { $checkedOut = 'unknown' }
$checkedOut = "$checkedOut".Trim()
$commit = if ($env:VIVIAN_SOURCE_COMMIT) { $env:VIVIAN_SOURCE_COMMIT.Trim() } else { $checkedOut }
$revision = @("-p:SourceRevisionId=$commit")

Invoke-Dotnet @('--info') 'artifacts/logs/sdk.log'
Invoke-Dotnet @('restore','VIvian.slnx','--locked-mode','-m:1') 'artifacts/logs/restore.log'
Invoke-Dotnet (@('build','VIvian.slnx','-m:1','-c','Release','--no-restore') + $revision) 'artifacts/logs/build.log'
Invoke-Dotnet @('test','tests/VIvian.Tests','-c','Release','--no-build','--logger','trx;LogFileName=tests.trx','--results-directory','artifacts/tests') 'artifacts/logs/test.log'
Invoke-Dotnet (@('publish','src/VIvian.App','-m:1','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true','-o','artifacts/portable') + $revision) 'artifacts/logs/publish.log'

# Build verification: the portable folder must be runnable without an installed .NET.
$required = @('VIvian.exe','VIvian.dll','hostfxr.dll','hostpolicy.dll','coreclr.dll','e_sqlite3.dll','PresentationFramework.dll')
$missing = $required | Where-Object { -not (Test-Path (Join-Path 'artifacts/portable' $_)) }
if ($missing) { throw "Portable build is incomplete (not self-contained?): $($missing -join ', ')" }
$version = (Get-Item 'artifacts/portable/VIvian.exe').VersionInfo.ProductVersion
if ($version -notmatch '-dev') { throw "Unexpected product version '$version' (a development build must carry -dev)" }
Write-Output "VIvian.exe product version: $version"

if ($SmokeTest) { & "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) 'artifacts/portable/VIvian.exe') | Tee-Object -FilePath 'artifacts/logs/smoke.log' }

Copy-Item 'docs/RUN_ON_WINDOWS.md' 'artifacts/portable/使用说明-开发版.md' -Force
Copy-Item 'docs/RELEASE_NOTES/v0.4.0-dev.md' 'artifacts/portable/发布说明.md' -Force
Copy-Item 'THIRD-PARTY-NOTICES.md' 'artifacts/portable/THIRD-PARTY-NOTICES.md' -Force
@"
VIvian development build (pre-desktop)
Product version : $version
Source commit   : $commit
Checked out     : $checkedOut$(if ($checkedOut -ne $commit) { ' (pull-request merge commit: built from the branch merged into its base)' })
CI event        : $env:GITHUB_EVENT_NAME
Built (UTC)     : $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))
Runtime         : self-contained win-x64 (no separate .NET installation required)
Real engines    : NOT enabled. Simulation engine and read-only engine checks only.
CI run          : $env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID
"@ | Out-File -Encoding utf8 'artifacts/portable/BUILD-INFO.txt'

Compress-Archive -Path 'artifacts/portable/*' -DestinationPath 'artifacts/VIvian-win-x64.zip' -Force
$hash = (Get-FileHash 'artifacts/VIvian-win-x64.zip' -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  VIvian-win-x64.zip" | Out-File -Encoding ascii 'artifacts/VIvian-win-x64.zip.sha256'
Copy-Item 'artifacts/portable/BUILD-INFO.txt' 'artifacts/BUILD-INFO.txt' -Force
Get-FileHash 'artifacts/VIvian-win-x64.zip' -Algorithm SHA256 | Format-List | Out-File 'artifacts/logs/package-hash.log'
Write-Output "Package SHA-256: $hash"
