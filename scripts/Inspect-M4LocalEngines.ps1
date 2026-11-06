param([string]$OutputDirectory = 'E:\auto agent game\M4C-batch-check')
$ErrorActionPreference = 'Stop'
$watch = [Diagnostics.Stopwatch]::StartNew()
$names = 'BetterGI.exe','BetterGI_v0.66.0.7z','OneDragon-RuntimeLauncher.exe','WithRuntime-Full.zip','March7thAssistant_full-v2026.10.31.zip','m7a.exe'
$found = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Candidate([string]$path) {
    if (-not [IO.File]::Exists($path) -or -not $seen.Add($path)) { return }
    $file = [IO.FileInfo]::new($path)
    $item = [ordered]@{ name=$file.Name; path=$file.FullName; bytes=$file.Length; sha256=$null; version=$null; requiredFolders=$null; logHeaderSample=$null; autoUpdateCode=$null }
    if ($file.Name -in @('BetterGI.exe','OneDragon-RuntimeLauncher.exe','m7a.exe')) {
        $item.version = [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).ProductVersion
        $item.sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($file.Name -eq 'March7thAssistant_full-v2026.10.31.zip')
        { $item.sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    if ($file.Name -eq 'OneDragon-RuntimeLauncher.exe') {
        $yaml = Join-Path $file.DirectoryName 'config\env.yml'
        if ([IO.File]::Exists($yaml) -and ([IO.FileInfo]::new($yaml)).Length -le 1048576) {
            $matching = @(Select-String -LiteralPath $yaml -Pattern '^\s*auto_update_code\s*:\s*(true|false)\s*(?:#.*)?$' -CaseSensitive:$false)
            if ($matching.Count -eq 1) { $item.autoUpdateCode = $matching[0].Matches[0].Groups[1].Value.ToLowerInvariant() }
        }
    }
    if ($file.Name -eq 'BetterGI.exe') {
        $item.requiredFolders = [ordered]@{ Assets=[IO.Directory]::Exists((Join-Path $file.DirectoryName 'Assets')); GameTask=[IO.Directory]::Exists((Join-Path $file.DirectoryName 'GameTask')) }
        $logFolder = Join-Path $file.DirectoryName 'log'
        $latest = Get-ChildItem -LiteralPath $logFolder -Filter 'better-genshin-impact*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latest) {
            $lines = @(Get-Content -LiteralPath $latest.FullName -Encoding utf8 -TotalCount 120)
            $rx = '^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[[A-Z]{3}\] \[(Primary|ChildSession|WebView):S\d{1,6}:P\d{1,10}:T\d{1,16}\]'
            $item.logHeaderSample = [ordered]@{ file=$latest.Name; linesSampled=$lines.Count; matchingHeaders=@($lines | Where-Object { $_ -match $rx }).Count }
        }
    }
    $found.Add($item)
}
function Scan-Shallow([string]$root, [int]$depth) {
    if (-not [IO.Directory]::Exists($root)) { return }
    $queue = [Collections.Generic.Queue[object]]::new()
    $queue.Enqueue(@($root,0))
    while ($queue.Count -gt 0 -and $watch.Elapsed.TotalSeconds -lt 35) {
        $node = $queue.Dequeue(); $dir = [string]$node[0]; $level = [int]$node[1]
        foreach ($name in $names) { Add-Candidate (Join-Path $dir $name) }
        try {
            foreach ($file in [IO.Directory]::EnumerateFiles($dir)) {
                $name = [IO.Path]::GetFileName($file)
                if ($name -match '^(BetterGI.*\.7z|.*WithRuntime.*Full.*\.zip)$') { Add-Candidate $file }
            }
        } catch [UnauthorizedAccessException],[IO.IOException] { }
        if ($level -ge $depth) { continue }
        try {
            foreach ($child in [IO.Directory]::EnumerateDirectories($dir)) {
                if ([IO.File]::GetAttributes($child).HasFlag([IO.FileAttributes]::ReparsePoint)) { continue }
                if ($level -eq 0 -and $root -eq 'E:\auto agent game' -and
                    [IO.Path]::GetFileName($child) -notmatch 'Better|OneDragon|Zenless|March7th') { continue }
                $queue.Enqueue(@($child,$level+1))
            }
        } catch [UnauthorizedAccessException],[IO.IOException] { }
    }
}
Scan-Shallow 'E:\auto agent game' 2
Scan-Shallow 'C:\Users\胡桃\Downloads' 1
foreach ($dir in @('E:\BetterGI','E:\OneDragon','E:\ZenlessZoneZero-OneDragon','C:\Program Files\BetterGI','C:\Program Files\OneDragon')) { Scan-Shallow $dir 2 }
Add-Candidate 'E:\auto agent game\March7thAssistant-official-v2026.10.31\March7thAssistant_full\m7a.exe'
Add-Candidate 'E:\auto agent game\March7thAssistant_full-v2026.10.31.zip'
$items = @($found)
$report = [ordered]@{
    schema='vivian.m4c-local-check/1'; scannedAt=(Get-Date).ToString('o'); seconds=[Math]::Round($watch.Elapsed.TotalSeconds,2)
    candidates=$items
    betterGiPinnedInstallFound=[bool](@($items | Where-Object { $_.name -eq 'BetterGI.exe' -and $_.version -match '0\.66\.0' }).Count)
    betterGiOfficialArchiveFound=[bool](@($items | Where-Object name -eq 'BetterGI_v0.66.0.7z').Count)
    oneDragonLauncherFound=[bool](@($items | Where-Object name -eq 'OneDragon-RuntimeLauncher.exe').Count)
    oneDragonOfficialArchiveFound=[bool](@($items | Where-Object name -eq 'WithRuntime-Full.zip').Count)
    march7thOfficialArchiveVerified=[bool](@($items | Where-Object { $_.name -eq 'March7thAssistant_full-v2026.10.31.zip' -and $_.sha256 -eq 'e03aaad4ea27c70f30bf5241ab3bfac8894de7675ddd30a9aef7f1e551af9985' }).Count)
}
if (Test-Path -LiteralPath $OutputDirectory) { throw '输出目录已存在；为保护既有报告，请指定新目录。' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory '结果.json') -Encoding utf8
$summary = @(
    'VIvian M4-C 本机批量环境检查'
    "BetterGI 0.66.0 安装：$($report.betterGiPinnedInstallFound)；官方 7z：$($report.betterGiOfficialArchiveFound)"
    "OneDragon 启动器：$($report.oneDragonLauncherFound)；官方 ZIP：$($report.oneDragonOfficialArchiveFound)"
    "March7thAssistant 官方 ZIP 摘要匹配：$($report.march7thOfficialArchiveVerified)"
    "发现相关文件：$($items.Count)；耗时：$($report.seconds) 秒"
    '文件名和版本只用于定位；未取得官方摘要时不宣称发行包身份已核验。'
)
$summary | Set-Content -LiteralPath (Join-Path $OutputDirectory '摘要.txt') -Encoding utf8
$summary
