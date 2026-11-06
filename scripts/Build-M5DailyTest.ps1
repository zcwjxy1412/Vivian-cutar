$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$target = 'artifacts/m5-daily-test'
$name = 'VIvian-M5-DailyTest-win-x64'
if (Test-Path $target) { throw 'M5 实测目录已存在，请先检查，不自动覆盖。' }
$commit = if ($env:VIVIAN_SOURCE_COMMIT) { $env:VIVIAN_SOURCE_COMMIT.Trim() } else { (git rev-parse HEAD).Trim() }
dotnet publish src/VIvian.App/VIvian.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M5DailyTestBuild=true "-p:InformationalVersion=0.5.0-dev+$commit" -o $target
if ($LASTEXITCODE -ne 0) { throw 'M5 实测版编译失败' }
if (-not (Test-Path "$target/VIvian.exe") -or -not (Test-Path "$target/coreclr.dll")) { throw '自包含产物不完整' }
'VIvian M5 daily manual test; isolated data; per-run consent' | Set-Content "$target/VIvian-M5-DailyTest.marker" -Encoding ascii
Copy-Item 'docs/M5_DAILY_WINDOWS_TEST.md' "$target/快速上手-每日实训.md"
@(
  "VIvian M5 每日实训手动实测版"
  "版本: 0.5.0-dev+$commit"
  "构建时间(UTC): $((Get-Date).ToUniversalTime().ToString('o'))"
  "构建标志: M5DailyTestBuild=true (M5_DAILY_TEST)"
  "SDK: $(dotnet --version)"
  "开放的真实任务: March7thAssistant v2026.10.31 daily（每次由本人确认）"
  "未开放: 定时/无人值守执行、BetterGI、OneDragon、星铁其他任务"
  "数据目录: %LOCALAPPDATA%\VIvian-M5-DailyTest"
) | Set-Content "$target/BUILD-INFO.txt" -Encoding utf8
& "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) "$target/VIvian.exe")
$archive = "artifacts/$name.zip"
if (Test-Path $archive) { throw 'M5 ZIP 已存在，请先检查，不自动覆盖。' }
Compress-Archive -Path "$target/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Out-File -Encoding ascii "$archive.sha256"
Write-Output "M5 每日实训包 SHA-256: $hash"
