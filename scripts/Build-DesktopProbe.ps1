$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$target = Join-Path (Get-Location) 'artifacts/desktop-probe'
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($project in @('tools/VIvian.WindowsBoundaryProbe', 'tests/VIvian.FakeEngine', 'tests/VIvian.CrashHost')) {
    $name = Split-Path $project -Leaf
    $output = Join-Path (Get-Location) "artifacts/probe-publish/$name"
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:SourceRevisionId=$env:VIVIAN_SOURCE_COMMIT -o $output
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$project" }
    Copy-Item (Join-Path $output "$name.exe") $target -Force
}
Copy-Item 'scripts/Run-DesktopProbe.ps1' $target -Force
Copy-Item 'scripts/Run-DesktopProbe.cmd' $target -Force
Copy-Item 'docs/DESKTOP_PROBE_README.md' (Join-Path $target '使用说明.md') -Force
@"
源提交：$env:VIVIAN_SOURCE_COMMIT
CI：$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID
用途：阶段 B 无害进程验证；不连接游戏，不读取正式 VIvian 数据。
"@ | Out-File -Encoding utf8 (Join-Path $target 'BUILD-INFO.txt')
& (Join-Path $target 'VIvian.WindowsBoundaryProbe.exe')
if ($LASTEXITCODE -ne 0) { throw "Windows CI 无害探针失败：$LASTEXITCODE" }
Compress-Archive -Path (Join-Path $target '*') -DestinationPath 'artifacts/VIvian-DesktopProbe-win-x64.zip' -Force
$hash = (Get-FileHash 'artifacts/VIvian-DesktopProbe-win-x64.zip' -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  VIvian-DesktopProbe-win-x64.zip" | Out-File -Encoding ascii 'artifacts/VIvian-DesktopProbe-win-x64.zip.sha256'
Write-Output "DesktopProbe SHA-256: $hash"
