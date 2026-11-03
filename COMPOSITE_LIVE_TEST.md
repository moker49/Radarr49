# First composite live test

This milestone enables **new movies in a dedicated test root** through the existing add
API. Run it after rebuilding/restarting the dev app with these sources. Automated tests
build `_output/net8.0`; they do not update or restart the running `_output/dev` app.
Stop the dev app before using the existing backend build/start workflow. Keep a restorable
copy of its database/configuration for this development test; migration 251 adds only a
nullable activation-root column and does not convert existing movies.

## Prepare

1. Create a separate empty directory, for example `C:\RadarrCompositeTest`, and add it in
   Settings > Media Management > Root Folders. Use a real path appropriate to this machine.
2. Configure two ordinary profiles, SDR and HDR. Each owns its quality, Custom Format and
   bitrate/size settings. Ensure their acceptance rules distinguish SDR from HDR. The labels
   below name versions and files; they do not themselves filter dynamic range.
3. Create a third unused ordinary profile named `SDR+HDR`. Configure/name it before attaching
   definitions; legacy profile edits are blocked afterward. Its child definitions govern
   release decisions. Record all three IDs from GET `/api/v3/qualityprofile`.
4. Configure working indexers/download clients as usual. Choose a movie not already in this
   instance. Start with concurrency 1.

## Configure and activate

The examples use PowerShell. Set `RADARR_API_KEY` in the shell to this instance's API key;
none of these examples prints the key. Replace all example IDs, paths and TMDB IDs.

```powershell
$api = 'http://localhost:7878/api/v3'
$headers = @{ 'X-Api-Key' = $env:RADARR_API_KEY }
$parentId = 10
$sdrId = 1
$hdrId = 2
$testRoot = 'C:\RadarrCompositeTest'

$body = @{
    maximumConcurrentDownloads = 1
    versions = @(
        @{ id = 0; qualityProfileId = $sdrId; label = 'SDR'; priority = 0; isPrimary = $true }
        @{ id = 0; qualityProfileId = $hdrId; label = 'HDR'; priority = 1; isPrimary = $false }
    )
} | ConvertTo-Json -Depth 20
Invoke-RestMethod "$api/qualityprofile/$parentId/composite" -Method Put `
    -Headers $headers -ContentType 'application/json' -Body $body

$body = @{ rootFolderPath = $testRoot } | ConvertTo-Json
Invoke-RestMethod "$api/qualityprofile/$parentId/composite/activate" -Method Post `
    -Headers $headers -ContentType 'application/json' -Body $body
```

Expect two persistent definition IDs, `isDraft: false` and the test root. A missing,
unconfigured root, invalid definitions or an assigned parent is rejected. The root must
contain no existing managed movies at activation. To revise an unused configuration,
POST `.../deactivate`, GET the definitions and retain their returned IDs on PUT. After
adding a movie, deactivation and definition changes are blocked pending a future explicit
conversion/removal workflow. Legacy DELETE intentionally cannot undo this test movie.

## Add one movie through the ordinary API

First add without requesting an immediate search so the assignments can be inspected.

```powershell
$tmdbId = 1271 # Example only: choose a movie absent from this instance.
$movie = Invoke-RestMethod "$api/movie/lookup/tmdb?tmdbId=$tmdbId" -Headers $headers
$body = @{
    tmdbId = $movie.tmdbId
    title = $movie.title
    qualityProfileId = $parentId
    rootFolderPath = $testRoot
    monitored = $true
    minimumAvailability = 'released'
    addOptions = @{ searchForMovie = $false }
} | ConvertTo-Json -Depth 20
$added = Invoke-RestMethod "$api/movie" -Method Post -Headers $headers `
    -ContentType 'application/json' -Body $body
$movieId = $added.id
Invoke-RestMethod "$api/movie/$movieId/versions" -Headers $headers
```

Expect one movie with the parent `qualityProfileId` and two explicit monitored versions,
SDR first, each with its own ordinary profile and `hasFile: false`. There must be no new
files adopted from an existing directory. Additions outside the activated root, or into a
movie directory containing any files recursively, are rejected. Ordinary profile additions
retain their previous behavior. No special fields are required on the legacy add request.

nzb360/Seer should be able to list/select the parent and add a different new movie using
that same root. Capture their actual requests when testing those clients; this has not yet
been verified against either app. Their edits/deletes of managed movies remain blocked.

## Exercise acquisition and recovery

```powershell
$body = @{ name = 'MoviesSearch'; movieIds = @($movieId) } | ConvertTo-Json
Invoke-RestMethod "$api/command" -Method Post -Headers $headers `
    -ContentType 'application/json' -Body $body
```

1. Inspect candidates/queue: prefer SDR when both have eligible candidates. If SDR has no
   eligible result, HDR can proceed. Concurrency 1 permits one active managed download for
   this movie. The versions API, queue/history version IDs and download reservation API
   provide the authoritative version state; the old movie page shows the primary summary.
2. After the first import completes, run another search if the second version has not yet
   been acquired by RSS. A single search with concurrency 1 does not promise an automatic
   follow-up search after completion. Missing copies rank ahead of upgrades.
3. Verify both physical files share the movie directory and have its exact basename plus
   ` - SDR.mkv` and ` - HDR.mkv` (extension follows the source). Check each file's actual
   dynamic range and bitrate, plus its matching version/profile/history identity.
4. Test one eligible SDR upgrade. HDR and its sidecars must remain intact. Compare version
   IDs, file IDs and physical files before/after; version IDs remain stable.
5. Restart normally, then refresh/rescan. Both assignments must survive; one copy must not
   replace or disappear behind the primary. Inspect version status, reservation and import
   journals if a download/import remains blocked. The implementation document describes
   recovery endpoints; retries with physical overrides require inspection/checkpoints.
6. Scan the test folder in Emby. Verify one movie exposes both versions and each plays.
   Emby recognition and real process interruption during file transfers are live checks,
   not claims established by the automated tests.

Managed folder moves, existing-library conversion, whole-movie removal and a full composite
editor are still pending. Keep this first test in the dedicated root; do not use the old UI
or clients to move/delete managed movies. Legacy operations return HTTP 409 rather than
partially managing the two copies.
