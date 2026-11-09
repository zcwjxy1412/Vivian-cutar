# Run in a regular, non-elevated PowerShell window before the first elevated M6 desktop run.
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在普通权限的 PowerShell 窗口初始化 M6 桌面锁。'
}

$directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VIvian-M6'
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
