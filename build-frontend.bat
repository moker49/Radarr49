@echo off
setlocal
cd /d "%~dp0"
if not exist "%NVM_HOME%\v24.14.0\node.exe" (
    echo Node 24 not found at %NVM_HOME%\v24.14.0.
    goto failed
)
set "PATH=%NVM_HOME%\v24.14.0;%PATH%"
echo Installing frontend dependencies...
call corepack yarn install --frozen-lockfile
if errorlevel 1 goto failed
echo Building frontend...
call corepack yarn build
if errorlevel 1 goto failed
echo Frontend build succeeded.
pause
exit /b 0

:failed
echo Frontend build failed.
pause
exit /b 1
