$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$target = 'artifacts/m4-probe'
dotnet publish tools/VIvian.March7thProbe/VIvian.March7thProbe.csproj -c Release -r win-x64 --self-contained true -o $target
if ($LASTEXITCODE -ne 0) { throw '星铁状态探针编译失败' }
if (-not (Test-Path "$target/VIvian.March7thProbe.exe") -or -not (Test-Path "$target/coreclr.dll")) { throw '自包含产物不完整' }
Copy-Item 'scripts/Run-March7thProbe.cmd' "$target/运行星铁状态检查.cmd"
Copy-Item 'docs/M4_MARCH7TH_PROBE.md' "$target/使用说明.md"
Compress-Archive -Path "$target/*" -DestinationPath 'artifacts/VIvian-M4-March7th-Status-Probe-win-x64.zip' -Force
$hash = (Get-FileHash 'artifacts/VIvian-M4-March7th-Status-Probe-win-x64.zip' -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  VIvian-M4-March7th-Status-Probe-win-x64.zip" | Out-File -Encoding ascii 'artifacts/VIvian-M4-March7th-Status-Probe-win-x64.zip.sha256'
Write-Output "星铁状态探针 SHA-256: $hash"
