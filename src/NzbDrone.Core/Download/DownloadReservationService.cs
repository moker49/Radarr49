using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
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
    }

    public class DownloadReservationService : IDownloadReservationService,
                                              IHandle<MovieFileImportedEvent>,
                                              IHandle<DownloadFailedEvent>
    {
        private readonly IDownloadReservationRepository _repository;
        private readonly ITrackedDownloadService _trackedDownloads;
        private readonly IMovieRepository _movies;
        private readonly IMovieVersionService _versions;

        public DownloadReservationService(IDownloadReservationRepository repository,
                                          ITrackedDownloadService trackedDownloads,
                                          IMovieRepository movies,
                                          IMovieVersionService versions)
        {
            _repository = repository;
            _trackedDownloads = trackedDownloads;
            _movies = movies;
            _versions = versions;
        }

        public DownloadReservation Reserve(RemoteMovie movie, int downloadClientId)
        {
            if (movie.MovieVersionId is not > 0 || movie.Movie == null)
            {
                throw new InvalidOperationException("A managed version is required for a download reservation.");
            }

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
                _repository.MarkFailed(rows[0].Id, "Download client reported failure: " + message.Message);
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
