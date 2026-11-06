# M4-F1: reusable manual real-test build (March7thAssistant screen_test only, each run confirmed by the user).
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$target = 'artifacts/m4f1-manual-test'
$name = 'VIvian-M4-F1-ManualTest-win-x64'
if (Test-Path $target) { throw '手动实测版目录已存在，请先检查，不自动覆盖。' }
$commit = if ($env:VIVIAN_SOURCE_COMMIT) { $env:VIVIAN_SOURCE_COMMIT.Trim() } else { (git rev-parse HEAD).Trim() }
dotnet publish src/VIvian.App/VIvian.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M4ManualTestBuild=true "-p:InformationalVersion=0.4.0-dev+$commit" -o $target
if ($LASTEXITCODE -ne 0) { throw '手动实测版编译失败' }
if (-not (Test-Path "$target/VIvian.exe") -or -not (Test-Path "$target/coreclr.dll")) { throw '自包含产物不完整' }
'VIvian M4-F1 manual real-test build: isolated data directory, per-run user confirmation' | Set-Content "$target/VIvian-M4-F1-ManualTest.marker" -Encoding ascii
Copy-Item 'docs/M4F1_MANUAL_TEST_WINDOWS.md' "$target/快速上手-真实任务测试.md"
@(
  "VIvian M4-F1 手动实测版"
  "版本: 0.4.0-dev+$commit"
  "构建时间(UTC): $((Get-Date).ToUniversalTime().ToString('o'))"
  "构建标志: M4ManualTestBuild=true (M4_MANUAL_TEST)"
  "SDK: $(dotnet --version)"
  "开放的真实任务: March7thAssistant v2026.10.31 screen_test（每次由本人确认）"
  "未开放: 定时/无人值守执行、BetterGI、OneDragon、星铁其他任务"
  "数据目录: %LOCALAPPDATA%\VIvian-M4-ManualTest"
) | Set-Content "$target/BUILD-INFO.txt" -Encoding utf8
& "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) "$target/VIvian.exe")
$archive = "artifacts/$name.zip"
if (Test-Path $archive) { throw '手动实测版 ZIP 已存在，请先检查，不自动覆盖。' }
Compress-Archive -Path "$target/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Out-File -Encoding ascii "$archive.sha256"
Write-Output "手动实测版 SHA-256: $hash"
