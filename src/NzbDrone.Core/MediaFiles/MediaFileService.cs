using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NzbDrone.Common;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileService
    {
        MovieFile Add(MovieFile movieFile);
        void Update(MovieFile movieFile);
        void Update(List<MovieFile> movieFile);
        void Delete(MovieFile movieFile, DeleteMediaFileReason reason);
        void DeleteManagedVersionFile(MovieFile movieFile, int movieVersionId, bool unmonitor);
        List<MovieFile> GetFilesByMovie(int movieId);
        List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds);
        List<MovieFile> GetFilesWithoutMediaInfo();
        List<string> FilterExistingFiles(List<string> files, Movie movie);
        MovieFile GetMovie(int id);
        List<MovieFile> GetMovies(IEnumerable<int> ids);
        List<MovieFile> GetFilesWithRelativePath(int movieIds, string relativePath);
    }

    public class MediaFileService : IMediaFileService, IHandleAsync<MoviesDeletedEvent>
    {
        private readonly IMediaFileRepository _mediaFileRepository;
        private readonly IMovieRepository _movieRepository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IMovieVersionRepository _versions;
        private readonly ICompositeProfileVersionRepository _definitions;
        private readonly IConfigService _config;
        private readonly IVersionFileDeletionRepository _deletions;

        public MediaFileService(IMediaFileRepository mediaFileRepository,
                                IMovieRepository movieRepository,
                                IEventAggregator eventAggregator,
                                IMovieVersionRepository versions,
                                ICompositeProfileVersionRepository definitions,
                                IConfigService config,
                                IVersionFileDeletionRepository deletions)
        {
            _mediaFileRepository = mediaFileRepository;
            _movieRepository = movieRepository;
            _eventAggregator = eventAggregator;
            _versions = versions;
            _definitions = definitions;
            _config = config;
            _deletions = deletions;
        }

        public MovieFile Add(MovieFile movieFile)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieFile.MovieId))
            {
                return AddCore(movieFile);
            }
        }

        private MovieFile AddCore(MovieFile movieFile)
        {
            var versionId = movieFile.Movie?.ManagedVersion?.Id;
            MovieFile addedFile;
            if (versionId.HasValue)
            {
                addedFile = _mediaFileRepository.InsertForVersion(movieFile, versionId.Value);
            }
            else
            {
                var movie = movieFile.Movie ?? _movieRepository.Get(movieFile.MovieId);
                if ((movie != null && _definitions.ForProfile(movie.QualityProfileId)?.Any() == true) || _versions.ForMovie(movieFile.MovieId)?.Any() == true)
                {
                    throw new System.InvalidOperationException("A managed movie requires an explicit version before adding a file.");
                }

                addedFile = _mediaFileRepository.Insert(movieFile);
            }

            if (addedFile.Movie == null)
            {
                addedFile.Movie = _movieRepository.Get(movieFile.MovieId);
            }

            if (versionId.HasValue)
            {
                addedFile.MovieVersionId = versionId;
                addedFile.Movie.MovieFileId = addedFile.Id;
                addedFile.Movie.MovieFile = addedFile;
            }

            _eventAggregator.PublishEvent(new MovieFileAddedEvent(addedFile));

            return addedFile;
        }

        public void Update(MovieFile movieFile)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieFile.MovieId))
            {
                _deletions.CheckReady(movieFile.MovieId, movieFile.MovieVersionId);
                PreserveManagement(movieFile);
                _mediaFileRepository.Update(movieFile);
            }
        }

        public void Update(List<MovieFile> movieFiles)
        {
            var locks = movieFiles.Select(f => f.MovieId).Distinct().OrderBy(id => id).Select(MovieVersionImportSynchronization.ForMovie).ToList();
            var acquired = 0;
            try
            {
                foreach (var sync in locks)
                {
                    Monitor.Enter(sync);
                    acquired++;
                }

                foreach (var file in movieFiles)
                {
                    _deletions.CheckReady(file.MovieId, file.MovieVersionId);
                    PreserveManagement(file);
                }

                _mediaFileRepository.UpdateMany(movieFiles);
            }
            finally
            {
                for (var i = acquired - 1; i >= 0; i--)
                {
                    Monitor.Exit(locks[i]);
                }
            }
        }

        private void PreserveManagement(MovieFile file)
        {
            var stored = _mediaFileRepository.Find(file.Id);
            if (stored != null)
            {
                file.IsUnmanaged = stored.IsUnmanaged;
                file.ManagementProfileId = stored.ManagementProfileId;
            }
        }

        public void Delete(MovieFile movieFile, DeleteMediaFileReason reason)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieFile.MovieId))
            {
                DeleteCore(movieFile, reason);
            }
        }

        public void DeleteManagedVersionFile(MovieFile movieFile, int movieVersionId, bool unmonitor)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieFile.MovieId))
            {
                DeleteCore(movieFile, DeleteMediaFileReason.Manual, movieVersionId, unmonitor);
            }
        }

        private void DeleteCore(MovieFile movieFile, DeleteMediaFileReason reason, int? expectedVersionId = null, bool unmonitor = false)
        {
            // Little hack so we have the movie attached for the event consumers
            if (movieFile.Movie == null)
            {
                movieFile.Movie = _movieRepository.Get(movieFile.MovieId);
            }

            movieFile.Path = Path.Combine(movieFile.Movie.Path, movieFile.RelativePath);

            var versionId = expectedVersionId.HasValue
                ? _mediaFileRepository.DeleteManagedVersionFile(movieFile, expectedVersionId.Value, unmonitor)
                : _mediaFileRepository.DeleteForVersion(movieFile, (reason == DeleteMediaFileReason.Manual || reason == DeleteMediaFileReason.MissingFromDisk) && _config.AutoUnmonitorPreviouslyDownloadedMovies);
            movieFile.MovieVersionId = versionId;
            if (!versionId.HasValue)
            {
                _mediaFileRepository.Delete(movieFile);
            }

            _eventAggregator.PublishEvent(new MovieFileDeletedEvent(movieFile, reason));
        }

        public List<MovieFile> GetFilesByMovie(int movieId)
        {
            return _mediaFileRepository.GetFilesByMovie(movieId);
        }

        public List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds)
        {
            return _mediaFileRepository.GetFilesByMovies(movieIds);
        }

        public List<MovieFile> GetFilesWithoutMediaInfo()
        {
            return _mediaFileRepository.GetFilesWithoutMediaInfo();
        }

        public List<string> FilterExistingFiles(List<string> files, Movie movie)
        {
            var movieFiles = GetFilesByMovie(movie.Id).Select(f => Path.Combine(movie.Path, f.RelativePath)).ToList();

            if (!movieFiles.Any())
            {
                return files;
            }

            return files.Except(movieFiles, PathEqualityComparer.Instance).ToList();
        }

        public List<MovieFile> GetMovies(IEnumerable<int> ids)
        {
            return _mediaFileRepository.Get(ids).ToList();
        }

        public MovieFile GetMovie(int id)
        {
            return _mediaFileRepository.Get(id);
        }

        public List<MovieFile> GetFilesWithRelativePath(int movieId, string relativePath)
        {
            return _mediaFileRepository.GetFilesWithRelativePath(movieId, relativePath);
        }

        public void HandleAsync(MoviesDeletedEvent message)
        {
            _mediaFileRepository.DeleteForMovies(message.Movies.Select(m => m.Id).ToList());
        }

        public static List<string> FilterExistingFiles(List<string> files, List<MovieFile> movieFiles, Movie movie)
        {
            var seriesFilePaths = movieFiles.Select(f => Path.Combine(movie.Path, f.RelativePath)).ToList();

            if (!seriesFilePaths.Any())
            {
                return files;
            }

            return files.Except(seriesFilePaths, PathEqualityComparer.Instance).ToList();
        }
    }
}
