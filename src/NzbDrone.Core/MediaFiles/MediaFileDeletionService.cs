using System;
using System.IO;
using System.Linq;
using System.Net;
using Dapper;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDeleteMediaFiles
    {
        void DeleteMovieFile(Movie movie, MovieFile movieFile);
        void DeleteUnmanagedFile(int movieId, int movieFileId);
    }

    public class MediaFileDeletionService : IDeleteMediaFiles,
                                            IHandleAsync<MoviesDeletedEvent>,
                                            IHandle<MovieFileDeletedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMovieService _movieService;
        private readonly IConfigService _configService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly ICompositeProfilePolicy _compositeProfilePolicy;
        private readonly IMainDatabase _database;

        public MediaFileDeletionService(IDiskProvider diskProvider,
                                        IRecycleBinProvider recycleBinProvider,
                                        IMediaFileService mediaFileService,
                                        IMovieService movieService,
                                        IConfigService configService,
                                        IEventAggregator eventAggregator,
                                        ICompositeProfilePolicy compositeProfilePolicy,
                                        Logger logger,
                                        IMainDatabase database)
        {
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _movieService = movieService;
            _configService = configService;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _compositeProfilePolicy = compositeProfilePolicy;
            _database = database;
        }

        public void DeleteMovieFile(Movie movie, MovieFile movieFile)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movie.Id))
            {
                movie = _movieService.GetMovie(movie.Id) ?? movie;
                _compositeProfilePolicy.EnsureLegacyMutationAllowed(new[] { movie });
                if (movieFile.IsUnmanaged)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "Delete retained unmanaged files through the explicit unmanaged-file action.");
                }

                DeleteMovieFileCore(movie, movieFile);
            }
        }

        public void DeleteUnmanagedFile(int movieId, int movieFileId)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                var movie = _movieService.GetMovie(movieId);
                var file = _mediaFileService.GetMovie(movieFileId);
                MovieProfileConversion.Require(file.MovieId == movieId && file.IsUnmanaged && movie.MovieFileId != movieFileId, "The selected file is no longer an unmanaged copy of this movie.");
                var fullPath = Path.GetFullPath(Path.Combine(movie.Path, file.RelativePath));
                MovieProfileConversion.Require(!Path.IsPathRooted(file.RelativePath) && Path.GetFullPath(movie.Path).IsParentPath(fullPath), "The file is outside the movie folder.");
                MovieProfileConversion.Require(!_mediaFileService.GetFilesByMovie(movieId).Any(f => f.Id != movieFileId && Path.GetFullPath(Path.Combine(movie.Path, f.RelativePath)).PathEquals(fullPath)), "Another file shares this path.");
                using var connection = _database.OpenConnection();
                MovieProfileConversion.CheckIdle(connection, null, movieId);
                MovieProfileConversion.Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" WHERE \"MovieFileId\"=@movieFileId AND \"Retired\"=FALSE", new { movieFileId }) == 0, "A managed version owns this file.");
                if (_diskProvider.FileExists(fullPath))
                {
                    MovieProfileConversion.Require(_diskProvider.GetFileSize(fullPath) == file.Size, "The file changed. Rescan before deleting it.");
                }

                DeleteMovieFileCore(movie, file);
            }
        }

        private void DeleteMovieFileCore(Movie movie, MovieFile movieFile)
        {
            var fullPath = Path.Combine(movie.Path, movieFile.RelativePath);
            var rootFolder = _diskProvider.GetParentFolder(movie.Path);

            if (!_diskProvider.FolderExists(rootFolder))
            {
                _logger.Warn("Movie's root folder ({0}) doesn't exist.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Movie's root folder ({0}) doesn't exist.", rootFolder);
            }

            if (_diskProvider.GetDirectories(rootFolder).Empty())
            {
                _logger.Warn("Movie's root folder ({0}) is empty. Rescan will not update movies as a failsafe.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Movie's root folder ({0}) is empty. Rescan will not update movies as a failsafe.", rootFolder);
            }

            if (_diskProvider.FolderExists(movie.Path) && _diskProvider.FileExists(fullPath))
            {
                _logger.Info("Deleting movie file: {0}", fullPath);

                var subfolder = _diskProvider.GetParentFolder(movie.Path).GetRelativePath(_diskProvider.GetParentFolder(fullPath));

                try
                {
                    _recycleBinProvider.DeleteFile(fullPath, subfolder);
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Unable to delete movie file");
                    throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete movie file");
                }
            }

            // Delete the movie file from the database to clean it up even if the file was already deleted
            _mediaFileService.Delete(movieFile, DeleteMediaFileReason.Manual);

            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        public void HandleAsync(MoviesDeletedEvent message)
        {
            if (message.DeleteFiles)
            {
                var allMovies = _movieService.AllMoviePaths();

                foreach (var movie in message.Movies)
                {
                    foreach (var s in allMovies)
                    {
                        if (s.Key == movie.Id)
                        {
                            continue;
                        }

                        if (movie.Path.IsParentPath(s.Value))
                        {
                            _logger.Error("Movie path: '{0}' is a parent of another movie, not deleting files.", movie.Path);
                            return;
                        }

                        if (movie.Path.PathEquals(s.Value))
                        {
                            _logger.Error("Movie path: '{0}' is the same as another movie, not deleting files.", movie.Path);
                            return;
                        }
                    }

                    if (_diskProvider.FolderExists(movie.Path))
                    {
                        _recycleBinProvider.DeleteFolder(movie.Path);
                    }
                }

                _eventAggregator.PublishEvent(new DeleteCompletedEvent());
            }
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(MovieFileDeletedEvent message)
        {
            if (message.ImportOperationId.HasValue)
            {
                return;
            }

            if (_configService.DeleteEmptyFolders)
            {
                var movie = message.MovieFile.Movie;
                var moviePath = movie.Path;
                var folder = message.MovieFile.Path.GetParentPath();

                while (moviePath.IsParentPath(folder))
                {
                    if (_diskProvider.FolderExists(folder))
                    {
                        _diskProvider.RemoveEmptySubfolders(folder);
                    }

                    folder = folder.GetParentPath();
                }

                _diskProvider.RemoveEmptySubfolders(moviePath);

                if (_diskProvider.FolderEmpty(moviePath))
                {
                    _diskProvider.DeleteFolder(moviePath, true);
                }
            }
        }
    }
}
