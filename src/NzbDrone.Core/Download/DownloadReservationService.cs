using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface IDownloadReservationService
    {
        DownloadReservation Reserve(RemoteMovie movie, int downloadClientId);
        void BeginDispatch(int id);
        void Submitted(int id, string downloadId);
        void DispatchFailed(int id, bool definitive, string note);
        List<DownloadReservation> ForMovie(int movieId);
        void ConfirmNotSubmitted(int movieId, int reservationId, string note);
        void BindDownload(int movieId, int reservationId, string downloadId, string note);
        void RemoveFromClient(TrackedDownload tracked);
        void ConfirmRemoved(int movieId, int reservationId, string downloadId, string note);
    }

    public class DownloadReservationService : IDownloadReservationService,
                                              IHandle<MovieFileImportedEvent>,
                                              IHandle<DownloadFailedEvent>
    {
        private readonly IDownloadReservationRepository _repository;
        private readonly ITrackedDownloadService _trackedDownloads;
        private readonly IMovieRepository _movies;
        private readonly IMovieVersionService _versions;
        private readonly IProvideDownloadClient _clients;
        private readonly IVersionFileDeletionRepository _fileOperations;

        public DownloadReservationService(IDownloadReservationRepository repository,
                                          ITrackedDownloadService trackedDownloads,
                                          IMovieRepository movies,
                                          IMovieVersionService versions,
                                          IProvideDownloadClient clients,
                                          IVersionFileDeletionRepository fileOperations)
        {
            _repository = repository;
            _trackedDownloads = trackedDownloads;
            _movies = movies;
            _versions = versions;
            _clients = clients;
            _fileOperations = fileOperations;
        }

        public DownloadReservation Reserve(RemoteMovie movie, int downloadClientId)
        {
            if (movie.MovieVersionId is not > 0 || movie.Movie == null)
            {
                throw new InvalidOperationException("A managed version is required for a download reservation.");
            }

            lock (MovieVersionImportSynchronization.ForMovie(movie.Movie.Id))
            {
                return ReserveCore(movie, downloadClientId);
            }
        }

        private DownloadReservation ReserveCore(RemoteMovie movie, int downloadClientId)
        {
            var owner = _movies.Get(movie.Movie.Id);
            movie.Movie = _versions.ForVersion(owner, movie.MovieVersionId.Value);
            var active = _trackedDownloads.GetTrackedDownloads()
                .Where(t => t.IsTrackable && t.RemoteMovie?.Movie?.Id == owner.Id &&
                    t.State is not (TrackedDownloadState.Imported or TrackedDownloadState.Failed or TrackedDownloadState.FailedPending or TrackedDownloadState.Ignored))
                .Select(t => new ActiveVersionDownload
                {
                    MovieVersionId = t.MovieVersionId ?? t.RemoteMovie.MovieVersionId,
                    DownloadClientId = t.DownloadClient,
                    DownloadId = t.DownloadItem.DownloadId,
                    ReleaseKeys = DownloadReleaseIdentity.GetKeys(t.RemoteMovie.Release)
                }).ToList();
            var intent = new DownloadReservation
            {
                MovieId = owner.Id,
                MovieVersionId = movie.MovieVersionId.Value,
                DownloadClientId = downloadClientId,
                SourceTitle = movie.Release.Title,
                ReleaseKeys = DownloadReleaseIdentity.GetKeys(movie.Release)
            };
            return _repository.TryReserve(intent, active) ?? throw new DownloadReservationConflictException();
        }

        public void BeginDispatch(int id)
        {
            _repository.BeginDispatch(id);
        }

        public void Submitted(int id, string downloadId)
        {
            _repository.BindDownload(id, downloadId, null);
        }

        public void DispatchFailed(int id, bool definitive, string note)
        {
            _repository.RecordFailure(id, definitive, note);
        }

        public List<DownloadReservation> ForMovie(int movieId)
        {
            _movies.Get(movieId);
            return _repository.ForMovie(movieId).OrderByDescending(r => r.Created).ToList();
        }

        public void ConfirmNotSubmitted(int movieId, int reservationId, string note)
        {
            CheckResolution(movieId, reservationId, note);
            try
            {
                _repository.ConfirmNotSubmitted(reservationId, note);
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
            }
        }

        public void BindDownload(int movieId, int reservationId, string downloadId, string note)
        {
            var row = CheckResolution(movieId, reservationId, note);
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Specify the verified download ID from the reserved client.");
            }

            var matches = _trackedDownloads.GetTrackedDownloads().Where(t => t.DownloadClient == row.DownloadClientId &&
                string.Equals(t.DownloadItem.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || (matches[0].MovieVersionId.HasValue && matches[0].MovieVersionId != row.MovieVersionId) ||
                !DownloadReleaseIdentity.GetClientKeys(matches[0].DownloadItem).Intersect(row.ReleaseKeys).Any())
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "The tracked client download must match the reserved release and have no conflicting version assignment. Refresh tracking and verify its identity.");
            }

            try
            {
                _repository.BindDownload(reservationId, downloadId, note, true);
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
            }

            _trackedDownloads.StopTracking(downloadId);
        }

        public void RemoveFromClient(TrackedDownload tracked)
        {
            if (tracked.DownloadReservationId is not > 0 || tracked.MovieVersionId is not > 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Managed removal requires the exact version and reservation identity.");
            }

            var row = _repository.Get(tracked.DownloadReservationId.Value);
            lock (MovieVersionImportSynchronization.ForMovie(row.MovieId))
            {
                row = _repository.Get(row.Id);
                if (row.MovieVersionId != tracked.MovieVersionId || row.DownloadClientId != tracked.DownloadClient ||
                    !string.Equals(row.DownloadId, tracked.DownloadItem.DownloadId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(row.DownloadId) ||
                    row.State is not (DownloadReservationState.Submitted or DownloadReservationState.Uncertain or DownloadReservationState.Failed or DownloadReservationState.Imported))
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "The tracked download does not match its bound reservation.");
                }

                _fileOperations.CheckReady(row.MovieId, row.MovieVersionId);
                var client = _clients.Get(row.DownloadClientId) ?? throw new NzbDroneClientException(HttpStatusCode.Conflict, "The reserved download client is unavailable.");

                // A failed/ambiguous client call must retain the slot. A crash after a
                // successful removal can be reconciled through ConfirmRemoved.
                client.RemoveItem(tracked.DownloadItem, true);
                if (row.State != DownloadReservationState.Imported)
                {
                    MarkRemoved(row, "Download explicitly removed from its client through the activity API.");
                }
            }
        }

        public void ConfirmRemoved(int movieId, int reservationId, string downloadId, string note)
        {
            var row = CheckResolution(movieId, reservationId, note);
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                row = CheckResolution(movieId, reservationId, note);
                if (string.IsNullOrWhiteSpace(downloadId) || !string.Equals(row.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase) ||
                    row.State is not (DownloadReservationState.Submitted or DownloadReservationState.Uncertain or DownloadReservationState.Failed))
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "Confirm the exact bound download ID of a removed, unimported reservation.");
                }

                _fileOperations.CheckReady(movieId, row.MovieVersionId);
                var client = _clients.Get(row.DownloadClientId) ?? throw new NzbDroneClientException(HttpStatusCode.Conflict, "The reserved download client is unavailable.");

                // Enumerate fully: client errors, unavailable history or incomplete
                // enumeration must not be interpreted as proof that the job is gone.
                var items = client.GetItems()?.ToList() ?? throw new NzbDroneClientException(HttpStatusCode.Conflict, "Could not inspect the reserved download client's queue/history.");
                if (items.Any(i => string.Equals(i.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "The bound job still exists in the download client's queue/history.");
                }

                MarkRemoved(row, note);
                var tracked = _trackedDownloads.Find(row.DownloadId);
                if (tracked?.DownloadClient == row.DownloadClientId && tracked.DownloadReservationId == row.Id)
                {
                    _trackedDownloads.StopTracking(row.DownloadId);
                }
            }
        }

        private void MarkRemoved(DownloadReservation row, string note)
        {
            try
            {
                _repository.MarkRemoved(row.Id, row.DownloadClientId, row.DownloadId, note);
            }
            catch (InvalidOperationException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
            }
        }

        public void Handle(MovieFileImportedEvent message)
        {
            if (!message.NewDownload || !message.MovieInfo.MovieVersionId.HasValue || message.DownloadClientInfo == null || string.IsNullOrWhiteSpace(message.DownloadId))
            {
                return;
            }

            var rows = _repository.FindForDownload(message.DownloadClientInfo.Id, message.DownloadId)
                .Where(r => r.MovieId == message.ImportedMovie.MovieId && r.MovieVersionId == message.MovieInfo.MovieVersionId.Value).ToList();
            if (rows.Count == 1)
            {
                _repository.MarkImported(rows[0].Id, message.MovieInfo.MovieVersionId.Value, message.ImportedMovie.Id);
            }
        }

        public void Handle(DownloadFailedEvent message)
        {
            var tracked = message.TrackedDownload;
            if (tracked?.MovieVersionId == null || string.IsNullOrWhiteSpace(message.DownloadId))
            {
                return;
            }

            var rows = _repository.FindForDownload(tracked.DownloadClient, message.DownloadId)
                .Where(r => r.MovieId == message.MovieId && r.MovieVersionId == tracked.MovieVersionId.Value).ToList();
            if (rows.Count == 1)
            {
                _repository.MarkFailed(rows[0].Id, "Download failed: " + message.Message);
            }
        }

        private DownloadReservation CheckResolution(int movieId, int reservationId, string note)
        {
            var row = _repository.Get(reservationId);
            if (row.MovieId != movieId)
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Reservation does not belong to this movie.");
            }

            if (string.IsNullOrWhiteSpace(note))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Explain how submission was verified before resolving this reservation.");
            }

            return row;
        }
    }
}
