using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IVersionFileDeletionRecoveryService
    {
        void Start(Movie movie, MovieFile file, bool unmonitor);
        void Resume(int movieId, int operationId, bool retryOriginalSource, bool preserveChangedExtras, string note);
        void Cancel(int movieId, int operationId, string note);
    }

    public class VersionFileDeletionRecoveryService : IVersionFileDeletionRecoveryService, IHandleAsync<ApplicationStartedEvent>
    {
        private readonly IVersionFileDeletionRepository _operations;
        private readonly IMovieService _movies;
        private readonly IMovieVersionService _versions;
        private readonly IMediaFileService _files;
        private readonly IExtraFileRepository<SubtitleFile> _subtitles;
        private readonly IExtraFileRepository<MetadataFile> _metadata;
        private readonly IExtraFileRepository<OtherExtraFile> _others;
        private readonly IExtraFileDeletionGuard _extraGuard;
        private readonly IDiskProvider _disk;
        private readonly IRecycleBinProvider _recycle;
        private readonly IEventAggregator _events;
        private readonly Logger _logger;

        public VersionFileDeletionRecoveryService(IVersionFileDeletionRepository operations,
                                                 IMovieService movies,
                                                 IMovieVersionService versions,
                                                 IMediaFileService files,
                                                 IExtraFileRepository<SubtitleFile> subtitles,
                                                 IExtraFileRepository<MetadataFile> metadata,
                                                 IExtraFileRepository<OtherExtraFile> others,
                                                 IExtraFileDeletionGuard extraGuard,
                                                 IDiskProvider disk,
                                                 IRecycleBinProvider recycle,
                                                 IEventAggregator events,
                                                 Logger logger)
        {
            _operations = operations;
            _movies = movies;
            _versions = versions;
            _files = files;
            _subtitles = subtitles;
            _metadata = metadata;
            _others = others;
            _extraGuard = extraGuard;
            _disk = disk;
            _recycle = recycle;
            _events = events;
            _logger = logger;
        }

        public void Start(Movie movie, MovieFile file, bool unmonitor)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movie.Id))
            {
                var snapshot = new VersionFileDeletionSnapshot
                {
                    File = CopyFile(file),
                    SourceExisted = _disk.FileExists(Path.Combine(movie.Path, file.RelativePath))
                };
                if (snapshot.SourceExisted)
                {
                    snapshot.SourceSize = _disk.GetFileSize(Path.Combine(movie.Path, file.RelativePath));
                    snapshot.SourceLastWrite = _disk.FileGetLastWrite(Path.Combine(movie.Path, file.RelativePath));
                    snapshot.SourceLastWriteUtcTicks = snapshot.SourceLastWrite.ToUniversalTime().Ticks;
                }

                AddExtras(snapshot, movie, file.Id, "SubtitleFiles", _subtitles.GetFilesByMovieFile(file.Id));
                AddExtras(snapshot, movie, file.Id, "MetadataFiles", _metadata.GetFilesByMovieFile(file.Id));
                AddExtras(snapshot, movie, file.Id, "ExtraFiles", _others.GetFilesByMovieFile(file.Id));
                var operation = _operations.Begin(new VersionFileDeletion
                {
                    MovieId = movie.Id, MovieVersionId = movie.ManagedVersion.Id.Value, MovieFileId = file.Id,
                    MoviePath = movie.Path, Unmonitor = unmonitor, Snapshot = snapshot
                });
                Resume(movie.Id, operation.Id, false, false, null);
            }
        }

        public void Resume(int movieId, int operationId, bool retryOriginalSource, bool preserveChangedExtras, string note)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                try
                {
                    Require(!(retryOriginalSource || preserveChangedExtras) || !string.IsNullOrWhiteSpace(note), "A verification note is required for a physical retry or preserving changed extras.");
                    Run(GetOwned(movieId, operationId), retryOriginalSource, preserveChangedExtras, note);
                }
                catch (InvalidOperationException ex)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
                }
            }
        }

        public void Cancel(int movieId, int operationId, string note)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                GetOwned(movieId, operationId);
                try
                {
                    _operations.Cancel(operationId, note);
                }
                catch (InvalidOperationException ex)
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
                }
            }
        }

        public void HandleAsync(ApplicationStartedEvent message)
        {
            foreach (var operation in _operations.Pending())
            {
                try
                {
                    Resume(operation.MovieId, operation.Id, false, false, null);
                }
                catch (Exception ex)
                {
                    _logger.Info("Version-file deletion {0} remains pending recovery: {1}", operation.Id, ex.Message);
                }
            }
        }

        private void Run(VersionFileDeletion operation, bool retryOriginal, bool preserveChangedExtras, string note)
        {
            if (operation.State == VersionFileDeletionState.Completed)
            {
                return;
            }

            Require(operation.State != VersionFileDeletionState.Cancelled, "This deletion was cancelled. Create a new exact-file request if needed.");
            var movie = _movies.GetMovie(operation.MovieId);
            Require(movie.Path == operation.MoviePath, "The movie path changed since this deletion was prepared. Reconcile it before recovery.");
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(operation.MoviePath));
            var root = Path.GetDirectoryName(folder);
            Require(!string.IsNullOrWhiteSpace(root) && _disk.FolderExists(root), "The movie root is unavailable. Recovery did not assume any files were deleted.");
            var path = OwnedPath(folder, operation.Snapshot.File.RelativePath);
            var fresh = false;
            if (operation.State is VersionFileDeletionState.Prepared or VersionFileDeletionState.Recycling)
            {
                Require(_disk.GetDirectories(root).Any(), "The movie root is empty. Verify availability before recovery.");
                Require(_versions.Get(movie, operation.MovieVersionId).MovieFileId == operation.MovieFileId &&
                    _files.GetFilesByMovie(movie.Id).Any(f => f.Id == operation.MovieFileId && f.RelativePath == operation.Snapshot.File.RelativePath),
                    "The original file assignment changed. No replacement will be recycled.");
                Require(!_files.GetFilesByMovie(movie.Id).Any(f => f.Id != operation.MovieFileId && Path.GetFullPath(Path.Combine(folder, f.RelativePath)).PathEquals(path)), "Another file now claims the source path.");
                Require(!_movies.AllMoviePaths().Any(m => m.Key != movie.Id &&
                    (Path.GetFullPath(m.Value).PathEquals(folder) || Path.GetFullPath(m.Value).PathEquals(path) || Path.GetFullPath(m.Value).IsParentPath(path))),
                    "The source path now overlaps another movie.");
                if (operation.State == VersionFileDeletionState.Prepared)
                {
                    if (_disk.FileExists(path))
                    {
                        Require(Matches(path, operation.Snapshot.SourceExisted, operation.Snapshot.SourceSize, operation.Snapshot.SourceLastWriteUtcTicks), "The source changed since deletion was prepared. Cancel this unstarted deletion and inspect the file.");
                    }

                    _operations.Advance(operation.Id, operation.State, VersionFileDeletionState.Recycling, note: note);
                    operation = _operations.Get(operation.Id);
                    fresh = true;
                }

                string recycled = null;
                if (_disk.FileExists(path))
                {
                    Require(fresh || retryOriginal, "File recycling may already have begun. Verify the original source explicitly before retrying; an existing path is never blindly recycled after restart.");
                    Require(Matches(path, operation.Snapshot.SourceExisted, operation.Snapshot.SourceSize, operation.Snapshot.SourceLastWriteUtcTicks), "The source no longer matches the original file snapshot. It will not be recycled.");
                    if (!fresh)
                    {
                        _operations.Advance(operation.Id, operation.State, operation.State, note: note);
                    }

                    recycled = _recycle.DeleteFile(path, root.GetRelativePath(Path.GetDirectoryName(path)));
                }

                _operations.Detach(operation.Id, recycled);
                operation = _operations.Get(operation.Id);
            }

            while (operation.State is VersionFileDeletionState.Detached or VersionFileDeletionState.RecyclingExtra)
            {
                if (operation.ExtraIndex == operation.Snapshot.Extras.Count)
                {
                    _operations.Advance(operation.Id, VersionFileDeletionState.Detached, VersionFileDeletionState.EventsPending);
                    operation = _operations.Get(operation.Id);
                    break;
                }

                var extra = operation.Snapshot.Extras[operation.ExtraIndex];
                if (extra.Preserve || !_operations.OwnsExtra(operation) || !_extraGuard.CanRecycle(movie, operation.MovieFileId, extra.RelativePath))
                {
                    _operations.FinishExtra(operation.Id, true);
                    operation = _operations.Get(operation.Id);
                    continue;
                }

                var extraPath = OwnedPath(folder, extra.RelativePath);
                if (_disk.FileExists(extraPath))
                {
                    if (!Matches(extraPath, extra.SourceExisted, extra.Size, extra.LastWriteUtcTicks) && preserveChangedExtras)
                    {
                        _operations.FinishExtra(operation.Id, true, "Preserved changed extra " + extra.RelativePath + ": " + note);
                        operation = _operations.Get(operation.Id);
                        continue;
                    }

                    Require(Matches(extraPath, extra.SourceExisted, extra.Size, extra.LastWriteUtcTicks), "An extra file changed. Inspect it and explicitly preserve changed extras to continue without recycling them.");
                    fresh = operation.State == VersionFileDeletionState.Detached;
                    Require(fresh || retryOriginal, "Extras recycling may already have begun. Verify the original extra file explicitly before retrying.");
                    _operations.Advance(operation.Id, operation.State, VersionFileDeletionState.RecyclingExtra, note: note);
                    _recycle.DeleteFile(extraPath, root.GetRelativePath(Path.GetDirectoryName(extraPath)));
                }

                _operations.FinishExtra(operation.Id, true);
                operation = _operations.Get(operation.Id);
            }

            if (operation.State == VersionFileDeletionState.EventsPending)
            {
                var file = CopyFile(operation.Snapshot.File);
                file.MovieVersionId = operation.MovieVersionId;
                file.Movie = movie.WithVersion(_versions.Get(movie, operation.MovieVersionId), null);
                file.Path = path;
                _events.PublishEvent(new MovieFileDeletedEvent(file, DeleteMediaFileReason.Manual) { DeletionOperationId = operation.Id });
                _events.PublishEvent(new DeleteCompletedEvent());
                _operations.Advance(operation.Id, operation.State, VersionFileDeletionState.Completed);
            }
        }

        private void AddExtras<T>(VersionFileDeletionSnapshot snapshot, Movie movie, int fileId, string table, IEnumerable<T> extras)
            where T : ExtraFile
        {
            foreach (var extra in extras)
            {
                var entry = new VersionFileDeletionExtra { Id = extra.Id, Table = table, RelativePath = extra.RelativePath, Preserve = !_extraGuard.CanRecycle(movie, fileId, extra.RelativePath) };
                if (!entry.Preserve)
                {
                    var path = OwnedPath(movie.Path, extra.RelativePath);
                    entry.SourceExisted = _disk.FileExists(path);
                    if (entry.SourceExisted)
                    {
                        entry.Size = _disk.GetFileSize(path);
                        entry.LastWrite = _disk.FileGetLastWrite(path);
                        entry.LastWriteUtcTicks = entry.LastWrite.ToUniversalTime().Ticks;
                    }
                }

                snapshot.Extras.Add(entry);
            }
        }

        private VersionFileDeletion GetOwned(int movieId, int operationId)
        {
            var row = _operations.Get(operationId);
            if (row.MovieId != movieId)
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Deletion operation does not belong to this movie.");
            }

            return row;
        }

        private bool Matches(string path, bool existed, long size, long modifiedUtcTicks) => existed && _disk.GetFileSize(path) == size && _disk.FileGetLastWrite(path).ToUniversalTime().Ticks == modifiedUtcTicks;

        private static string OwnedPath(string folder, string relative)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative), "A relative file path inside the movie folder is required.");
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            Require(Path.GetFullPath(folder).IsParentPath(path), "A deletion snapshot path escapes the movie folder.");
            return path;
        }

        private static MovieFile CopyFile(MovieFile file) => new MovieFile
        {
            Id = file.Id, MovieId = file.MovieId, MovieVersionId = file.MovieVersionId, RelativePath = file.RelativePath,
            Size = file.Size, DateAdded = file.DateAdded, OriginalFilePath = file.OriginalFilePath, SceneName = file.SceneName,
            ReleaseGroup = file.ReleaseGroup, Quality = file.Quality, IndexerFlags = file.IndexerFlags,
            MediaInfo = file.MediaInfo, Edition = file.Edition, Languages = file.Languages
        };

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
