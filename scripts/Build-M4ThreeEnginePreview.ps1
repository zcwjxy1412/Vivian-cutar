$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$target = 'artifacts/m4-three-engine-preview'
$archive = 'artifacts/VIvian-M4-ThreeEngine-Preview-win-x64.zip'
if ((Test-Path $target) -or (Test-Path $archive)) { throw '集成预览产物已存在，不自动覆盖。' }
$commit = if ($env:VIVIAN_SOURCE_COMMIT) { $env:VIVIAN_SOURCE_COMMIT.Trim() } else { (git rev-parse HEAD).Trim() }
dotnet publish src/VIvian.App/VIvian.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:M4ScreenTestBuild=false "-p:InformationalVersion=0.4.0-dev+$commit" -o $target
if ($LASTEXITCODE -ne 0) { throw '三引擎预览版编译失败' }
if (-not (Test-Path "$target/VIvian.exe") -or -not (Test-Path "$target/coreclr.dll")) { throw '自包含产物不完整' }
'VIvian M4 three-engine preview: simulation-only, isolated data directory' | Set-Content "$target/VIvian-M4-ThreeEngine-Preview.marker" -Encoding ascii
Copy-Item 'docs/M4_THREE_ENGINE_PREVIEW_WINDOWS.md' "$target/使用说明-三引擎预览.md"
& "$PSScriptRoot/Smoke.ps1" -ExePath (Join-Path (Get-Location) "$target/VIvian.exe")
Compress-Archive -Path "$target/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  VIvian-M4-ThreeEngine-Preview-win-x64.zip" | Out-File -Encoding ascii "$archive.sha256"
Write-Output "三引擎预览包 SHA-256: $hash"
