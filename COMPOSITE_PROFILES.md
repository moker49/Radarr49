# Composite profile implementation

## Contract

An ordinary quality profile is one implicit movie version. A composite is an existing
quality profile ID with ordered CompositeProfileVersions attached to it. Each definition
references an ordinary profile and has a permanent ID, label, priority and primary flag.
Child profiles own their size settings. Composite names do not determine behavior.

Existing movies are not rewritten. MovieVersionService adapts their existing profile
and file into a single runtime version without persisting anything. Composite movies
require explicit MovieVersions assignments; their legacy MovieFileId is never guessed
to belong to a child. Assignments must be created by a later activation/conversion flow.

Legacy discovery and add APIs will retain qualityProfileId. Legacy destructive endpoints
must reject composite-managed targets, including bulk requests. New explicit APIs will
manage versions. The designated primary version supplies legacy singular file summaries.

Default naming: exact movie folder name followed by ` - SDR` or ` - HDR` and extension.
Prefer the earlier version when candidates exist for both; allow the later version when
the earlier one is unavailable. Primary selection is independent of download ordering.

## Current foundation

- Additive schema and repositories for version definitions and movie assignments.
- Unique assignment per movie/definition and unique non-null file ownership.
- Read-only runtime resolution for ordinary and composite profiles.
- Validation for primary selection, unique labels/priorities, nested composites,
  incomplete assignments and duplicate file ownership.
- No composite creation API or download activation yet. Existing consumers still use
  their original single-file paths; the new resolver is not wired into those paths yet.

## Remaining sequence

1. Profile-owned size settings and a single effective-settings resolver used by release
   acceptance and ranking; retain global defaults for profiles without attached settings.
2. Composite configuration/validation and atomic movie conversion. Preserve files;
   require explicit assignment when ambiguous. Protect legacy edits and deletes before
   allowing creation or activation through any API.
3. Persist version identity through search, RSS, pending releases, history, queue and
   tracked downloads; implement prefer-first scheduling and concurrency controls.
4. Scope imports, upgrades, scans, renames, moves, housekeeping and extra files to versions.
5. Version-aware UI, API summaries and wanted/cutoff queries.
6. Full lifecycle tests and actual nzb360/Seer add-workflow validation before activation.

## Single-file assumption inventory

| Location | Required change |
| --- | --- |
| Movies/Movie.cs | Retain legacy fields as summaries; resolve actual version state explicitly |
| Movies/MovieRepository.cs | Version-aware missing/cutoff queries and file joins |
| Movies/MovieService.cs | Import/delete events update the targeted version |
| MediaFiles/UpgradeMediaFileService.cs | Replace only the targeted version's file |
| MediaFiles/MovieImport/ImportApprovedMovie.cs | Group imports by movie and version |
| MediaFiles/DiskScanService.cs | Preserve assignments and stop on ambiguous new files |
| DecisionEngine/Specifications | Evaluate child profile and its existing file |
| DecisionEngine/DownloadDecisionComparer.cs | Read effective child profile size settings |
| Download/ProcessDownloadDecisions.cs | Deduplicate and prioritize by version |
| Download/Pending/PendingRelease.cs | Persist version identity |
| Download/TrackedDownloads/TrackedDownload.cs | Recover intended version after restart |
| History/History.cs | Persist original download assignment |
| Radarr.Api.V3 movie/file/profile controllers | Guard destructive legacy routes and bulk routes |
| Organizer, extras, housekeeping and notifications | Audit file selection and scope to version |

The critical lifecycle test is add, acquire SDR and HDR, upgrade SDR, restart, rescan,
and verify HDR is untouched and both assignments survive. Also test unavailable SDR,
failures/retries, concurrent imports, ambiguous files, profile changes in flight,
path collisions, duplicate releases and protected legacy operations.
