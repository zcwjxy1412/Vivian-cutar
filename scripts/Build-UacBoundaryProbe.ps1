$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$target = 'artifacts/m4-uac-boundary-probe-standard'
dotnet publish tools/VIvian.UacBoundaryProbe/VIvian.UacBoundaryProbe.csproj -c Release -r win-x64 --self-contained true -o $target
if ($LASTEXITCODE -ne 0) { throw 'UAC 边界工具编译失败' }
if (-not (Test-Path "$target/VIvian.UacBoundaryProbe.exe") -or -not (Test-Path "$target/coreclr.dll")) { throw '自包含产物不完整' }
Copy-Item 'docs/evidence/M4A2_UAC_TEST_PLAN_2026-11-05.md' "$target/使用说明.md"
Compress-Archive -Path "$target/*" -DestinationPath 'artifacts/VIvian-M4A2-UAC-Boundary-Probe-win-x64.zip' -Force
$hash = (Get-FileHash 'artifacts/VIvian-M4A2-UAC-Boundary-Probe-win-x64.zip' -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  VIvian-M4A2-UAC-Boundary-Probe-win-x64.zip" | Out-File -Encoding ascii 'artifacts/VIvian-M4A2-UAC-Boundary-Probe-win-x64.zip.sha256'
Write-Output "UAC 边界工具 SHA-256: $hash"
