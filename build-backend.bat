@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_ROOT=%LOCALAPPDATA%\RadarrDev\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
if not exist "%DOTNET_ROOT%\dotnet.exe" (
    echo .NET SDK not found at %DOTNET_ROOT%.
    goto failed
)

echo Building Radarr backend...
dotnet build src\NzbDrone.Console\Radarr.Console.csproj -c Debug -p:Platform=Windows -p:RuntimeIdentifiers=win-x64 "-p:OutDir=%CD%\_output\dev/" "-p:SolutionDir=%CD%\src/"
if errorlevel 1 goto failed
dotnet build src\NzbDrone.Windows\Radarr.Windows.csproj -c Debug -p:Platform=Windows -p:RuntimeIdentifiers=win-x64 "-p:OutDir=%CD%\_output\dev/" "-p:SolutionDir=%CD%\src/"
if errorlevel 1 goto failed
echo Backend build succeeded.
pause
exit /b 0

:failed
echo Backend build failed.
pause
exit /b 1
