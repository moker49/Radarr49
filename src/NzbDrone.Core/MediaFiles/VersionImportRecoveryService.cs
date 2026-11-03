using System;
using System.IO;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IVersionImportRecoveryService
    {
        VersionFileImport Get(int movieId, int operationId);
        void Resume(int movieId, int operationId, string checkpoint = null, bool retryPhysical = false, bool preserveChangedExtras = false, bool preserveChangedSources = false, string note = null);
        void Cancel(int movieId, int operationId, string checkpoint, string note);
    }

    public class VersionImportRecoveryService : IVersionImportRecoveryService, IHandleAsync<ApplicationStartedEvent>
    {
        private readonly IVersionFileImportRepository _operations;
        private readonly IVersionImportHandoffService _handoff;
        private readonly IVersionImportSidecarService _sidecars;
        private readonly IVersionImportCleanupService _cleanup;
        private readonly IVersionImportCompletionService _completion;
        private readonly Logger _logger;

        public VersionImportRecoveryService(IVersionFileImportRepository operations,
                                            IVersionImportHandoffService handoff,
                                            IVersionImportSidecarService sidecars,
                                            IVersionImportCleanupService cleanup,
                                            IVersionImportCompletionService completion,
                                            Logger logger)
        {
            _operations = operations;
            _handoff = handoff;
            _sidecars = sidecars;
            _cleanup = cleanup;
            _completion = completion;
            _logger = logger;
        }

        public VersionFileImport Get(int movieId, int operationId)
        {
            var operation = _operations.Get(operationId);
            if (operation.MovieId != movieId)
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "The import operation does not belong to this movie.");
            }

            return operation;
        }

        public void Resume(int movieId, int operationId, string checkpoint = null, bool retryPhysical = false, bool preserveChangedExtras = false, bool preserveChangedSources = false, string note = null)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                var operation = Get(movieId, operationId);
                var overrides = retryPhysical || preserveChangedExtras || preserveChangedSources;
                CheckCheckpoint(operation, checkpoint, overrides);
                if (operation.State == VersionFileImportState.Completed)
                {
                    Require(!overrides, "A completed import has no physical recovery checkpoint.");
                    return;
                }

                Require(operation.State != VersionFileImportState.Cancelled, "This import was cancelled. A new import intent is required.");
                int? recoveryIndex = null;
                if (overrides)
                {
                    Require(!string.IsNullOrWhiteSpace(note), "A verification note is required for physical recovery overrides.");
                    recoveryIndex = VersionImportRecoveryCheckpoint.CurrentCleanupIndex(operation);
                    Require(recoveryIndex.HasValue, "There is no pending cleanup entry to authorize.");
                    var item = operation.Snapshot.Cleanup[recoveryIndex.Value];
                    Require(operation.State == VersionImportCleanupPlan.Phase(item.Kind), "The current cleanup entry has not reached its physical recovery phase.");
                    Require(!retryPhysical || item.State == VersionImportCleanupState.Recycling, "Only an interrupted recycling checkpoint needs a physical retry.");
                    Require(!preserveChangedExtras || item.Kind == VersionImportCleanupKind.OldExtra, "Extra preservation applies only to the current old-extra entry.");
                    Require(!preserveChangedSources || item.Kind == VersionImportCleanupKind.Source, "Source preservation applies only to the current source entry.");
                }

                try
                {
                    Run(operation, recoveryIndex, retryPhysical, preserveChangedExtras, preserveChangedSources, note);
                    _operations.RecordRecoveryResult(operationId, null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    TryRecordFailure(operationId, ex.Message);
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
                }
                catch (Exception ex)
                {
                    TryRecordFailure(operationId, ex.Message);
                    throw;
                }
            }
        }

        public void Cancel(int movieId, int operationId, string checkpoint, string note)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                var operation = Get(movieId, operationId);
                CheckCheckpoint(operation, checkpoint, true);
                Require(!string.IsNullOrWhiteSpace(note), "A cancellation note is required.");
                Require(operation.State is VersionFileImportState.Prepared or VersionFileImportState.Staging or VersionFileImportState.Staged, "The import may already have moved the previous file. It cannot be cancelled at this checkpoint.");
                try
                {
                    _handoff.CancelBeforeBackup(movieId, operationId, note);
                    _operations.RecordRecoveryResult(operationId, null);
                }
                catch (InvalidOperationException ex)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
                }
            }
        }

        public void HandleAsync(ApplicationStartedEvent message)
        {
            foreach (var operation in _operations.Pending().OrderBy(o => o.Created).ThenBy(o => o.Id))
            {
                try
                {
                    Resume(operation.MovieId, operation.Id);
                }
                catch (Exception ex)
                {
                    _logger.Info("Version-file import {0} remains pending recovery: {1}", operation.Id, ex.Message);
                }
            }
        }

        private void Run(VersionFileImport operation, int? recoveryIndex, bool retryPhysical, bool preserveChangedExtras, bool preserveChangedSources, string note)
        {
            while (operation.State != VersionFileImportState.Completed)
            {
                VersionFileImportState expected;
                switch (operation.State)
                {
                    case VersionFileImportState.Prepared:
                    case VersionFileImportState.Staging:
                    case VersionFileImportState.Staged:
                    case VersionFileImportState.BackingUp:
                    case VersionFileImportState.Installing:
                        _handoff.Resume(operation.MovieId, operation.Id);
                        expected = VersionFileImportState.Registered;
                        break;
                    case VersionFileImportState.Registered:
                    case VersionFileImportState.Finalizing:
                        _sidecars.Resume(operation.MovieId, operation.Id);
                        expected = VersionFileImportState.CleaningOldExtras;
                        break;
                    case VersionFileImportState.CleaningOldExtras:
                    case VersionFileImportState.RecyclingBackup:
                    case VersionFileImportState.RemovingSource:
                        _cleanup.Resume(operation.MovieId, operation.Id, retryPhysical, preserveChangedExtras, preserveChangedSources, note, recoveryIndex);
                        expected = VersionFileImportState.EventsPending;
                        break;
                    case VersionFileImportState.EventsPending:
                        _completion.Resume(operation.MovieId, operation.Id);
                        expected = VersionFileImportState.Completed;
                        break;
                    default:
                        throw new InvalidOperationException("Unknown import recovery checkpoint.");
                }

                operation = _operations.Get(operation.Id);
                if (operation.State != expected)
                {
                    throw new InvalidOperationException("The import phase did not reach its expected checkpoint. Recovery stopped.");
                }
            }
        }

        private static void CheckCheckpoint(VersionFileImport operation, string checkpoint, bool required)
        {
            Require(!required || !string.IsNullOrWhiteSpace(checkpoint), "Refresh the operation and supply its checkpoint before authorizing recovery changes.");
            Require(checkpoint == null || string.Equals(checkpoint, VersionImportRecoveryCheckpoint.For(operation), StringComparison.Ordinal), "The recovery checkpoint changed. Refresh the operation before retrying.");
        }

        private void TryRecordFailure(int id, string error)
        {
            try
            {
                _operations.RecordRecoveryResult(id, error);
            }
            catch (Exception ex)
            {
                _logger.Info("Could not persist recovery diagnostics for import {0}: {1}", id, ex.Message);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, message);
            }
        }
    }
}
