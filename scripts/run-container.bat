@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 (
    echo Cannot access the deployment directory.
    pause
    exit /b 1
)

where docker >nul 2>&1
if errorlevel 1 (
    echo Docker CLI was not found. Start Docker Desktop with Linux containers enabled.
    goto failed
)
for %%F in ("radarr-composite-linux-amd64.tar" "radarr-composite-linux-amd64.tar.sha256" "compose.yaml" ".env") do (
    if not exist "%%~F" (
        echo Missing deployment file: %%~F
        goto failed
    )
)

echo Checking the image archive...
powershell.exe -NoProfile -Command "$archive='radarr-composite-linux-amd64.tar'; $expected=(Get-Content -LiteralPath ($archive+'.sha256')).Split(' ')[0]; if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { Write-Error 'Image archive checksum mismatch'; exit 1 }"
if errorlevel 1 goto failed
docker compose config --quiet
if errorlevel 1 goto failed

echo Loading the image...
docker image load --input "radarr-composite-linux-amd64.tar"
if errorlevel 1 goto failed
echo Creating and starting the container...
docker compose up -d --no-build --force-recreate
if errorlevel 1 goto failed
docker compose ps
if errorlevel 1 goto failed

echo Container started. Use docker compose logs radarr if it does not become healthy.
popd
pause
exit /b 0

:failed
echo Deployment failed. Review the messages above.
popd
pause
exit /b 1
