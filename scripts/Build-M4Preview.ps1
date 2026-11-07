$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$source = 'artifacts/portable'
$target = 'artifacts/m4a-preview'
if (-not (Test-Path "$source/VIvian.exe")) { throw '请先运行 Build.ps1 生成已验证的便携版。' }
if (Test-Path $target) { throw '预览目录已存在，请先检查，不自动覆盖。' }
Copy-Item $source $target -Recurse
Copy-Item 'docs/M4A_PREVIEW_WINDOWS.md' "$target/使用说明-M4A预览.md"
'VIvian M4-A preview: isolated data directory' | Set-Content "$target/VIvian-M4A-Preview.marker" -Encoding ascii
'@echo off' + "`r`n" + 'cd /d "%~dp0"' + "`r`n" + '"%~dp0VIvian.exe" --m4-preview' + "`r`n" |
    Set-Content "$target/启动M4A预览.cmd" -Encoding ascii
$archive = 'artifacts/VIvian-M4A-preview-win-x64.zip'
if (Test-Path $archive) { throw '预览 ZIP 已存在，请先检查，不自动覆盖。' }
Compress-Archive -Path "$target/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  VIvian-M4A-preview-win-x64.zip" | Out-File -Encoding ascii "$archive.sha256"
Write-Output "M4A 预览包 SHA-256: $hash"
