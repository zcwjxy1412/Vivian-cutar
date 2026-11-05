$ErrorActionPreference = 'Stop'
$probe = Join-Path $PSScriptRoot 'VIvian.WindowsBoundaryProbe.exe'
if (-not (Test-Path -LiteralPath $probe)) { throw '请先完整解压测试工具 ZIP。' }
& $probe
$code = $LASTEXITCODE
Write-Host '请将输出末尾显示的报告目录中的「报告.json」和「摘要.txt」交回 Codex。'
Write-Host '输入回车关闭窗口。'
[void][Console]::ReadLine()
exit $code
