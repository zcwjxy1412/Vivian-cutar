[CmdletBinding()]
param([string]$Output = (Join-Path $env:TEMP 'furina-court-interactive-preview'))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
# Build/copy only; never starts an interactive window. Use the documented command explicitly.
dotnet publish (Join-Path $PSScriptRoot 'CourtPreview.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M5DailyTestBuild=false -p:M4ManualTestBuild=false -p:M4ScreenTestBuild=false -p:UnifiedInstallBuild=false -p:GenshinRewardAcceptanceBuild=false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Preview build failed' }
$files = Get-ChildItem -LiteralPath $Output -File -Recurse | Where-Object { $_.FullName -notlike '*\verification\*' -and $_.Name -ne 'package-sha256.json' }
$files | ForEach-Object { [pscustomobject]@{path=[IO.Path]::GetRelativePath($Output, $_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'package-sha256.json')
& (Join-Path $Output 'CourtPreview.exe') --verify
if ($LASTEXITCODE -ne 0) { throw 'Preview offline verification failed' }
Write-Output "Ready for coordinator launch (not started): $Output\CourtPreview.exe --interactive"
