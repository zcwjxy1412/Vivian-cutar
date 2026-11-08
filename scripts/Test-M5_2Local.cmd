@echo off
chcp 65001 >nul
rem Fixed local entry: full regression (3 build flavours) + M5.2 package for the current commit.
if exist "%~dp0..\..\.dotnet\dotnet.exe" set "PATH=%~dp0..\..\.dotnet;%PATH%"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-M5_2Local.ps1"
echo.
echo exit code %ERRORLEVEL%
