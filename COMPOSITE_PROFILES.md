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
- Composite draft configuration is available through a separate API. Draft assignment
  and downloads are blocked until version-aware acquisition/import is implemented.
  Existing consumers still use their original single-file paths; the new resolver is
  not wired into those paths yet.
- Ordinary profiles now support per-quality SizeDefinitions in storage, API and profile
  editor. Release size rejection and preferred-size ranking use these settings through
  one shared resolver. Unconfigured qualities retain global defaults. Legacy updates
  omitting the field preserve settings; an explicit empty list resets to global defaults.
- Legacy movie and movie-file edit/delete routes, including bulk routes, preflight through
  a shared policy. Mixed bulk requests fail before any mutation. Definition references
  and persisted movie-version assignments both protect a movie. Ordinary operations remain
  allowed. Composite parent and child profiles cannot be deleted while referenced.
- Draft configuration validates two distinct ordinary child profiles, filename-safe unique
  labels, priority 0/1 and exactly one primary. Nested/self references and converting an
  assigned profile are rejected. Reordering preserves IDs in one database transaction;
  failed saves roll back. Removing an unused draft removes only its definitions.

## Remaining sequence

1. Profile-owned size settings are implemented; extend their use to child profile context
   when version-aware download evaluation is wired in.
2. Draft configuration and legacy edit/delete protections are implemented. Add atomic
   movie conversion and assignment reconciliation before activation. Preserve files and
   require explicit assignment when ambiguous. Coordinate configuration changes with
   assignment/profile deletion to avoid races when activation is introduced.
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
| DecisionEngine/DownloadDecisionComparer.cs | Profile size resolution implemented; supply child context in version-aware evaluation |
| Download/ProcessDownloadDecisions.cs | Deduplicate and prioritize by version |
| Download/Pending/PendingRelease.cs | Persist version identity |
| Download/TrackedDownloads/TrackedDownload.cs | Recover intended version after restart |
| History/History.cs | Persist original download assignment |
| Radarr.Api.V3 movie/file/profile controllers | Shared guards implemented; audit command/manual-import routes before activation |
| Organizer, extras, housekeeping and notifications | Audit file selection and scope to version |

The critical lifecycle test is add, acquire SDR and HDR, upgrade SDR, restart, rescan,
and verify HDR is untouched and both assignments survive. Also test unavailable SDR,
failures/retries, concurrent imports, ambiguous files, profile changes in flight,
path collisions, duplicate releases and protected legacy operations.

## Latest validation

The profile-size stage passed 84 relevant core tests, two API tests, ESLint and an isolated
webpack build. The draft configuration/protection stage passed 86 relevant core tests and
eight API tests, including existing movie-update/download behavior and new configuration,
database rollback and bulk preflight cases. Existing ImageSharp audit warnings are nonfatal
only in test commands; repository warning policy is unchanged.

## Draft configuration API

Use an unused ordinary profile as the parent (give it the desired composite name first).
The separate endpoint is `/api/v3/qualityprofile/{profileId}/composite`:

- GET returns the ordered definitions and `isDraft: true`.
- PUT accepts `versions`, each containing `id` (zero for initial creation),
  `qualityProfileId`, `label`, `priority` (0 or 1), and `isPrimary`.
  The route supplies the parent profile ID. Subsequent saves must retain both definition
  IDs returned by the API. Definition order in the JSON does not determine priority.
- DELETE removes an unused draft's definitions, leaving the ordinary parent profile intact.

No activation endpoint or composite editor is exposed yet. The ordinary profile endpoint
continues to list the parent, but assignment currently returns HTTP 409. Legacy edits or
deletes against protected targets also return HTTP 409 with an explanation; this applies
to the built-in legacy interface as well as external clients. The future legacy add
workflow will be enabled once version assignments, downloads and imports are ready.
