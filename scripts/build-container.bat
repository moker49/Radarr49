@echo off
setlocal
cd /d "%~dp0.."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-container-build.ps1" %*
set "BUILD_EXIT_CODE=%errorlevel%"
echo.
pause
exit /b %BUILD_EXIT_CODE%
