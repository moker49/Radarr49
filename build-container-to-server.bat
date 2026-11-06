@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-container-to-server.ps1" %*
exit /b %errorlevel%
