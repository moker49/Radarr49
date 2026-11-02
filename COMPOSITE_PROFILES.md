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
  and downloads remain blocked until the remaining lifecycle work and activation flow
  are complete. Search, tracking, import and naming now use explicit version contexts.
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
- Backend assignment/conversion is transactional and idempotent. Existing files require
  explicit definition-to-file mappings; every stored file must be accounted for exactly
  once. Stale movie/file/definition snapshots fail without partial writes. No conversion
  or activation route calls this primitive yet.
- Configuration, conversion, ordinary movie mutations and profile deletion share a
  mutation lock, with database transactions protecting multi-row changes. Legacy mutation
  protection also runs in the movie service so controller preflight cannot be bypassed
  by a concurrent conversion.
- History, download history and pending rows persist nullable MovieVersionId; old rows
  remain unversioned. Tracking restores the persisted movie/version, applies an isolated
  child-profile/file context, and blocks missing/conflicting identities. Pending add,
  removal and queue grouping distinguish versions. Reprocessing carries a transient
  pending version marker back into decision-making.
- GET `/api/v3/movie/{movieId}/versions` exposes implicit or explicit version state.
  History resources also expose MovieVersionId.
- Search/RSS release evaluation expands into independent child-profile/file contexts.
  Parsed data, release language lists and format scores are isolated. Version definitions
  and file contexts are cached per batch rather than queried for every release. Pending
  reprocessing targets only its original movie/version; stale assignments are rejected.
- Queue cutoff, recent-history and pending/delay comparisons are scoped to the target
  version. Interactive release cache keys include version identity, and legacy overrides
  cannot strip that identity. Release and queue resources expose MovieVersionId.
- Prefer-first ordering prioritizes missing copies before further upgrades and honors the
  configured version order within those groups. Unavailable or delayed SDR does not block
  eligible HDR. Draft configuration accepts maximumConcurrentDownloads of 1 (default) or 2.
  The scheduler respects active queue items, same-version occupancy and grabs within the
  batch; it detects duplicate URLs/GUIDs/torrent hashes while they are active.
- Durable DownloadReservations now serialize slot acquisition using the owning movie's
  database row/write lock. Active-version and bound-client-ID uniqueness also have database
  indexes. Both automatic and interactive dispatch go through the central download service:
  persist intent, persist dispatch marker, call client, persist returned ID, then publish grab.
  Imported release ownership remains effective while its file belongs to that version.
- Preparation failures and definite rejections release slots. Ambiguous dispatch failures
  and accepted jobs without IDs remain active for explicit reconciliation; there is no
  automatic expiration that could cause duplicate submission. Tracking can recover from a
  submitted reservation even if history writes failed, and scopes reused IDs to the current
  reservation attempt. Blocked cached items can recover when identity becomes available.
- Explicit reservation inspection/reconciliation endpoints are implemented. Binding requires
  a tracked job on the reserved client, a matching release fingerprint, no conflicting
  version, and a verification note. Imported-file and failed-download event handlers update
  ownership/capacity; import handlers require a verified version-to-file assignment first.
- Imports preserve/reload the explicit version after aggregation and immediately before
  writing. Client imports require the exact bound reservation; unassigned existing files
  require explicit selection. Child quality, language, Custom Format threshold and size
  settings are checked again against the actual file before replacement.
- Import grouping and duplicate suppression distinguish movie/version pairs. Replacement
  selects only the target version's file. Empty sources, unrelated destination files,
  conflicting file registrations and sources already at the destination fail before recycle.
- File insertion/deletion and version ownership/primary summary updates commit together.
  File events/history carry version identity; managed file events no longer overwrite the
  root's primary summary. Automatic unmonitor affects the deleted version on manual or
  missing-file deletion, and preserves monitoring during upgrades/metadata refresh.
- Naming uses the exact movie folder plus version label, including existing assigned files
  and when ordinary renaming is disabled. Rename batches serialize with imports/conversion
  and avoid legacy movie updates. Legacy file deletion rechecks protection while holding
  the same per-movie lock before removing a physical file.
- Singular movie repository joins now consistently select MovieFileId, so queries cannot
  load an arbitrary secondary file. Completed downloads require their own verified imported
  reservation; history and import-result checks cannot complete a different version.
- Scans serialize their disk snapshot, cleanup and events with imports/conversion/renames.
  They reload the root, validate the version registry before cleanup, preserve registered
  assignments, refresh file media info in each child context and publish a fresh primary
  summary. Unassigned video files still require explicit version selection; matching a
  filename label alone does not assign a video.
- Existing subtitles and other sidecars resolve against actual registered video paths,
  with exact basename or dot-suffix matching in the same directory. Ambiguous or unmatched
  files stay unmanaged. Aggregation preserves explicit version context before subtitle
  parsing. Managed download extras use the source video's basename and do not use the
  broad movie-title or single-video subtitle fallback. Generic names and nested subtitle
  layouts that cannot be matched safely remain for explicit handling.
- Recognized shared artwork and movie/folder NFOs have no file owner. File-specific
  metadata retains the matched version's file ID. Providers generating one shared NFO
  write it only from the primary version; refresh reuses the existing record. Shared
  metadata survives version removal, while per-file extras remain scoped to their owner.
- Extras scan/cover/rename events serialize with imports. Delayed deletion checks whether
  a replacement registered the same sidecar path before recycling it, and metadata-only
  file refresh preserves sidecars. Duplicate metadata records sharing the retained path
  are removed without recycling that physical file.
- Housekeeping preserves files owned by MovieVersions even when they are not the primary.
  Metadata deduplication now distinguishes movie, file owner, consumer and path, retaining
  both versions' NFOs and shared metadata while removing all true duplicate records.

## Remaining sequence

1. Profile-owned size settings and their use in independent child decision contexts are implemented.
2. Draft configuration, legacy protections and backend atomic conversion are implemented.
   Add version-aware activation/conversion orchestration, events and assignment
   reconciliation, including files and downloads already in flight, before exposing it.
3. Search/RSS expansion, scoped comparisons, prefer-first ordering and durable dispatch
   reservations are implemented. Finish integration with version-aware imports and manual
   import context, and add end-to-end recovery checks before activation. Manual imports of
   a reserved download must retain its bound client/download identity; never infer that
   association from a sole pending job. Keep active and currently owned reservations during
   housekeeping; reconcile/cascade them explicitly when version-aware deletion is added.
4. Import/replacement, transactional file ownership and basic naming/rename paths are
   implemented. Scans, sidecar association, shared metadata and relevant file/metadata
   housekeeping are also implemented. Finish folder moves, version-aware deletion and
   explicit manual-import routes; audit notifications and remaining command paths.
   Managed download script imports are rejected pending dedicated
   support. Existing standard recycle-before-transfer behavior remains: a transfer failure
   can leave the target's previous file in the recycle bin; there is no physical rollback.
   Audit crash recovery between file transfer, registration, extras and import events.
5. Version-aware UI, API summaries and wanted/cutoff queries.
6. Full lifecycle tests and actual nzb360/Seer add-workflow validation before activation.

## Single-file assumption inventory

| Location | Required change |
| --- | --- |
| Movies/Movie.cs | Retain legacy fields as summaries; resolve actual version state explicitly |
| Movies/MovieRepository.cs | Primary file joins implemented; version-aware missing/cutoff queries remain |
| Movies/MovieService.cs | Managed file-event ownership/summary updates implemented |
| MediaFiles/UpgradeMediaFileService.cs | Targeted replacement and pre-recycle checks implemented |
| MediaFiles/MovieImport/ImportApprovedMovie.cs | Movie/version grouping and validation implemented |
| MediaFiles/DiskScanService.cs | Serialized snapshots, preserved assignments and per-file contexts implemented; new video assignments remain explicit |
| DecisionEngine/Specifications | Child contexts and scoped queue/history/delay checks implemented |
| DecisionEngine/DownloadDecisionComparer.cs | Effective child profile size settings implemented |
| Download/ProcessDownloadDecisions.cs | Version grouping and prefer-first scheduling implemented; reservation races become pending releases |
| Download/Pending/PendingRelease.cs | Identity and scoped pending contexts implemented |
| Download/TrackedDownloads/TrackedDownload.cs | Identity restored; incomplete/conflicting assignments block import |
| History/History.cs | Identity persisted; scope best-quality/cutoff history queries and file events to version |
| Radarr.Api.V3 movie/file/profile controllers | Shared guards implemented; audit command/manual-import routes before activation |
| Extras and housekeeping | Per-file sidecars, shared metadata and secondary-file preservation implemented |
| Organizer folder moves and notifications | Audit file selection and scope to version |

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

The assignment/identity stage passes 140 relevant core tests and 10 API tests, with one
existing ignored test. Coverage includes migration preservation, atomic rollback,
idempotency, isolated child contexts, persisted identity, restart recovery and cross-version
pending isolation. A broader decision-maker test, `broken_report_shouldnt_blowup_the_process`, expects
three error logs but captures none in this environment. It also fails with this stage's
decision-maker change removed; its assertion has not been weakened or changed.
It is excluded from the passing regression run and recorded separately in
`_temp/decision-maker-baseline.log`.

The search/scheduling stage passes 185 relevant core tests (one existing ignored test)
and 13 API tests. The same independently reproduced logging-expectation test remains
excluded. Coverage includes per-profile size decisions from one fetched release, pending
target preservation, prefer-first fallback, missing-copy priority, concurrency limits,
duplicate active-release detection, batch caching and interactive cache separation.

The durable-dispatch stage adds real SQLite concurrency/slot tests, dispatch ordering,
uncertain failure handling, release ownership, reconciliation validation and recovery
without grab history. Its regression run passes 243 core tests (one existing ignored test)
and 17 API tests; the documented logging-expectation test remains excluded. PostgreSQL uses
the same transaction/row-lock SQL path but has not
been exercised against a live PostgreSQL database in this checkout.

The import/ownership stage passes 867 core tests with five existing platform/ignored
skips and 17 API tests. The previously documented logging-expectation test remains
excluded. Coverage includes real SQLite file ownership/summary transactions, primary-only
query joins, cross-version replacement isolation, pre-write collisions, reservation-bound
imports/completion, child profile enforcement, exact naming and protected legacy deletion.
Legacy test builders now explicitly leave version IDs null where they represent old rows.
Logs: `_temp/composite-import-regression.log` and `_temp/composite-import-api-tests.log`.

The scan/extras stage passes a broader 1,031-test core regression (five existing skips),
17 compatibility API tests and two additional real-SQLite scan persistence tests. The latter run repeated scans
with two registered versions, then verify missing-secondary cleanup preserves the primary
assignment and summary. Targeted tests cover actual sidecar matching, subtitle aggregation,
shared/file-specific metadata, delayed cleanup and housekeeping preservation. The documented
baseline logging-expectation test remains excluded. Logs:
`_temp/composite-scan-extras-regression.log`, `_temp/composite-scan-persistence.log` and
`_temp/composite-scan-extras-api.log`.

## Draft configuration API

Use an unused ordinary profile as the parent (give it the desired composite name first).
The separate endpoint is `/api/v3/qualityprofile/{profileId}/composite`:

- GET returns the ordered definitions and `isDraft: true`.
- PUT accepts `versions`, each containing `id` (zero for initial creation),
  `qualityProfileId`, `label`, `priority` (0 or 1), and `isPrimary`.
  The route supplies the parent profile ID. Subsequent saves must retain both definition
  IDs returned by the API. Definition order in the JSON does not determine priority.
  The body also accepts `maximumConcurrentDownloads`: 1 for sequential downloads (the
  default), or 2 to allow both versions concurrently once acquisition is enabled.
- DELETE removes an unused draft's definitions, leaving the ordinary parent profile intact.

No activation endpoint or composite editor is exposed yet. The ordinary profile endpoint
continues to list the parent, but assignment currently returns HTTP 409. Legacy edits or
deletes against protected targets also return HTTP 409 with an explanation; this applies
to the built-in legacy interface as well as external clients. The future legacy add
workflow will be enabled once version assignments, downloads and imports are ready.

## Reservation state and reconciliation API

GET `/api/v3/movie/{movieId}/downloadreservations` lists intent, version/client identity,
state, timestamps and diagnostic/verification notes. Release URLs and fingerprint keys
are not returned as resource fields.

| State | Slot behavior |
| --- | --- |
| Prepared | Intent persisted; client dispatch has not begun; slot held |
| Dispatching | Marker persisted before client call; slot held |
| Submitted | Returned client ID persisted; slot held through verified import |
| Uncertain | Submission outcome unresolved; slot held until verified reconciliation |
| Imported | Slot released; release ownership retained while the assigned file exists |
| Failed | Slot and release claim released after a known failure or explicit confirmation |

POST `.../{reservationId}/confirm-not-submitted` accepts a verification `note` and can
release an unresolved, unbound attempt. It cannot clear an already bound submission.
POST `.../{reservationId}/bind-download` accepts a `downloadId` and verification `note`.
The client job must already be tracked and match the reserved release. Both operations
validate route ownership; no legacy DELETE endpoint can release reservations.
