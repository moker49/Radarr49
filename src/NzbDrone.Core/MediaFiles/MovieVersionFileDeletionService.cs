using System;
using System.IO;
using System.Linq;
using System.Net;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMovieVersionFileDeletionService
    {
        void Delete(int movieId, int movieVersionId, int movieFileId, bool unmonitor);
    }

    public class MovieVersionFileDeletionService : IMovieVersionFileDeletionService
    {
        private readonly IMovieService _movies;
        private readonly IMovieVersionService _versions;
        private readonly IMediaFileService _files;
        private readonly IDownloadReservationRepository _reservations;
        private readonly ITrackedDownloadService _tracked;
        private readonly IDiskProvider _disk;
        private readonly IRecycleBinProvider _recycle;
        private readonly IEventAggregator _events;

        public MovieVersionFileDeletionService(IMovieService movies,
                                              IMovieVersionService versions,
                                              IMediaFileService files,
                                              IDownloadReservationRepository reservations,
                                              ITrackedDownloadService tracked,
                                              IDiskProvider disk,
                                              IRecycleBinProvider recycle,
                                              IEventAggregator events)
        {
            _movies = movies;
            _versions = versions;
            _files = files;
            _reservations = reservations;
            _tracked = tracked;
            _disk = disk;
            _recycle = recycle;
            _events = events;
        }

        public void Delete(int movieId, int movieVersionId, int movieFileId, bool unmonitor)
        {
            if (movieId <= 0 || movieVersionId <= 0 || movieFileId <= 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "Specify the movie, managed version and exact file IDs to delete.");
            }

            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                try
                {
                    DeleteCore(movieId, movieVersionId, movieFileId, unmonitor);
                }
                catch (InvalidOperationException ex)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
                }
            }
        }

        private void DeleteCore(int movieId, int movieVersionId, int movieFileId, bool unmonitor)
        {
            var movie = _versions.ForVersion(_movies.GetMovie(movieId), movieVersionId);
            Require(movie.ManagedVersion?.Id == movieVersionId && movie.MovieFileId == movieFileId && movie.MovieFile?.Id == movieFileId && movie.MovieFile.MovieId == movieId,
                "The selected file is no longer owned by this version. Refresh before deleting anything.");
            Require(!_reservations.ForMovie(movieId).Any(r => r.MovieVersionId == movieVersionId && r.IsActive),
                "This version has an active download reservation. Reconcile it before deleting its file.");
            Require(!_tracked.GetTrackedDownloads().Any(t => t.IsTrackable && t.RemoteMovie?.Movie?.Id == movieId &&
                t.State is not (TrackedDownloadState.Imported or TrackedDownloadState.Failed or TrackedDownloadState.FailedPending or TrackedDownloadState.Ignored) &&
                ((t.MovieVersionId ?? t.RemoteMovie.MovieVersionId) == movieVersionId || !(t.MovieVersionId ?? t.RemoteMovie.MovieVersionId).HasValue)),
                "A tracked download targets this version or has unresolved version identity. Resolve it before deleting the file.");

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(movie.Path));
            var path = Path.GetFullPath(Path.Combine(folder, movie.MovieFile.RelativePath));
            Require(!Path.IsPathRooted(movie.MovieFile.RelativePath) && folder.IsParentPath(path), "The registered file path is outside the movie folder.");
            Require(!_files.GetFilesByMovie(movieId).Any(f => f.Id != movieFileId && Path.GetFullPath(Path.Combine(folder, f.RelativePath)).PathEquals(path)),
                "Another registered file shares this path. Resolve the file ownership conflict before deletion.");
            Require(!_movies.AllMoviePaths().Any(m => m.Key != movieId &&
                (Path.GetFullPath(m.Value).PathEquals(folder) || Path.GetFullPath(m.Value).PathEquals(path) || Path.GetFullPath(m.Value).IsParentPath(path))),
                "This file path overlaps another movie's folder. Resolve the folder conflict before deletion.");

            var root = Path.GetDirectoryName(folder);
            Require(!string.IsNullOrWhiteSpace(root) && _disk.FolderExists(root) && _disk.GetDirectories(root).Any(), "The movie's root folder is unavailable or empty. No file ownership was changed.");
            if (_disk.FileExists(path))
            {
                _recycle.DeleteFile(path, root.GetRelativePath(Path.GetDirectoryName(path)));
            }

            movie.MovieFile.Movie = movie;
            _files.DeleteManagedVersionFile(movie.MovieFile, movieVersionId, unmonitor);
            _events.PublishEvent(new DeleteCompletedEvent());
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
