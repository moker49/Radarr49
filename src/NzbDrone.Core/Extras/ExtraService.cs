using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Extras
{
    public interface IExtraService
    {
        void MoveFilesAfterRename(Movie movie, MovieFile movieFile);
        void ImportMovie(LocalMovie localMovie, MovieFile movieFile, bool isReadOnly);
        void CreateAfterMovieImport(Movie movie, MovieFile movieFile);
    }

    public class ExtraService : IExtraService,
                                IHandle<MediaCoversUpdatedEvent>,
                                IHandle<MovieFolderCreatedEvent>,
                                IHandle<MovieScannedEvent>,
                                IHandle<MovieRenamedEvent>
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IMovieService _movieService;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly List<IManageExtraFiles> _extraFileManagers;
        private readonly IMovieVersionService _versions;

        public ExtraService(IMediaFileService mediaFileService,
                            IMovieService movieService,
                            IDiskProvider diskProvider,
                            IConfigService configService,
                            IEnumerable<IManageExtraFiles> extraFileManagers,
                            IMovieVersionService versions,
                            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _movieService = movieService;
            _diskProvider = diskProvider;
            _configService = configService;
            _extraFileManagers = extraFileManagers.OrderBy(e => e.Order).ToList();
            _versions = versions;
        }

        public void ImportMovie(LocalMovie localMovie, MovieFile movieFile, bool isReadOnly)
        {
            lock (MovieVersionImportSynchronization.ForMovie(localMovie.Movie.Id))
            {
                ImportExtraFiles(localMovie, movieFile, isReadOnly);

                CreateAfterMovieImport(localMovie.Movie, movieFile);
            }
        }

        public void ImportExtraFiles(LocalMovie localMovie, MovieFile movieFile, bool isReadOnly)
        {
            if (!_configService.ImportExtraFiles)
            {
                return;
            }

            var folderSearchOption = localMovie.FolderMovieInfo != null;

            var wantedExtensions = _configService.ExtraFileExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                                     .Select(e => e.Trim(' ', '.')
                                                                     .Insert(0, "."))
                                                                     .ToList();

            var sourceFolder = _diskProvider.GetParentFolder(localMovie.Path);
            var files = _diskProvider.GetFiles(sourceFolder, folderSearchOption);
            var managedFiles = _extraFileManagers.Select((i) => new List<string>()).ToArray();

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                var matchingExtension = wantedExtensions.FirstOrDefault(e => e.Equals(extension));

                if (matchingExtension == null)
                {
                    continue;
                }

                for (var i = 0; i < _extraFileManagers.Count; i++)
                {
                    if (_extraFileManagers[i].CanImportFile(localMovie, movieFile, file, extension, isReadOnly))
                    {
                        managedFiles[i].Add(file);
                        break;
                    }
                }
            }

            for (var i = 0; i < _extraFileManagers.Count; i++)
            {
                _extraFileManagers[i].ImportFiles(localMovie, movieFile, managedFiles[i], isReadOnly);
            }
        }

        public void CreateAfterMovieImport(Movie movie, MovieFile movieFile)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movie.Id))
            {
                if (movie.ManagedVersion?.Id is int versionId)
                {
                    var current = _versions.ForVersion(_movieService.GetMovie(movie.Id), versionId);
                    if (current.MovieFileId != movieFile.Id)
                    {
                        throw new InvalidOperationException("The managed file assignment changed before metadata generation.");
                    }
                }

                foreach (var extraFileManager in _extraFileManagers)
                {
                    extraFileManager.CreateAfterMovieImport(movie, movieFile);
                }
            }
        }

        public void Handle(MediaCoversUpdatedEvent message)
        {
            if (message.Updated)
            {
                WithMovieLock(message.Movie, movie =>
                {
                    foreach (var extraFileManager in _extraFileManagers)
                    {
                        extraFileManager.CreateAfterMediaCoverUpdate(movie);
                    }
                });
            }
        }

        public void Handle(MovieScannedEvent message)
        {
            WithMovieLock(message.Movie, movie =>
            {
                var movieFiles = GetMovieFiles(movie);
                foreach (var extraFileManager in _extraFileManagers)
                {
                    extraFileManager.CreateAfterMovieScan(movie, movieFiles);
                }
            });
        }

        public void Handle(MovieFolderCreatedEvent message)
        {
            WithMovieLock(message.Movie, movie =>
            {
                foreach (var extraFileManager in _extraFileManagers)
                {
                    extraFileManager.CreateAfterMovieFolder(movie, message.MovieFolder);
                }
            });
        }

        public void MoveFilesAfterRename(Movie movie, MovieFile movieFile)
        {
            WithMovieLock(movie, current =>
            {
                var movieFiles = new List<MovieFile> { movieFile };
                foreach (var extraFileManager in _extraFileManagers)
                {
                    extraFileManager.MoveFilesAfterRename(current, movieFiles);
                }
            });
        }

        public void Handle(MovieRenamedEvent message)
        {
            WithMovieLock(message.Movie, movie =>
            {
                var movieFiles = GetMovieFiles(movie);
                foreach (var extraFileManager in _extraFileManagers)
                {
                    extraFileManager.MoveFilesAfterRename(movie, movieFiles);
                }
            });
        }

        private void WithMovieLock(Movie movie, Action<Movie> action)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movie.Id))
            {
                action(_movieService.GetMovie(movie.Id) ?? movie);
            }
        }

        private List<MovieFile> GetMovieFiles(Movie movie)
        {
            var movieFiles = _mediaFileService.GetFilesByMovie(movie.Id).Where(f => !f.IsUnmanaged).ToList();
            var versions = _versions.Resolve(movie);

            foreach (var movieFile in movieFiles)
            {
                var version = versions?.SingleOrDefault(v => v.Id.HasValue && v.MovieFileId == movieFile.Id);
                movieFile.MovieVersionId = version?.Id;
                movieFile.Movie = version == null ? movie : movie.WithVersion(version, movieFile);
            }

            return movieFiles;
        }
    }
}
