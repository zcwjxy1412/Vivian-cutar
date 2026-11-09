param(
  # CI keeps the default. A local run passes a fresh directory; existing output is never overwritten.
  [string]$Target = 'artifacts/m5-2-test-ci',
  [switch]$SkipSmoke
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$name = 'VIvian-M5.2-AutoSwitch-win-x64'
if (Test-Path $Target) { throw "M5.2 输出目录已存在：$Target。请先检查，不自动覆盖。" }
$commit = if ($env:VIVIAN_SOURCE_COMMIT) { $env:VIVIAN_SOURCE_COMMIT.Trim() } else { (git rev-parse HEAD).Trim() }
dotnet publish src/VIvian.App/VIvian.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M5DailyTestBuild=true "-p:InformationalVersion=0.5.2-dev+$commit" -o $Target
if ($LASTEXITCODE -ne 0) { throw 'M5.2 实测版编译失败' }
if (-not (Test-Path "$Target/VIvian.exe") -or -not (Test-Path "$Target/coreclr.dll")) { throw '自包含产物不完整' }
# The marker selects the existing execution mode. This branch uses a separate M6 data directory.
'VIvian M5 daily test; isolated data; elevation before batch' | Set-Content "$Target/VIvian-M5-DailyTest.marker" -Encoding ascii
Copy-Item 'docs/M5_2_DELIVERY.md' "$Target/使用说明-M5.2.md"
Copy-Item 'docs/M5_2_ACCEPTANCE.md' "$Target/验收状态-M5.2.md"
Copy-Item 'docs/M5_2_ACCOUNT_SWITCH_SCREEN_FLOW.md' "$Target/账号切换截图流程.md"
@(
  "VIvian M5.2 自动账号切换实测版"
  "版本: 0.5.2-dev+$commit"
  "源码提交: $commit"
  "构建时间(UTC): $((Get-Date).ToUniversalTime().ToString('o'))"
  "构建标志: M5DailyTestBuild=true (M5_DAILY_TEST)"
  "SDK: $(dotnet --version)"
  "真实任务: March7thAssistant v2026.10.31 日常（预先配置，一键批次无逐项确认）"
  "多账号: 已保存账号切换；打开程序时由本人完成 UAC；批次内不提权；身份不明即停止"
  "数据目录: %LOCALAPPDATA%\VIvian-M6-StarRailTest（隔离档案，SQLite schema v7）"
) | Set-Content "$Target/BUILD-INFO.txt" -Encoding utf8
if (-not $SkipSmoke) { & "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) "$Target/VIvian.exe") }
$archive = "$(Split-Path $Target -Parent)/$name.zip"
if (Test-Path $archive) { throw "M5.2 ZIP 已存在：$archive。请先检查，不自动覆盖。" }
Compress-Archive -Path "$Target/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Out-File -Encoding ascii "$archive.sha256"
Write-Output "M5.2 实测包 SHA-256: $hash"
