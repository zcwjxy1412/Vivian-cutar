# Fixed local entry point: full regression (all build flavours) + M5.2 package for the current commit.
# Writes only under artifacts/m5-2-local-<sha>/; never touches %LOCALAPPDATA% data or an existing folder.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$sha = (git rev-parse HEAD).Trim()
$dirty = git status --porcelain
$root = "artifacts/m5-2-local-$($sha.Substring(0,7))"
if (Test-Path $root) { throw "$root 已存在，请先检查，不自动覆盖。" }
New-Item -ItemType Directory $root | Out-Null
$log = "$root/run.log"
"commit=$sha dirty=$([bool]$dirty) started=$((Get-Date).ToUniversalTime().ToString('o')) sdk=$(dotnet --version) os=$([Environment]::OSVersion.VersionString)" | Tee-Object $log
$runs = @(
  @{ Name = 'default'; Args = @() },
  @{ Name = 'm4-manual'; Args = @('-p:M4ManualTestBuild=true') },
  @{ Name = 'm5-daily'; Args = @('-p:M5DailyTestBuild=true') }
)
$failed = $false
foreach ($run in $runs) {
  dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release -p:RestoreLockedMode=true @($run.Args) --results-directory "$root/tests" --logger "trx;LogFileName=$($run.Name).trx" 2>&1 | Tee-Object -Append $log
  "exit[$($run.Name)]=$LASTEXITCODE" | Tee-Object -Append $log
  if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) { throw "回归测试存在失败，未打包。详见 $log" }
$env:VIVIAN_SOURCE_COMMIT = $sha
& "$PSScriptRoot/Build-M5_2Test.ps1" -Target "$root/package" 2>&1 | Tee-Object -Append $log
"finished=$((Get-Date).ToUniversalTime().ToString('o'))" | Tee-Object -Append $log
