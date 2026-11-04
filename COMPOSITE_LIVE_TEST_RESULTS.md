# Composite profile live test — 2026-11-03

The first real download/import lifecycle passed using Bao (2018), TMDB 514754,
in the existing sandbox instance and `C:\Media\Movies`. No separate media root was used.
Radarr remains running with both copies available.

## Final state

Movie ID 2 uses parent profile 7 (`SDR+HDR`). Definitions 1 and 2 reference ordinary
profiles 4 (`SDR`) and 5 (`HDR`), respectively. SDR is primary and preferred first;
maximum concurrent downloads is 1. These settings survived the restart.

| Version | Version ID | Final file ID | Filename | Bytes | Actual media |
| --- | --- | --- | --- | --- | --- |
| SDR | 1 | 4 | Bao (2018) - SDR.mkv | 368,636,136 | 1080p, 8-bit, SDR |
| HDR | 2 | 3 | Bao (2018) - HDR.mkv | 978,257,548 | 2160p, 10-bit, Dolby Vision |

Files are in `C:\Media\Movies\Bao (2018)`. The legacy movie resource retains parent
profile ID 7 and the primary SDR file ID 4. Its total size is 1,346,893,684 bytes.
The versions API exposes both independent files and their child profiles.

## Checks performed

- Configured and activated the unused parent through the composite API, using the
  existing root after the user removed the prior ordinary Bao entry.
- Added Bao through ordinary POST `/api/v3/movie`, with only the parent profile ID.
  Both empty, monitored version assignments were immediately available.
- Searched the real indexers. Child Custom Format rules accepted SDR and HDR releases
  independently. Automatic acquisition attempted SDR first.
- Acquired/imported SDR, then searched again. Missing HDR was selected ahead of another
  SDR upgrade. HDR remained pending while the SDR reservation occupied the single slot.
- Verified the first SDR file's SHA-256 did not change during HDR acquisition/import.
- Exercised explicit version-targeted SDR replacement using the nikt0 release. SDR file
  ID changed from 2 to 4, while version ID 1 remained stable. HDR retained file ID 3 and
  the same SHA-256. The first SDR import, HDR import and SDR replacement journals all
  reached Completed. Replacement audit events belong to SDR version 1.
- Gracefully shut down and launched a fresh backend process against the same dev data,
  then ran RescanMovie for Bao. Both versions retained their file IDs, exact bytes and
  SHA-256 hashes. The ordinary movie-list/detail and file APIs continued to work.
- Wanted/missing returned zero movies; wanted/cutoff returned Bao once, consistent with
  the configured child cutoffs. Neither version disappeared behind the primary summary.
- Legacy movie DELETE, movie PUT, file DELETE and parent-profile PUT returned HTTP 409.
  Editing active composite definitions also returned 409. Rejected mutations preserved
  monitoring, profile selection and file ownership.
- Used two temporary ordinary profile clones to round-trip different attached size
  settings for the same quality. Updating one did not change the other; an update omitting
  sizeDefinitions preserved the attached values. Removed both clones afterward and
  verified the real SDR/HDR profiles were unchanged. Their size settings remain as the
  user configured them (currently inheriting global defaults).
- Final queue count: zero. Active/uncertain reservations: zero. Completed import
  journals: three. No movie/file records were changed directly in the database.

## Download-client observations

SABnzbd rejected the DDR and FLAME NZBs as duplicates of earlier downloads. Its response
produced a generic client error, leaving the new reservation Uncertain. Before clearing
each one through confirm-not-submitted, the test checked SAB's queue/history and its
explicit duplicate-rejection log entry. Alternative releases were then submitted.
This demonstrates the conservative duplicate-submission protection and reconciliation
path, but the generic duplicate error remains a usability issue.

The first HDR release (FLUX) failed in SABnzbd as incomplete. Radarr recorded that failure
against HDR version 2, released the failed reservation, and acquired the MEHH release
against the same version. SDR stayed intact.

The SDR replacement was a manual, explicitly version-targeted selection. Automatic
same-quality/same-Custom-Format-score upgrades were rejected by existing upgrade rules;
this test does not establish automatic bitrate-only upgrades.

## Still unverified

Actual nzb360/Seer add requests and Emby grouping/playback have not been exercised.
Concurrency 2, HDR-first ordering, sidecar replacement under varied layouts, and actual
process interruption in the middle of a transfer remain additional live-test cases.
This run used a normal restart after imports completed, not an import crash simulation.
Existing-library conversion, managed folder moves, whole-movie removal and the full
composite editor remain pending implementation.

## Evidence

Sanitized API snapshots, history, final journals, reservations and hash comparisons are
under `_temp/composite-live-test/`. Relevant files include `versions-final.json`,
`files-final.json`, `history-final.json`, `import-journals-final.json`,
`reservations-final.json`, `restart-rescan-check.json`, `profile-size-api-check.json`
and `legacy-protections.json`. API keys were read from local configuration and were not
included in these snapshots or displayed in tool output.
