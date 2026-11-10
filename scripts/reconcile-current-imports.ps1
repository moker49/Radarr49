[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [Security.SecureString]$ApiKey,
    [switch]$ClearAbandonedInvite
)

$ErrorActionPreference = 'Stop'
if (-not $ApiKey) { $ApiKey = Read-Host 'Radarr API key' -AsSecureString }
$secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ApiKey)
try {
    $headers = @{ 'X-Api-Key' = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer) }
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer)
}
$root = $BaseUrl.TrimEnd('/') + '/api/v3'
function Invoke-Radarr {
    param([string]$Method, [string]$Path, $Body)
    $options = @{ Method = $Method; Uri = "$root/$Path"; Headers = $headers; TimeoutSec = 1800 }
    if ($null -ne $Body) {
        $options.ContentType = 'application/json'
        $options.Body = $Body | ConvertTo-Json -Depth 10
    }
    Invoke-RestMethod @options
}

$movies = @(Invoke-Radarr GET 'movie')
foreach ($movie in $movies | Where-Object { $_.title -in @('The Resident', 'The Invite') }) {
    Write-Host "`n$($movie.title):"
    @(Invoke-Radarr GET "movie/$($movie.id)/versionfileimports") |
        Where-Object { $_.isActive } |
        Select-Object id, state, sourcePath, lastRecoveryError | Format-List | Out-Host
}
if (-not $ClearAbandonedInvite) {
    Write-Host 'Read-only inspection complete. Use -ClearAbandonedInvite only after removing the abandoned PiRaTeS HDR job from SABnzbd.'
    return
}

$invite = @($movies | Where-Object { $_.tmdbId -eq 950028 -and $_.title -eq 'The Invite' })
if ($invite.Count -ne 1) { throw 'Could not uniquely identify the expected Invite movie. Nothing changed.' }
$movieId = $invite[0].id
$operation = Invoke-Radarr GET "movie/$movieId/versionfileimports/20"
if ($operation.sourcePath -notlike '*The.Invite.2026.2160p.HMAX.WEB-DL.DDP.5.1.DV.HDR.H.265-PiRaTeS*' -or
    $operation.downloadReservationId -ne 34 -or $null -ne $operation.oldMovieFileId -or $null -ne $operation.newMovieFileId) {
    throw 'Operation 20 does not match the abandoned, unregistered HDR import from the supplied backup. Nothing changed.'
}
if ($operation.isActive) {
    if ($operation.state.ToString().ToLowerInvariant() -notin @('backingup', '3') -or -not $operation.canCancel) {
        throw 'This import is not at the safe cancellation checkpoint. Inspect it before making changes.'
    }
    Invoke-Radarr POST "movie/$movieId/versionfileimports/20/cancel" @{
        checkpoint = $operation.checkpoint
        note = 'User abandoned the original HDR download during import; cancel the unpublished operation and retain its private staging files.'
    } | Out-Null
    Write-Host 'Cancelled the abandoned HDR operation. Staging and existing movie files were retained.'
}
$reservation = @(Invoke-Radarr GET "movie/$movieId/downloadreservations") | Where-Object { $_.id -eq 34 }
if (-not $reservation -or $reservation.movieVersionId -ne $operation.movieVersionId) {
    throw 'The expected download reservation could not be verified.'
}
if ($reservation.state.ToString().ToLowerInvariant() -notin @('failed', '4')) {
    Invoke-Radarr POST "movie/$movieId/downloadreservations/34/confirm-removed" @{
        downloadId = $reservation.downloadId
        note = 'User removed the abandoned PiRaTeS HDR job from SABnzbd; the unpublished import was safely cancelled.'
    } | Out-Null
}
Invoke-Radarr POST 'command' @{ name = 'MoviesSearch'; movieIds = @($movieId); movieVersionId = $operation.movieVersionId } | Out-Null
Write-Host 'Released the old HDR reservation and queued an HDR-only replacement search.'
