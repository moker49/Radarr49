# Composite profile live test — 2026-11-03

Concurrency policy was subsequently changed to one independent active download per
movie/version; SDR and HDR may now download simultaneously. The earlier sequential
observations below describe the policy in effect during those tests.

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

## Follow-up: movie lookup after the agent-initiated restart

The backend initially launched by the test inherited the tool sandbox's outbound network
restrictions. Later new-movie searches returned HTTP 503 with Windows socket error 10013
for `api.radarr.video:443`. This was an execution-environment issue introduced by that
restart, rather than a composite-profile or client/API compatibility failure.

The dev backend was gracefully stopped and relaunched outside the tool sandbox using an
approved elevated tool execution. No source, firewall, API key or media changes were
needed. Lookup requests for `fast five` and `bao` then returned HTTP 200 with six and
twenty results, respectively. Bao's SDR/HDR assignments remained file IDs 4 and 3. Future
agent-initiated backend launches must run outside the restricted tool process if the
instance needs outbound metadata/indexer access; launching via the user's desktop script
also avoids this inherited restriction.

The earlier network failures also left both indexers in a persisted failure cooldown.
Fast Five's subsequent release searches therefore reported zero active indexers even
after metadata lookup recovered. POST `/api/v3/indexer/testall` successfully tested both
providers and cleared their failure state through the normal status service. Interactive
release search for Fast Five then returned 170 results, including 94 approved decisions,
across version IDs 3 and 4. The sanitized search summary is saved in
`_temp/composite-live-test/fast-five-release-search-check.json`.

## Follow-up: cancelling a managed download

Fast Five's cancelled SDR job retained Submitted reservation 7 after activity removal.
The next search displayed SDR/HDR candidates with status versionDownloadLimit, but those
rows had no download/client IDs and were not SABnzbd jobs. The old reservation occupied
the movie's single allowed download slot.

The backend now records successful managed client removals against the exact bound
reservation for both single and bulk Activity deletion. It checks file-operation readiness
before touching the client and retains the slot on removal failures. Imported reservation
ownership is preserved. An additive confirm-removed recovery endpoint handles previously
orphaned bound attempts after explicit verification and fresh client inspection; it does
not automatically treat missing cached downloads as cancelled.

Validation: 862 download/queue tests passed (two existing skips; the previously reproduced
broken-report baseline failure excluded), 64 v3 API tests and 16 container checks passed
(one existing skip). Regression coverage includes another version retaining its reservation,
slot reuse after cancellation, client errors, exact client/download identity, active imports,
imported ownership, route ownership and single/bulk queue dispatch. Logs are
`_temp/reservation-removal-*-tests.log` and `_temp/reservation-removal-regression.log`.

The fix was built and deployed to the running dev backend, launched outside the tool's
network sandbox. SAB's queue/history confirmed the original job was absent; its reservation
was reconciled through the new API and two stale waiting entries were removed. A fresh
SDR test job (reservation 8) was submitted. Confirm-removed returned HTTP 409 while the
job was present, then ordinary Activity deletion removed it and immediately released its
reservation. No manual database edits were used.

Final checks: Activity empty, no active Fast Five reservations, both Fast Five versions
still have no files, movie lookup HTTP 200 and interactive release search returns 170
results. Evidence is under `_temp/reservation-cancellation-test/`. At the time of this cancellation test, the profile allowed
one active version download at a time, so the second activity candidate could be waiting
rather than an additional active SABnzbd job. The subsequent per-version concurrency change
removes that aggregate cap.

## Per-version concurrency update

The aggregate movie cap is now retired. Each movie/version owns one active slot, so SDR
and HDR can be submitted concurrently; a second request for the same version remains
blocked. Preference order still submits SDR first when both are eligible, and missing
versions rank ahead of upgrades. Uncertainty blocks its own version while duplicate
release identity and ambiguous unscoped client jobs remain conservatively protected.
Existing stored limits of one do not require a migration or profile reconfiguration.

The composite API reports maximumConcurrentDownloadsPerVersion 1 and an effective
maximumConcurrentDownloads of 2 for the two-child composite. The old aggregate input
is accepted for compatibility but no longer controls dispatch. Validation passed 888
core download/queue/profile tests (two existing skips and the same broken-report exclusion),
65 v3 API tests and 16 container checks (one existing skip). Tests include simultaneous
reservations for distinct versions, duplicate concurrent requests for the same version,
old stored limits of one, uncertain submissions and same-batch dispatch/preference order.

The tested backend was deployed and restarted outside the restricted tool process.
Fast Five already had SDR reservation 9 in progress and HDR waiting. A new scoped movie
search submitted the existing HDR candidate as reservation 10 while SDR remained active.
Both bound IDs were verified directly in SABnzbd's queue, one for each version. Client
transfer order remains subject to SABnzbd's own queue/priority behavior. Both user downloads
were left in place. Live evidence is in `_temp/per-version-concurrency-live-profile.json`,
`_temp/per-version-concurrency-live-reservations.json` and
`_temp/per-version-concurrency-live-client.json`; test logs use the same prefix.

## Fast Five large-file import observation

The SDR-designated import (operation 5, reservation 9) completed normally after about
8 minutes 49 seconds. Its 36,471,548,990-byte file had already been installed and registered
as file ID 6 while Activity still displayed Importing. Observation showed journal progress
through CleaningOldExtras, RemovingSource and EventsPending to Completed, with no recovery
error. The worker was consuming CPU during verification. Cleanup and completion perform
multiple full-file SHA-256 passes, including checks at cleanup phase boundaries; these are
expensive for a large movie and currently have no useful Activity progress indicator.
No resume override, restart or file mutation was needed. Activity subsequently became empty.
HDR retained file ID 5 and its expected size. Evidence is in
`_temp/fast-five-sdr-import/completed-journal.json` and `media-info.json`.

This inspection also exposed an unresolved content-classification issue: both Fast Five
files report HDR10 in their parsed media info. The SDR-designated FLAME release lacked an
HDR marker in its release title, and the configured SDR Custom Format merely negates an
HDR/DV title regex. It therefore scored as SDR despite actually containing HDR10. The two
managed file assignments and import lifecycle are working, but these title-only profile
rules do not guarantee an actual SDR/HDR pair. Reliable enforcement needs validation of
the downloaded file's dynamic range; this has not yet been implemented. The files and
profile rules were left as observed during this diagnostic run.

## Import performance and single-file deletion fixes (2026-11-04)

The large-file import delay was repeated full-video SHA-256 verification, rather than
a stuck import. Empty sidecar and cleanup phases now advance without rereading the
video; checks remain before physical cleanup and final completion. SHA-256 uses a
pooled 1 MiB buffer, and debug logs report bytes and elapsed hashing time. A read-only
benchmark on the current 9,342,901,748-byte Fast Five SDR file averaged 8.235 seconds
with the previous stream hashing and 4.401 seconds with the new buffer, producing
identical digests. This measures hashing only; a complete new import has not yet been
timed. Evidence: `_temp/import-hash-benchmark.json`.

The movie file dialog previously sent managed file deletions to the intentionally
blocked legacy endpoint. File resources now expose their persisted `movieVersionId`,
and the dialog routes an individual managed file deletion to its exact version/file
endpoint. Ordinary files retain their existing route. Deletion errors are displayed
in the dialog. When no explicit monitoring override is supplied, the server reads
the saved Unmonitor Deleted Movies setting; this does not depend on whether the
browser has visited Media Management settings.

Live sandbox test: Fast Five SDR version 3/file 8 (9,342,901,748 bytes) was deleted
with HTTP 204 and a Completed deletion journal. SDR remained monitored. HDR version
4/file 5 retained its path, size and last-write timestamp. A legacy delete of the
same SDR file returned HTTP 409 before the version-specific deletion. The movie
lookup endpoint returned HTTP 200 after deployment. Evidence:
`_temp/import-delete-live-result.json` and the before snapshots with the same prefix.

Validation: 1,370 broader core regression tests passed (five existing skips),
including the import-specific regression coverage; the previously reproduced
unrelated broken-report case remains excluded. All 68 v3 API tests passed, as did
16 container checks (one existing skip), frontend lint and the full frontend build.
An isolated check of the actual frontend action verifies SDR, HDR and ordinary file
request routing. Browser click verification could not be completed: the desktop tool
stopped because it could not establish the active browser URL. No further UI
automation was attempted; the live deletion was verified through the authorized API.

## SABnzbd duplicate rejection recovery (2026-11-04)

An automatic Fast Five search at 10:00 EST selected the previously downloaded PRoDJi
release. SABnzbd's log explicitly records `Ignoring duplicate NZB` and purging that
new attempt. Its addfile response reported false status with no explanatory error.
The proxy raised a generic client exception before the existing no-download-ID
rejection handling could run. Reservation 13 therefore became Uncertain, retained
the SDR slot and caused a VersionDownloadLimit pending Activity entry. This was not
an active SABnzbd download, although nzb360 displayed it as downloading.

The addfile path now permits that empty-error negative response to reach the
existing client adapter, which rejects it with DownloadClientRejectedReleaseException.
The reservation is recorded as definitively Failed and its slot is released, allowing
the automatic search to continue. Explicit client errors, errors from other API
methods and ambiguous transport failures retain their existing handling. The rejection
message now mentions a possible duplicate without claiming every rejection is one.

Validation: 174 focused SABnzbd, download service, version decision and reservation
tests passed (one existing skip). Coverage includes the real proxy-to-adapter path,
managed reservation release, no grab event on rejection, accepted download IDs,
explicit API errors and uncertain timeouts. The backend was built and deployed.

After independently checking SAB's duplicate log and empty live queue, the stale
pending entry was removed and reservation 13 was resolved through the existing
confirm-not-submitted API. No database edits or SAB duplicate-setting changes were
made. A fresh automatic search (command 2302) rejected both PRoDJi candidates with
Failed reservations 14/15, then submitted the HDC SDR release as reservation 16.
Both Radarr and SABnzbd reported the exact ID `SABnzbd_nzo_n33ht4gz` downloading.
HDR retained version 4/file 5. The new SDR download was left running.

Evidence: `_temp/sab-duplicate-tests.log`, `_temp/sab-duplicate-live-result.json`,
`_temp/sab-duplicate-before.json` and `_temp/sab-duplicate-after.json`.

## Quality screen size-setting scope selector (2026-11-04)

Profile-specific size controls now live under Settings > Quality rather than in
the profile modal. The Size Settings For dropdown offers Global and all ordinary
profiles, including composite child profiles. Composite parent profiles are excluded
using an additive read-only `isComposite` field on profile API responses. Both draft
and active composites are identified by their persisted version definitions.

Global retains its existing quality-definition editor and reset command. Profile mode
edits the existing per-quality overrides through the existing profile save API;
unchecked qualities continue to inherit global values. Switching with unsaved edits
uses the existing discard/cancel dialog, switching is disabled during a save, and
the global reset control is hidden in profile mode. Storage and saved size values
are unchanged; no migration is involved.

Validation: frontend lint and full webpack/type checks passed, all 69 v3 API tests
passed, and 16 dependency-container checks passed (one existing skip). An isolated
frontend check covered selector options, save callback routing, discard/cancel,
save-in-flight switching and reset isolation. The backend/frontend were deployed
to the running sandbox. Live API verification classified SDR/HDR as ordinary and
SDR+HDR as composite; complete before/after comparisons preserved all saved profile
fields and all global quality definitions. Movie lookup remained HTTP 200.

Evidence: `_temp/quality-size-*-before.json`, `_temp/quality-size-*-after.json`,
`_temp/quality-size-scope-api-tests.log`, `_temp/quality-size-container.log` and
`_temp/quality-size-frontend-build.log`.

## Shared Quality controls and global preference (2026-11-04)

Global and ordinary-profile modes now share the same QualityDefinitions table and
QualityDefinition rows, preserving quality order, column spacing, sliders, size labels
and advanced numeric inputs. Profile mode hides title textboxes and the title heading
while keeping their column spacing. Rows resynchronize slider positions when incoming
values change, including global-mode toggles and copying.

The profile controls beside the selector include Use global values and Copy global
values. Global mode displays current global values, disables editing and ignores
custom overrides in the shared backend size resolver. Custom overrides are retained
for switching back. Copy replaces all existing overrides with a snapshot of the saved
global definitions; subsequent profile edits remain independent. Copy is disabled
while global mode is enabled. Both operations use the existing Save Changes workflow.

Migration 252 adds the nullable UseGlobalSizeDefinitions preference. Existing profiles
retain their behavior. Legacy API updates that omit the preference preserve its stored
value; explicit false disables it. A consistent SQLite backup was taken before the
live migration at `_temp/quality-global-before-migration.db`.

Validation passed 94 core profile/size/ranking tests, 69 v3 API tests, 16 container
checks (one existing skip), the full frontend build and isolated frontend behavior
checks. These cover persisted toggle roundtrips, runtime override bypass/restoration,
legacy omission, explicit false, shared rows, hidden titles, disabled editing and
copy snapshots. Frontend lint has no errors and one conditional-style warning.

The tested backend/frontend were deployed. A temporary ordinary profile verified
toggle persistence with custom values retained, legacy omission preservation and a
30-quality global copy through the live API. It was deleted after validation. Complete
before/after comparisons confirmed that existing profiles and global quality definitions
were unchanged. Movie lookup remained HTTP 200. Browser layout/click verification was
not performed; the shared components and their handlers were checked in isolation.

Evidence: `_temp/quality-global-live-result.json`, `_temp/quality-global-*-tests.log`,
`_temp/quality-global-container.log`, `_temp/quality-global-frontend-build.log` and
the profiles/definitions before/after snapshots with the same prefix.

## Evidence

Sanitized API snapshots, history, final journals, reservations and hash comparisons are
under `_temp/composite-live-test/`. Relevant files include `versions-final.json`,
`files-final.json`, `history-final.json`, `import-journals-final.json`,
`reservations-final.json`, `restart-rescan-check.json`, `profile-size-api-check.json`
and `legacy-protections.json`. API keys were read from local configuration and were not
included in these snapshots or displayed in tool output.
