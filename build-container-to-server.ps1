[CmdletBinding()]
param(
    [string]$Destination = '\\server\Server\docker\radarr49',
    [string]$ImageTag = 'radarr-composite:local',
    [string]$DockerContext
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '_artifacts/container'))
$target = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
if ($target -eq $source -or $target.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The transfer destination must be outside the local export directory.'
}

# Check share access before spending time building the image.
New-Item -ItemType Directory -Path $target -Force | Out-Null
$buildOptions = @{ ImageTag = $ImageTag; OutputDirectory = $source }
if ($DockerContext) { $buildOptions.DockerContext = $DockerContext }
& (Join-Path $PSScriptRoot 'build-container.ps1') @buildOptions

$names = @('radarr-composite-linux-amd64.tar', 'radarr-composite-linux-amd64.tar.sha256', 'compose.yaml', 'README.md', '.env')
$transfers = @()
$suffix = '.uploading-' + [guid]::NewGuid().ToString('N')
try {
    foreach ($name in $names) {
        $localFile = Join-Path $source $name
        $remoteFile = Join-Path $target $name
        if ($name -eq '.env' -and (Test-Path -LiteralPath $remoteFile)) {
            Write-Host 'Preserving the existing server .env.'
            continue
        }

        if (-not (Test-Path -LiteralPath $localFile -PathType Leaf)) { throw "Build output is missing: $name" }
        $temporaryFile = $remoteFile + $suffix
        $transfer = [pscustomobject]@{ Source = $localFile; Temporary = $temporaryFile; Destination = $remoteFile; IsEnv = $name -eq '.env' }
        $transfers += $transfer
        Write-Host "Transferring $name..."
        Copy-Item -LiteralPath $localFile -Destination $temporaryFile -Force
        if ((Get-FileHash -LiteralPath $localFile).Hash -ne (Get-FileHash -LiteralPath $temporaryFile).Hash) {
            throw "Transfer verification failed: $name. Local outputs have been kept."
        }
    }

    # Publish only after every staged file has passed its checksum comparison.
    foreach ($transfer in $transfers) {
        Move-Item -LiteralPath $transfer.Temporary -Destination $transfer.Destination -Force:(!$transfer.IsEnv)
    }

    # All source paths are fixed filenames within this workspace's export directory.
    # Retain local outputs on any copy/verification/publish failure.
    foreach ($transfer in $transfers) {
        Remove-Item -LiteralPath $transfer.Source
    }
} finally {
    foreach ($transfer in $transfers) {
        if (Test-Path -LiteralPath $transfer.Temporary) {
            Remove-Item -LiteralPath $transfer.Temporary -Force
        }
    }
}

Write-Host "Build complete. Deployment files moved to $target"
Write-Host 'No container was started or restarted. Load the new image and recreate the container on the server when ready.'
