@echo off
chcp 65001 >nul
"%~dp0VIvian.WindowsBoundaryProbe.exe"
set "probe_exit=%ERRORLEVEL%"
echo.
echo 请将报告目录中的 报告.json 和 摘要.txt 交回 Codex。
pause
exit /b %probe_exit%
