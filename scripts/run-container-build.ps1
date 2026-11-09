[CmdletBinding()]
param(
    [switch]$ToServer,
    [string]$ImageTag,
    [string]$DockerContext,
    [string]$Destination,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $workspace '_artifacts/build-logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory ("container-{0}-{1}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)
$options = @{}
foreach ($key in $PSBoundParameters.Keys) {
    if ($key -ne 'ToServer') { $options[$key] = $PSBoundParameters[$key] }
}
$scriptName = if ($ToServer) { 'build-container-to-server.ps1' } else { 'build-container.ps1' }
$buildExitCode = 0
Start-Transcript -LiteralPath $logPath | Out-Null
try {
    & (Join-Path $workspace $scriptName) @options | Out-Host
} catch {
    $buildExitCode = 1
    Write-Host "Build failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
} finally {
    Stop-Transcript | Out-Null
    Write-Host "Build log: $logPath"
}
exit $buildExitCode
