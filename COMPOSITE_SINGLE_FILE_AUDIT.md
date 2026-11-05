# Composite profile single-file audit

Completed November 5, 2026 against the current workspace. No git commands were used.

## Outcome

Six areas needed fixes. Nine regression cases now cover those findings. The backend
fixes are deployed to the sandbox as 10.0.0.16791. The frontend was not overwritten.
No library file was imported, renamed, deleted, or rescanned during this audit.

| Finding | Previous behavior | Fixed behavior |
| --- | --- | --- |
| Orphaned-file housekeeping | A retained unmanaged file was considered orphaned because neither the primary pointer nor a version referenced it. Its database record could disappear. | Preserve explicitly unmanaged files belonging to an existing movie; still delete genuinely orphaned records. |
| Existing-file registration and import sources | Ordinary/manual registration could delete an unmanaged file's row and register it again as managed. An unmanaged source could also enter the import path as a new download. | Reject a registered unmanaged source before any replacement, row deletion, or registration. Profile conversion remains the ownership-changing path. |
| Selected-file rename boundaries and timing | A movie rename command could contain another movie's file IDs. Selected files were read before acquiring the movie lock, allowing conversion to invalidate that snapshot. | Validate every file's movie before the first move; read file ownership under the same movie lock used by conversion/import. Skip unmanaged copies. |
| Sidecar generation, rename and discovery | Scan/rename dispatched unmanaged videos to extras managers. After conversion to ordinary mode, an unmanaged HDR subtitle/artwork could be attributed to the ordinary SDR primary. | Exclude unmanaged videos from automatic extras lists. Recognize their registered video stems and leave their sidecars unclaimed, including in ordinary mode. |
| File dates | Scan-triggered file-date normalization included unmanaged files. | Leave unmanaged file timestamps alone. |
| Already-imported search guard | The search loop guard consulted the whole movie's history. A sibling's grab could incorrectly reject a release or bypass the selected version's loop guard. | Filter grab/import history by the selected version, including the implicit ordinary version. |

The isolated reproduction runs exposed the import, rename, extras/date and history
issues. The housekeeping query was confirmed by source inspection and a real database
fixture covering a primary file, secondary version, retained copy and genuine orphan.
The first housekeeping fixture attempt needed its required movie path corrected before
it could exercise the query; that setup failure is not evidence of a product failure.

## Paths reviewed

| Area | Assessment |
| --- | --- |
| Search/RSS, pending releases, priority and upgrades | `DownloadDecisionMaker` expands candidates into explicit version contexts. Disk/quality/custom-format specifications therefore see the child's profile and file. Pending candidates and reservation capacity are version-scoped. The history exception above was fixed. |
| Download identity and completed-download handling | Tracked jobs restore movie/version/reservation identity and reject conflicting ownership. Completed imported history can survive retired versions. Managed imports avoid ordinary whole-download-folder cleanup. |
| Import, replacement and manual import | Managed writes use the durable import pipeline. Existing-file registration validates the selected version/path; ordinary registration now also refuses unmanaged sources. Replacement destinations are checked against registered owners and on-disk conflicts. |
| Recovery and cleanup | Import/deletion journals retain explicit movie/version/file identity. Recovery checks the movie folder and installed files. Cleanup protects currently registered video/sidecar paths and uses recorded sizes/hashes and checkpoints. Existing failure/retry fixtures were included in the regression run. |
| Rescans and housekeeping | Registered paths are excluded from new import candidates. File metadata updates preserve ownership markers. Missing files are detached by exact ID. Housekeeping now preserves retained registrations. No active repair logic was found in the commented-out `FixWronglyMatchedMovieFiles` body. |
| Naming and rename | Composite naming derives the suffix from the file's explicit version assignment. Unmanaged videos are omitted. Selected-file ownership and conversion synchronization were tightened. Destination conflicts do not authorize replacing a retained copy. |
| Folder moves | Composite folder moves remain explicitly blocked by policy, including direct move commands and bulk preflight. An explicitly requested ordinary movie-folder move moves the folder contents together; this is different from automatic per-file rename. |
| Deletion and bulk operations | Legacy movie/file deletion preflights reject composites and retained files. Explicit version/unmanaged routes check ownership. Version deletion uses durable recovery; shared extras paths are protected. Mixed profile conversions use a transaction and active-operation checks. |
| Profile changes | Conversion records retained identities, retires old assignments without deleting media, reclaims unambiguous matching copies, and clears obsolete pending candidates. Profile-wide membership edits preserve logical slots. |
| Extras and metadata | Version-aware sidecar resolution and shared-primary metadata behavior were reviewed. Unmanaged scan/rename/discovery gaps were fixed. Missing sidecar record cleanup does not recycle the video. |
| API and UI summaries | The singular `movieFile`/`movieFileId` remains the designated primary summary. File lists return separate IDs and ownership markers; version resources report each child's status. UI deletion routes managed, unmanaged and ordinary files separately. |

## Verification

- 2,109 core tests passed, six existing skips. The run included profiles, movie
  conversion/move services, imports, deletion/recovery, downloads, decision engine,
  housekeeping, extras and movie statistics. The previously identified unrelated
  `broken_report_shouldnt_blowup_the_process` case remained excluded.
- All 73 v3 API tests passed.
- All 16 active dependency-container checks passed; one existing test was skipped.
- Backend staging build passed. Existing ImageSharp advisory warnings were nonfatal;
  no package or audit settings were changed by this work.
- A consistent SQLite backup was saved before deployment.
- After deployment/restart, all three sandbox movies and all six video files retained
  their profiles, primary summaries, version IDs/assignments, file IDs, unmanaged flags,
  paths, sizes and modification times. The queue remained empty. There were no unregistered
  top-level video files in the initial disk/API comparison.

Evidence under `_temp/`:

- `single-file-audit-regression.log`, `single-file-audit-api-tests.log`,
  `single-file-audit-container.log`, `single-file-audit-build.log`
- `single-file-audit-before.log`, `single-file-audit-sidecars-before.log`,
  `single-file-audit-history-before.log`, `single-file-audit-targeted.log`
- `single-file-audit-live-before.json`, `single-file-audit-live-after.json`,
  `single-file-audit-before.db`
- Reference inventories: `single-file-audit-references.txt`,
  `single-file-audit-extra-references.txt`, `single-file-audit-search-references.txt`

## Remaining limits

- This is a source, isolated-fixture and read-only live-state audit, not proof that
  every possible scheduling, filesystem or third-party-client interaction is correct.
- Recovery fixtures exercise failures/checkpoints, but this audit did not kill the
  live process mid-import or interrupt real storage/network operations.
- PostgreSQL, Linux/macOS and clients other than the configured sandbox were not run.
- Composite folder moves remain unsupported. Script imports for composites remain
  blocked until version-aware support exists.
- Singular legacy fields describe the primary. Size-on-disk statistics aggregate
  registered copies, including unmanaged ones; consumers should use version resources
  rather than infer per-version completeness from the legacy summary.
- No new browser-layout verification, captured nzb360/Seer request regression suite,
  or Emby/Plex playback test was performed. Earlier manual client checks remain useful
  evidence but are not replaced by this audit.
