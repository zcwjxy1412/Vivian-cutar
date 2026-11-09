[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackageDirectory,
    [Parameter(Mandatory)][string]$CandidatePackageDirectory
)
$ErrorActionPreference = 'Stop'
$previous = (Resolve-Path -LiteralPath $PreviousPackageDirectory).Path
$candidate = (Resolve-Path -LiteralPath $CandidatePackageDirectory).Path
$dotnetHost = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (!(Test-Path -LiteralPath (Join-Path $candidate 'VIvian-UnifiedInstall.marker') -PathType Leaf) -or
    (Test-Path -LiteralPath (Join-Path $previous 'VIvian-UnifiedInstall.marker'))) {
    throw '请提供现有版本及合流安装候选的完整程序目录。'
}
$root = [IO.Directory]::CreateTempSubdirectory('vivian-smoke-cutover-').FullName
$previousSmokeRoot = $env:VIVIAN_SMOKE_DATA
$install = Join-Path $root 'VIvian'
$staged = Join-Path $root 'candidate'
$rollback = Join-Path $root 'rollback'
$shortcutPath = Join-Path $root '启动 furina.lnk'
$entry = Join-Path $install 'VIvian.exe'

function Move-TestDirectory([string]$From, [string]$To) {
    $fromPath = (Resolve-Path -LiteralPath $From).Path
    $toPath = [IO.Path]::GetFullPath($To)
    if (([IO.Path]::GetDirectoryName($fromPath) -ne $root) -or ([IO.Path]::GetDirectoryName($toPath) -ne $root) -or
        ((Get-Item -LiteralPath $fromPath).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Test-Path -LiteralPath $toPath)) {
        throw '演练只允许在临时目录内移动未占用的子目录。'
    }
    [IO.Directory]::Move($fromPath, $toPath)
}

function Install-TestCandidate([string]$Package) {
    Move-TestDirectory $install $rollback
    try { Move-TestDirectory $Package $install }
    catch {
        Move-TestDirectory $rollback $install
        throw
    }
}

function Invoke-TestEntry([string]$Mode, [string]$Profile, [int]$ExpectedExit, [switch]$EnvironmentOnly) {
    $link = $shell.CreateShortcut($shortcutPath)
    if ($link.TargetPath -ne $entry) { throw '演练快捷方式目标发生变化。' }
    $startupMarker = Join-Path $Profile 'startup.ok'
    $previousMarker = if (Test-Path -LiteralPath $startupMarker) { Get-Content -LiteralPath $startupMarker -Raw }
    $env:VIVIAN_SMOKE_DATA = $Profile
    # The original M5 apphost elevates before it can read the rehearsal environment.
    $managedEntry = [IO.Path]::ChangeExtension($link.TargetPath, '.dll')
    $arguments = @(('"' + $managedEntry + '"'), $Mode, '--smoke-test')
    if (!$EnvironmentOnly) { $arguments += @('--smoke-data', ('"' + $Profile + '"')) }
    $proc = Start-Process -FilePath $dotnetHost -WorkingDirectory $install -WindowStyle Hidden -PassThru -ArgumentList $arguments
    if (!$proc.WaitForExit(30000)) {
        $proc.Kill()
        throw "演练入口超时。阶段 $Mode"
    }
    if ($proc.ExitCode -ne $ExpectedExit) {
        $report = Join-Path $install 'VIvian-启动失败报告.txt'
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Tail 14 | Write-Host }
        throw "演练入口退出码 $($proc.ExitCode)，预期 $ExpectedExit。阶段 $Mode"
    }
    if ($EnvironmentOnly) {
        $report = Join-Path $install 'VIvian-启动失败报告.txt'
        if (!(Select-String -LiteralPath $report -Pattern 'Invalid isolated smoke directory' -SimpleMatch -Quiet)) {
            throw '不安全的环境变量路径未被临时目录检查阻断。'
        }
    }
    if ($Mode -eq '--startup-check' -and $ExpectedExit -eq 0) {
        if (!(Test-Path -LiteralPath $startupMarker -PathType Leaf) -or
            (Get-Content -LiteralPath $startupMarker -Raw) -eq $previousMarker) {
            throw '演练入口未生成新的启动证据。'
        }
    }
}

try {
    Copy-Item -LiteralPath $previous -Destination $install -Recurse
    Copy-Item -LiteralPath $candidate -Destination $staged -Recurse
    $oldHash = (Get-FileHash -LiteralPath $entry).Hash
    $newHash = (Get-FileHash -LiteralPath (Join-Path $staged 'VIvian.exe')).Hash
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcutPath)
    $link.TargetPath = $entry
    $link.WorkingDirectory = $install
    $link.Save()

    $oldProfile = (New-Item -ItemType Directory -Path (Join-Path $root 'vivian-smoke-previous')).FullName
    Invoke-TestEntry '--startup-check' $oldProfile 0
    $failed = $false
    try { Install-TestCandidate (Join-Path $root 'intentionally-missing') }
    catch { $failed = $true }
    if (!$failed -or (Get-FileHash -LiteralPath $entry).Hash -ne $oldHash) { throw '失败替换未恢复旧程序。' }
    Invoke-TestEntry '--startup-check' $oldProfile 0

    Install-TestCandidate $staged
    if ((Get-FileHash -LiteralPath $entry).Hash -ne $newHash) { throw '入口未指向候选程序。' }
    $empty = (New-Item -ItemType Directory -Path (Join-Path $root 'vivian-smoke-empty')).FullName
    Invoke-TestEntry '--startup-check' $empty 1
    if (Test-Path -LiteralPath (Join-Path $empty 'vivian.db')) { throw '缺库检查误建了数据库。' }
    $fixture = (New-Item -ItemType Directory -Path (Join-Path $root 'vivian-smoke-fixture')).FullName
    Invoke-TestEntry '--prepare-unified-profile' $fixture 0
    $prepared = Join-Path $fixture 'vivian-smoke-prepared'
    Invoke-TestEntry '--startup-check' $prepared 0
    $nonSmokeProfile = (New-Item -ItemType Directory -Path (Join-Path $root 'not-a-smoke-profile')).FullName
    $nonSmokeDatabase = Join-Path $nonSmokeProfile 'vivian.db'
    Copy-Item -LiteralPath (Join-Path $fixture 'v6.db') -Destination $nonSmokeDatabase
    $nonSmokeHash = (Get-FileHash -LiteralPath $nonSmokeDatabase).Hash
    Invoke-TestEntry '--smoke-test' $nonSmokeProfile 1 -EnvironmentOnly
    if ((Get-FileHash -LiteralPath $nonSmokeDatabase).Hash -ne $nonSmokeHash) {
        throw '不安全的环境变量路径更改了原库。'
    }

    Move-TestDirectory $install $staged
    Move-TestDirectory $rollback $install
    if ((Get-FileHash -LiteralPath $entry).Hash -ne $oldHash) { throw '回滚后程序哈希不符。' }
    Invoke-TestEntry '--startup-check' $oldProfile 0
    Write-Output 'One shortcut target retained across replacement and rollback. Both installed assemblies passed startup checks. Missing prepared DB and unsafe environment override were rejected. Failed replacement and final rollback restored the original executable hash and fresh startup evidence.'
}
finally {
    $env:VIVIAN_SMOKE_DATA = $previousSmokeRoot
    $resolved = (Resolve-Path -LiteralPath $root).Path
    $temp = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $temp -and
        [IO.Path]::GetFileName($resolved).StartsWith('vivian-smoke-cutover-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
