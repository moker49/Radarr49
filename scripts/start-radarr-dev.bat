@echo off
setlocal
cd /d "%~dp0.."

rem Use the development tools installed for this checkout without changing system defaults.
set "PATH=%NVM_HOME%\v24.14.0;%LOCALAPPDATA%\RadarrDev\dotnet;%PATH%"
set "DOTNET_ROOT=%LOCALAPPDATA%\RadarrDev\dotnet"
if not exist "%NVM_HOME%\v24.14.0\node.exe" goto missing_tools
if not exist "%LOCALAPPDATA%\RadarrDev\dotnet\dotnet.exe" goto missing_tools

if not exist "_output\dev\Radarr.Console.exe" (
    echo Backend build not found. Run scripts\build-backend.bat first.
    goto failed
)
if not exist "_output\dev\Radarr.Windows.dll" (
    echo Windows backend components not found. Run scripts\build-backend.bat first.
    goto failed
)
if not exist "_output\UI\index.html" (
    echo Frontend build not found. Run scripts\build-frontend.bat first.
    goto failed
)
if not exist "node_modules\.bin\webpack.cmd" (
    echo Frontend dependencies not found. Run scripts\build-frontend.bat first.
    goto failed
)
powershell -NoProfile -Command "$c = New-Object System.Net.Sockets.TcpClient; try { $c.Connect('localhost', 7878); exit 1 } catch { exit 0 } finally { $c.Dispose() }"
if errorlevel 1 (
    echo Port 7878 is already in use. Stop the other application before launching Radarr.
    goto failed
)

echo Starting frontend watcher and Radarr...
start "Radarr frontend watcher" cmd /k "corepack yarn start"
start "Radarr development" /D "%CD%\_output\dev" "%CD%\_output\dev\Radarr.Console.exe" "/data=%CD%\_devdata" /nobrowser
echo Waiting for Radarr on http://localhost:7878 ...
powershell -NoProfile -Command "$ready = $false; for ($i = 0; $i -lt 60; $i++) { try { $r = Invoke-WebRequest 'http://localhost:7878' -UseBasicParsing -TimeoutSec 2; if ($r.StatusCode -eq 200) { $ready = $true; break } } catch {}; Start-Sleep -Seconds 1 }; if ($ready) { Start-Process 'http://localhost:7878' } else { Write-Host 'Radarr did not respond. Check the Radarr development window.'; exit 1 }"
if errorlevel 1 goto failed
echo Radarr is ready. Close the Radarr and frontend watcher windows to stop it.
exit /b 0

:missing_tools
echo Required development tools were not found. Check the Node 24 and RadarrDev .NET SDK installations.
goto failed

:failed
echo Startup failed. Review the messages above.
pause
exit /b 1
