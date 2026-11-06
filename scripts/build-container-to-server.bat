@echo off
setlocal
cd /d "%~dp0.."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\build-container-to-server.ps1" %*
exit /b %errorlevel%
