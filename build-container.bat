@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-container.ps1" %*
exit /b %errorlevel%
