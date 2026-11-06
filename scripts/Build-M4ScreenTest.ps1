$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$target = 'artifacts/m4d-screen-test-r3'
if (Test-Path $target) { throw '单次测试目录已存在，请先检查，不自动覆盖。' }
$commit = if ($env:VIVIAN_SOURCE_COMMIT) { $env:VIVIAN_SOURCE_COMMIT.Trim() } else { (git rev-parse HEAD).Trim() }
dotnet publish src/VIvian.App/VIvian.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M4ScreenTestBuild=true "-p:InformationalVersion=0.4.0-dev+$commit" -o $target
if ($LASTEXITCODE -ne 0) { throw '星铁单次测试版编译失败' }
if (-not (Test-Path "$target/VIvian.exe") -or -not (Test-Path "$target/coreclr.dll")) { throw '自包含产物不完整' }
'VIvian M4-A screen_test: isolated one-attempt data directory' | Set-Content "$target/VIvian-M4A-ScreenTest.marker" -Encoding ascii
Copy-Item 'docs/M4A_SCREEN_TEST_WINDOWS.md' "$target/使用说明-星铁单次测试.md"
& "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) "$target/VIvian.exe")
$archive = 'artifacts/VIvian-M4D-screen-test-r3-win-x64.zip'
if (Test-Path $archive) { throw '单次测试 ZIP 已存在，请先检查，不自动覆盖。' }
Compress-Archive -Path "$target/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  VIvian-M4D-screen-test-r3-win-x64.zip" | Out-File -Encoding ascii "$archive.sha256"
Write-Output "星铁单次测试包 SHA-256: $hash"
