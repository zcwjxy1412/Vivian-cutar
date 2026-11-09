# Run in a regular, non-elevated PowerShell window before the first elevated M6 desktop run.
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在普通权限的 PowerShell 窗口初始化 M6 桌面锁。'
}

$oldLock = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VIvian-M6/desktop-operation.lock'
$old = Get-Item -LiteralPath $oldLock -Force -ErrorAction SilentlyContinue
if ($old -and $old.Length -gt 0) {
    throw '旧位置仍有待清理记录，请先用原档案完成交接。'
}
$profile = [Environment]::GetFolderPath('UserProfile')
if (![IO.Path]::IsPathFullyQualified($profile)) { throw '无法确认 Windows 用户目录。' }
$directory = Join-Path $profile '.vivian-m6'
$lockPath = Join-Path $directory 'desktop-operation.lock'
for ($current = $lockPath; $current; $current = Split-Path -Parent $current) {
    $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '桌面锁路径包含链接，请恢复普通本地目录。'
    }
}
[IO.Directory]::CreateDirectory($directory) | Out-Null
$lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$lock.Dispose()
Write-Output "M6 桌面锁已初始化：$lockPath"
