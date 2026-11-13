param(
  # CI keeps the default. A local run passes a fresh directory; existing output is never overwritten.
  [string]$Target = 'artifacts/m5-2-test-ci',
  [switch]$SkipSmoke,
  [switch]$UnifiedInstall,
  [switch]$GenshinRewardAcceptance
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($GenshinRewardAcceptance -and (-not $UnifiedInstall -or -not $SkipSmoke)) {
  throw '原神领奖验收候选需同时指定 -UnifiedInstall 和 -SkipSmoke。'
}
$name = if ($GenshinRewardAcceptance) { 'VIvian-M6-GenshinRewardAcceptance-win-x64' }
  elseif ($UnifiedInstall) { 'VIvian-M6-Unified-win-x64' } else { 'VIvian-M5.2-AutoSwitch-win-x64' }
if (Test-Path $Target) { throw "M5.2 输出目录已存在：$Target。请先检查，不自动覆盖。" }
$commit = if ($env:VIVIAN_SOURCE_COMMIT) { $env:VIVIAN_SOURCE_COMMIT.Trim() } else { (git rev-parse HEAD).Trim() }
$schema = [regex]::Match((Get-Content 'src/VIvian.Infrastructure/LocalDatabase.cs' -Raw), 'CurrentSchemaVersion = (\d+);').Groups[1].Value
if (-not $schema) { throw '无法读取数据库版本，拒绝生成不准确的构建信息。' }
dotnet publish src/VIvian.App/VIvian.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M5DailyTestBuild=true "-p:UnifiedInstallBuild=$($UnifiedInstall.IsPresent)" "-p:GenshinRewardAcceptanceBuild=$($GenshinRewardAcceptance.IsPresent)" "-p:InformationalVersion=0.5.2-dev+$commit" -o $Target
if ($LASTEXITCODE -ne 0) { throw 'M5.2 实测版编译失败' }
if (-not (Test-Path "$Target/VIvian.exe") -or -not (Test-Path "$Target/coreclr.dll")) { throw '自包含产物不完整' }
# The compiled flavor and marker must agree. A normal launch of the unified flavor needs a prepared DB.
if ($UnifiedInstall) {
  'VIvian unified install; prepared profile required' | Set-Content "$Target/VIvian-UnifiedInstall.marker" -Encoding ascii
  Copy-Item 'docs/parallel/unified-entry.md' "$Target/M6-单入口说明.md"
} else {
  'VIvian M5 daily test; isolated data; elevation before batch' | Set-Content "$Target/VIvian-M5-DailyTest.marker" -Encoding ascii
}
if ($GenshinRewardAcceptance) {
  'VIvian Genshin reward or saved-daily acceptance' | Set-Content "$Target/VIvian-GenshinRewardAcceptance.marker" -Encoding ascii
}
Copy-Item 'docs/M5_2_DELIVERY.md' "$Target/使用说明-M5.2.md"
Copy-Item 'docs/M5_2_ACCEPTANCE.md' "$Target/验收状态-M5.2.md"
Copy-Item 'docs/parallel/ai-status.md' "$Target/M6-AI验收说明.md"
Copy-Item 'scripts/Initialize-M6DesktopLock.ps1' "$Target/Initialize-M6DesktopLock.ps1"
Copy-Item 'docs/M5_2_ACCOUNT_SWITCH_SCREEN_FLOW.md' "$Target/账号切换截图流程.md"
@(
  $(if ($UnifiedInstall) { "VIvian M6 合流安装候选" } else { "VIvian M6 星铁隔离测试版" })
  "版本: 0.5.2-dev+$commit"
  "源码提交: $commit"
  "构建时间(UTC): $((Get-Date).ToUniversalTime().ToString('o'))"
  "构建标志: M5DailyTestBuild=true UnifiedInstallBuild=$($UnifiedInstall.IsPresent) GenshinRewardAcceptanceBuild=$($GenshinRewardAcceptance.IsPresent)"
  "SDK: $(dotnet --version)"
  $(if ($GenshinRewardAcceptance) { "真实任务: --genshin-reward-acceptance 为单账号零树脂领奖；--genshin-daily-acceptance 按已保存配置执行勾选账号日常与切号" }
    elseif ($UnifiedInstall) { "真实任务: 星铁日常、原神 BetterGI 保存计划、绝区零 OneDragon v2.6.0 保存应用组" }
    else { "真实任务: March7thAssistant v2026.10.31 日常（预先配置，一键批次无逐项确认）" })
  $(if (-not $GenshinRewardAcceptance) { "多账号: 已保存账号切换；打开程序时由本人完成 UAC；批次内不提权；身份不明即停止" })
  $(if ($GenshinRewardAcceptance) { "数据目录: %LOCALAPPDATA%\VIvian-M6-GenshinAcceptance\<验收编号>（独立合流快照）" }
    elseif ($UnifiedInstall) { "数据目录: 默认 %LOCALAPPDATA%\VIvian-UserData；vNext 实机验收必须使用 --vnext-acceptance <专用副本目录>（已验证合流库，SQLite schema v$schema）" }
    else { "数据目录: %LOCALAPPDATA%\VIvian-M6-StarRailTest（隔离档案，SQLite schema v$schema）" })
) | Set-Content "$Target/BUILD-INFO.txt" -Encoding utf8
if (-not $SkipSmoke) { & "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) "$Target/VIvian.exe") }
$archive = "$(Split-Path $Target -Parent)/$name.zip"
if (Test-Path $archive) { throw "M5.2 ZIP 已存在：$archive。请先检查，不自动覆盖。" }
Compress-Archive -Path "$Target/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Out-File -Encoding ascii "$archive.sha256"
Write-Output "M5.2 实测包 SHA-256: $hash"
