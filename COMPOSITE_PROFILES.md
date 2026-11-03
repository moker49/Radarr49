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
- Manual import preview, reprocessing and command payloads carry MovieVersionId,
  DownloadClientId and DownloadReservationId. Existing library files expose the version
  associated with their stored file ID. Reprocessing preserves the child profile context.
  Execution preflights every selected source before any import and rejects duplicate
  source selections. Managed imports require explicit version selection.
- Manual client imports require the exact tracked client/download/reservation/version
  identity and a source inside that download's import path. They cannot redirect a managed
  job into an ordinary movie or drop the download ID to claim its source as an offline
  import. Offline selection cannot replace a version with an active reserved job.
  The shared write validator rechecks active reservations before replacement; dispatch
  reservation acquisition uses the same per-movie lock as file imports within the process.
- Manual download completion now uses the shared verified-import check. Folder cleanup
  only follows verified completion, and results are grouped by client, download ID and
  reservation attempt. A successful import result alone cannot complete another version.
- Legacy single/bulk folder-move commands enforce the composite mutation policy before
  physical changes, including direct command requests. Bulk preflight protects mixed
  selections, and individual moves recheck under the per-movie lock. The explicit managed
  folder move/recovery workflow is not implemented or exposed yet.
- Explicit version-file deletion targets movie, version and exact current file IDs. It
  rejects stale/replaced files, active target reservations, unresolved/targeted tracked
  downloads, shared file registrations, overlapping movie folders and unavailable roots
  before recycling. Another version's identified active download does not block deletion.
  The assignment row remains; unmonitoring defaults to that version only and can be
  explicitly disabled, independently of the global automatic-unmonitor setting.
- The repository rechecks exact version/profile ownership and active reservations under
  the movie row lock. File-row removal, assignment detachment, optional version unmonitoring
  and primary summary clearing commit together. Imported reservation rows remain as audit
  history; only the deleted file's effective release ownership expires. Active reservations
  are never cancelled or silently released by deletion.
- Extras recycling now checks video registrations and claims across subtitles, metadata
  and other extras, not just the current provider's category. Shared metadata, including
  recognized shared files with older file-owned registrations, is preserved. Paths outside
  the movie folder or overlapping another movie are not recycled as sidecars.
- Targeted deletion now persists a VersionFileDeletions journal before physical changes.
  The snapshot retains the exact file and registered extras, source sizes and precise UTC
  write-time ticks, monitoring choice and immutable movie path. Recycling markers precede
  each physical action. Detachment/monitoring/primary summary changes and the Detached
  checkpoint commit atomically; extras registration cleanup and cursor advancement also
  commit together. The previous direct recycle/delete path delegates to this runner.
- Startup resumes pending deletions when the recorded state and observed files permit it.
  Ambiguous recycling with an existing file remains blocked until explicitly verified;
  changed source attributes never authorize recycling, even with a retry flag. Missing
  sources are accepted only with the root available and the exact assignment intact.
  Unstarted requests can be cancelled with a note; changed extras can be explicitly
  preserved. Completed exact-file requests replay without selecting a replacement.
- Pending deletions block target registration, deletion, metadata writes, transfers and
  reservation claims. Scans and batch renames pause for the affected movie. Other versions
  retain acquisition eligibility; search returns a temporary rejection for only the held
  version. Housekeeping preserves pending extras registrations until recovery processes
  them. The durable runner owns extras cleanup rather than asynchronous delete subscribers.
- Delete event dispatch has a replay checkpoint and operation ID; replayed history records
  are deduplicated by that ID. The existing event aggregator still treats subscriber delivery
  as best effort, so external notifications are not guaranteed exactly once. This journal
  currently covers explicit version-file deletion, not upgrades/imports or folder moves.

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
   housekeeping. Targeted file deletion now preserves reservation audit rows and expires
   only ownership of the detached file. Whole-movie/version removal still needs explicit
   reservation/pending/history reconciliation; it is not exposed.
4. Import/replacement, transactional file ownership and basic naming/rename paths are
   implemented. Scans, sidecar association, shared metadata and relevant file/metadata
   housekeeping are also implemented. Explicit manual-import backend/API selection is
   implemented. Targeted version-file deletion is implemented. Finish managed folder moves;
   audit notifications
   and remaining command paths. Legacy folder moves are protected, not enabled for composites.
   Managed download script imports are rejected pending dedicated
   support. A durable main-file import handoff is now implemented and tested as an internal
   primitive, through registration. Durable incoming-sidecar installation and registration
   are also implemented as an internal primitive. Runtime imports still use the existing
   standard recycle-before-transfer behavior until durable finalization is ready. Finish
   old-extra cleanup, backup/source cleanup and event delivery before routing
   managed imports through the new journal. Explicit version-file deletion has durable
   recovery. Folder moves still need a dedicated journal before activation.
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
| Radarr.Api.V3 movie/file/profile controllers | Shared guards and explicit manual-import identity implemented; activation and remaining command audits pending |
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

The manual-import stage passes 1,062 core tests and 23 API tests, with five existing core skips. The previously
documented logging-expectation test remains excluded. Coverage includes explicit selection,
client/reservation conflicts, canonical source containment, dropped-identity protection,
command preflight, child-context preservation, completion verification and legacy move
protection. Logs: `_temp/composite-manual-regression.log` and
`_temp/composite-manual-api-tests.log`.

The targeted-deletion stage passes a 1,102-test core regression (five existing skips) and
25 API tests, including trailing-separator handling and both primary-delete monitoring choices.
The documented logging-expectation test remains excluded. Coverage includes real SQLite
ownership rollback, active-reservation rechecks, secondary-file/root-monitor preservation,
release-ownership expiration without deleting audit rows, exact-file API forwarding,
cross-provider extras claims, stale requests and filesystem failures. Logs:
`_temp/composite-version-delete-regression.log`,
`_temp/composite-version-delete-api-tests.log` and
`_temp/composite-version-delete-path-tests.log`.

The deletion-recovery stage passes 1,150 core tests (five existing skips) and 29 API tests.
The documented logging-expectation test remains excluded. New real-SQLite cases cover
concurrent intent creation, snapshot roundtrips with fractional timestamps, interruption
before/after physical recycling, atomic detach, partial extras cleanup, changed-source
protection, cancellation, completed replay, startup resumption and housekeeping retention.
Gating, per-version search isolation and history replay are also checked. Logs:
`_temp/deletion-recovery-regression.log` and `_temp/deletion-recovery-api-tests.log`.

## File operation recovery still required before activation

Explicit version-file deletion has a durable forward-recovery journal. It resumes the
authorized deletion, rather than restoring recycled files automatically. Size and precise
write-time checks detect changed entries; they are not cryptographic content identities.
An explicit physical retry still requires operator verification of the original file.
The supported runtime assumes one application instance owns the media filesystem; the
database locks/indexes protect records but do not provide a distributed filesystem lease.

Upgrades/imports now have the internal main-file handoff foundation described below;
finalization and runtime routing remain unfinished. Folder moves still lack equivalent
operation recovery. Composite activation remains disabled until those lifecycle paths
and the remaining UI/API work are ready. No running development app was restarted by
these test/build steps.

The folder-move audit found that DiskTransferService.TransferFolder can recursively merge
into an existing destination, overwrite file paths, and move files incrementally across
filesystems. A managed move must not just unblock MoveMovieCommand. Its implementation
needs a persisted operation/manifest, destination conflict checks, verification before
committing the new movie path, and restart reconciliation of partial transfers. An initial
root-folder move can preserve the movie folder basename and all relative paths, avoiding
filename changes in the same operation. Source cleanup and post-commit events/extras work
must be recoverable, and incomplete operations must block conflicting writes/acquisition.

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

## Version-file deletion API

DELETE `/api/v3/movie/{movieId}/versions/{movieVersionId}/file/{movieFileId}` removes only
the exact currently assigned file. `unmonitor=true` is the default; explicitly pass
`unmonitor=false` to preserve that version's monitoring. The movie, other version,
definition IDs and selected composite profile are retained. Successful deletion returns
HTTP 204. Stale/mismatched identities, active downloads or ownership/path conflicts return
HTTP 409 before physical changes; positive IDs are required. Ordinary/implicit versions
cannot use this route. Legacy movie/file delete endpoints continue to reject composites.

This route does not cancel client jobs, remove a version definition or delete the whole
movie. If a reservation is active, reconcile it explicitly before attempting deletion.

## Deletion recovery API

GET `/api/v3/movie/{movieId}/versionfiledeletions` lists checkpoints, exact movie/version/file
identity, monitoring choice, original paths/attributes, current extra, recycle path when
known, timestamps and notes. Each delete request retains its original monitoring choice;
retrying with a different choice returns HTTP 409.

POST `.../{operationId}/resume` accepts `{}` for ordinary forward recovery. If recycling
may have started and the original path still exists, use `retryOriginalSource: true` with
a nonempty verification `note` only after inspecting that original file. Attribute changes
still block recycling. For changed extras, `preserveChangedExtras: true` with a note retains
the physical files and clears only their old registrations. Both options apply to the
current persisted checkpoint; neither can override stale ownership or path checks.

POST `.../{operationId}/cancel` requires a `note` and only cancels Prepared operations,
before any recycling marker was written. Completed operations are idempotent; cancelled
operations require a new exact-file delete request. No automatic timeout releases an
unfinished operation. Inspection/resumption is scoped to the route's owning movie.

| State | Meaning |
| --- | --- |
| Prepared | Intent/snapshot persisted; no recycling marker written |
| Recycling | Main-file recycling may have begun; existing paths need verified retry |
| Detached | File row/assignment/monitoring committed; extras cursor pending |
| RecyclingExtra | Current extra recycling may have begun; cursor not committed |
| EventsPending | Durable physical/registration cleanup done; event dispatch pending |
| Completed | Journal no longer blocks writes; exact request can replay safely |
| Cancelled | Unstarted intent cancelled; files and monitoring were not changed |

## Import handoff foundation (internal, not runtime-routed)

Migration 250 adds VersionFileImports. VersionImportHandoffService prepares a detached
snapshot of the incoming file, previous file, reservation identity and source/old sidecars.
It hashes file contents with SHA-256; the snapshot records exact sidecar write-time ticks
as well as sizes and hashes. Preparation does not move or remove any file.

The handoff always makes a full isolated staging copy, even when ordinary import settings
prefer hardlinks or moves. This costs additional disk space and sequential I/O. It verifies
that copy before moving the previous version to a private backup under
`.radarr-version-imports/{operation}/`. It then installs the incoming file without overwrite.
The original download remains present regardless of the eventual copy/move choice.

| Checkpoint | Recovery behavior |
| --- | --- |
| Prepared | Verify original source identity before beginning copy |
| Staging | Reuse only an exact verified staging copy; partial copies stop for inspection |
| Staged | Revalidate child profile and verify staging before writing backup marker |
| BackingUp | Recover an already completed backup move; ambiguous paths stop |
| Installing | Recover an already completed install move; verify destination and backup |
| Registered | File, version pointer, primary summary and reservation import ownership committed together |
| Cancelled | Allowed only before BackingUp; retain private staging artifacts for inspection |

Registration is atomic and repeatable. A failed database transaction preserves the previous
registration and can retry against the already installed, verified replacement. Both versions'
monitoring is preserved. Other-version files are untouched. Path ownership, active deletions,
exact submitted reservation identity and current child-profile rules are rechecked.

Registered is deliberately still active: no source cleanup, backup recycling or event
publication occurs here. Pending imports block target-version writes
and acquisitions, retain old sidecar registrations during housekeeping, and prevent completed
download handling from removing the client job/source. Whole-movie mutations also remain
blocked through the existing shared operation guard.

VersionImportSidecarService continues Registered imports through Finalizing to
CleaningOldExtras. It persists a sidecar plan in the existing embedded snapshot before
performing any sidecar transfer. Each incoming sidecar has independent Prepared, Copying,
Staged, BackingUp, Installing and Registered checkpoints. The source video stem is replaced
with the destination video stem while retaining the sidecar suffix, for example
`300.2006.SDR.en.forced.srt` becomes `300 (2006) - SDR.en.forced.srt`. Existing subtitle
language/tag parsing populates the new registration; other supported sidecars use ExtraFiles.
Import options changing after capture do not change the persisted source selection/plan.

Each sidecar is copied and SHA-256 verified before its old destination is moved to a
private backup. Unregistered destinations, changed old files, shared ownership and new
claims across all three extra-file categories block replacement. The installation move
never overwrites an existing file. Exact old registrations are removed and the new owner
and Registered checkpoint commit atomically; a failed transaction retains the old rows
and safely retries the verified physical installation. Replay verifies installed content,
backup identity and the exact new registration. Video ownership and any imported
reservation are revalidated as well.

CleaningOldExtras remains active. The sidecar runner stops there, with video/sidecar
sources, all previous-file backups and non-replaced old extra registrations retained.
Subsequent cleanup must distinguish replaced sidecars (whose previous content is at
their private backup) from untouched old extras; it must never recycle an installed
replacement using an old snapshot's destination path. No cleanup events are emitted yet.

There is no public begin/resume/complete import route and no startup replay of these
primitives. Normal runtime imports do not call them yet. Remaining work is old-extra
cleanup, backup/source cleanup, event/history dispatch and operator recovery APIs,
followed by runtime routing and restart integration. Folder-move recovery is separate.

Validation including the incoming-sidecar stage: 1,197 core tests pass, with five existing skips, and 29 profile
API tests pass. The previously reproduced unrelated
`DownloadDecisionMakerFixture.broken_report_shouldnt_blowup_the_process` failure remains
excluded from this regression selection. Tests cover SHA-256 against actual bytes,
interrupted copies, restarts between physical moves/checkpoints, first acquisition,
transaction rollback/retry, changed sources/backups/profiles/reservations, path containment,
cross-operation gates, pending sidecar retention and delayed download completion. Twenty
additional sidecar tests cover fresh installs and replacements, language/tag suffixes,
other extras, interruptions after both physical moves, registration rollback/retry,
changed sources/destinations/backups/owners/reservations, shared-file protection,
stable captured settings and premature finalization rejection. Filesystem
handoff tests use a simulated disk and real SQLite; full real-filesystem/restart integration
remains pending. Logs: `_temp/import-sidecar-regression.log` and
`_temp/import-sidecar-api-tests.log`.

## Manual import API

GET `/api/v3/manualimport` accepts optional `movieVersionId`, `downloadClientId` and
`downloadReservationId`, and returns these fields on each item. A library listing using
`movieId` without a download ID exposes existing file-to-version assignments; use no
`folder` to request that listing when a version filter is supplied. An explicit version
plus `folder` previews that folder for the selected movie/version instead.

POST reprocessing items and `ManualImport` command `files` accept the same three nullable
fields. Ordinary imports keep them absent. For a composite, supply the destination
`movieVersionId`. When `downloadId` identifies a client job, also supply its exact client
and reservation IDs; the source must be within that job's import path. Without a download
ID, omit client/reservation IDs and select a version with no active reserved job. The
server rejects missing/conflicting identities with HTTP 409, or fails a queued command
before its first import during preflight.

These backend/API changes do not add a version selector to the UI or enable composite
activation. A separate version-aware folder relocation and recovery flow is still needed.

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
