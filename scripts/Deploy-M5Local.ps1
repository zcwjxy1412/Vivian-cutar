[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$workspace = 'E:\auto agent game'
$target = Join-Path $workspace 'VIvian'
$archiveRoot = Join-Path $workspace '项目归档'
$cleanupRoot = Join-Path $workspace '临时清理区'
$commit = (git rev-parse HEAD).Trim()
if ((git status --porcelain).Count -gt 0) { throw '源码有未提交修改；请先提交并测试，避免部署版本与提交号不一致。' }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$stage = Join-Path $workspace ("VIvian-stage-$stamp-" + $commit.Substring(0,7))
if (Test-Path -LiteralPath $stage) { throw '暂存目录已存在，停止部署。' }
New-Item -ItemType Directory -Path $stage | Out-Null
$backup = $null
$installed = $false
try {
  dotnet test tests/VIvian.Tests/VIvian.Tests.csproj -c Release -p:M5DailyTestBuild=true -p:RestoreLockedMode=true
  if ($LASTEXITCODE -ne 0) { throw 'M5 自动化测试失败。' }
  dotnet publish src/VIvian.App/VIvian.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M5DailyTestBuild=true "-p:InformationalVersion=0.5.0-dev+$commit" -o $stage
  if ($LASTEXITCODE -ne 0) { throw 'M5 发布构建失败。' }
  'VIvian M5 daily manual test; isolated data; per-run consent' | Set-Content (Join-Path $stage 'VIvian-M5-DailyTest.marker') -Encoding ascii
  Copy-Item 'docs/M5_DAILY_WINDOWS_TEST.md' (Join-Path $stage '快速上手-每日实训.md')
  @(
    'VIvian M5 固定测试版'
    "版本: 0.5.0-dev+$commit"
    "构建时间(UTC): $((Get-Date).ToUniversalTime().ToString('o'))"
    '数据目录: %LOCALAPPDATA%\VIvian-M5-DailyTest'
  ) | Set-Content (Join-Path $stage 'BUILD-INFO.txt') -Encoding utf8
  & "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path $stage 'VIvian.exe')
  foreach ($p in @(Get-Process -Name VIvian -ErrorAction SilentlyContinue)) {
    $path = try { $p.Path } catch { $null }
    if (-not $path -or $path.StartsWith($target + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
      throw '固定测试版正在运行或无法核对进程路径；请先正常关闭 VIvian，再重新部署。'
    }
  }
  New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
  New-Item -ItemType Directory -Path $cleanupRoot -Force | Out-Null
  if (Test-Path -LiteralPath $target) {
    $resolved = (Resolve-Path -LiteralPath $target).Path
    if ($resolved -ne $target) { throw '固定目录是链接或路径异常，停止部署。' }
    $backup = Join-Path $archiveRoot "VIvian-$stamp"
    Move-Item -LiteralPath $target -Destination $backup
  }
  Move-Item -LiteralPath $stage -Destination $target
  $installed = $true
  & "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path $target 'VIvian.exe')
  $startup = Start-Process -FilePath (Join-Path $target 'VIvian.exe') -ArgumentList '--startup-check' -PassThru -WindowStyle Hidden
  if (-not $startup.WaitForExit(120000)) { throw '固定版自动发现启动检查超时；保留进程与旧版以便诊断。' }
  $startupProof = Join-Path $env:LOCALAPPDATA 'VIvian-M5-DailyTest\startup.ok'
  if ($startup.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $startupProof) -or
      -not ((Get-Content -LiteralPath $startupProof -Raw).Contains($commit))) {
    throw "固定版自动发现启动检查失败（$($startup.ExitCode)）。"
  }
  $desktop = [Environment]::GetFolderPath('Desktop')
  if (-not (Test-Path -LiteralPath $desktop)) { throw '无法找到桌面目录。' }
  $shortcutPath = Join-Path $desktop 'VIvian（开发测试版）.lnk'
  $shell = New-Object -ComObject WScript.Shell
  $shortcut = $shell.CreateShortcut($shortcutPath)
  $shortcut.TargetPath = Join-Path $target 'VIvian.exe'
  $shortcut.WorkingDirectory = $target
  $shortcut.IconLocation = Join-Path $target 'VIvian.exe'
  $shortcut.Description = 'VIvian M5 开发测试版；真实任务需逐次确认'
  $shortcut.Save()
  Write-Output "DEPLOYED=$target"
  Write-Output "SHORTCUT=$shortcutPath"
  Write-Output "ROLLBACK=$backup"
  Write-Output "COMMIT=$commit"
} catch {
  if ($installed -and $backup -and (Test-Path -LiteralPath $backup)) {
    $active = @(Get-Process -Name VIvian -ErrorAction SilentlyContinue | Where-Object {
      try { $_.Path.StartsWith($target + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) }
      catch { $true }
    })
    if ($active.Count -gt 0) { throw '部署后仍有 VIvian 进程运行；未移动运行文件。旧版保留在项目归档，请先正常关闭程序后处理。' }
    $failed = Join-Path $cleanupRoot "VIvian-failed-$stamp"
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $failed }
    Move-Item -LiteralPath $backup -Destination $target
  }
  throw
}
