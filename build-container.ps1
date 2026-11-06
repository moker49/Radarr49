[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9./:_-]*$')]
    [string]$ImageTag = 'radarr-composite:local',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '_artifacts/container'),
    [string]$DockerContext
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI is required to build/export the image. Install Docker Desktop for building, or configure Docker CLI access to a remote Linux engine. This script does not run Radarr.'
}

$dockerOptions = @()
if ($DockerContext) {
    $dockerOptions = @('--context', $DockerContext)
}

function Invoke-Docker {
    param([string[]]$DockerArguments)
    & docker @dockerOptions @DockerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker failed ($LASTEXITCODE): $($DockerArguments -join ' ')"
    }
}

$engineOs = Invoke-Docker -DockerArguments @('info', '--format', '{{.OSType}}')
if (($engineOs -join '').Trim() -ne 'linux') {
    throw 'The Docker builder must use Linux containers.'
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive = Join-Path $output 'radarr-composite-linux-amd64.tar'

Write-Host 'Building the Linux amd64 image. Radarr will not be launched on this machine.'
Invoke-Docker -DockerArguments @('build', '--platform', 'linux/amd64', '--tag', $ImageTag, $PSScriptRoot)
Invoke-Docker -DockerArguments @('image', 'save', '--output', $archive, $ImageTag)

$sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $output 'radarr-composite-linux-amd64.tar.sha256') -Value "$sha256  radarr-composite-linux-amd64.tar" -Encoding Ascii
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compose.deploy.yaml') -Destination (Join-Path $output 'compose.yaml') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CONTAINER_DEPLOY.md') -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'scripts/run-container.bat') -Destination (Join-Path $output 'run-container.bat') -Force
$envPath = Join-Path $output '.env'
if (-not (Test-Path -LiteralPath $envPath)) {
    $defaults = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'container.defaults.env') -Raw
    Set-Content -LiteralPath $envPath -Value ("RADARR_IMAGE=$ImageTag`n" + $defaults) -Encoding Ascii
}

Write-Host "Export complete: $output"
Write-Host 'Copy that directory to the destination Docker host and follow README.md.'
