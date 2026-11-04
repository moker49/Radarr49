using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IVersionImportSidecarService
    {
        void Resume(int movieId, int operationId);
    }

    // Stops at CleaningOldExtras. Sources and all previous-file backups remain intact.
    public class VersionImportSidecarService : IVersionImportSidecarService
    {
        private readonly IVersionFileImportRepository _operations;
        private readonly IMovieService _movies;
        private readonly IMediaFileService _videos;
        private readonly IDiskProvider _disk;
        private readonly IDiskTransferService _transfer;
        private readonly IVersionImportFileVerifier _verifier;
        private readonly IRootFolderService _roots;
        private readonly IExtraFileDeletionGuard _guard;
        private readonly IExtraFileRepository<SubtitleFile> _subtitles;
        private readonly IExtraFileRepository<MetadataFile> _metadata;
        private readonly IExtraFileRepository<OtherExtraFile> _others;

        public VersionImportSidecarService(IVersionFileImportRepository operations,
                                           IMovieService movies,
                                           IMediaFileService videos,
                                           IDiskProvider disk,
                                           IDiskTransferService transfer,
                                           IVersionImportFileVerifier verifier,
                                           IRootFolderService roots,
                                           IExtraFileDeletionGuard guard,
                                           IExtraFileRepository<SubtitleFile> subtitles,
                                           IExtraFileRepository<MetadataFile> metadata,
                                           IExtraFileRepository<OtherExtraFile> others)
        {
            _operations = operations;
            _movies = movies;
            _videos = videos;
            _disk = disk;
            _transfer = transfer;
            _verifier = verifier;
            _roots = roots;
            _guard = guard;
            _subtitles = subtitles;
            _metadata = metadata;
            _others = others;
        }

        public void Resume(int movieId, int operationId)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                var operation = _operations.Get(operationId);
                Require(operation.MovieId == movieId, "The sidecar handoff belongs to another movie.");
                var movie = _movies.GetMovie(movieId);
                Require(movie.Path == operation.MoviePath, "The movie folder changed. Sidecar paths will not be adopted.");
                var root = _roots.GetBestRootFolderPath(movie.Path);
                Require(!string.IsNullOrWhiteSpace(root) && _disk.FolderExists(root) && Path.GetFullPath(root).IsParentPath(Path.GetFullPath(movie.Path)), "The configured movie root is unavailable.");

                // With no incoming sidecars this phase only advances database state.
                // Cleanup/completion still verify the video before physical actions.
                if (operation.Snapshot.IncomingExtras.Any())
                {
                    Require(Matches(Owned(movie.Path, operation.DestinationRelativePath), operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The installed video changed. Sidecars will not be modified.");
                    if (operation.Snapshot.PreviousExisted)
                    {
                        Require(Matches(Private(movie.Path, operation.BackupRelativePath), operation.Snapshot.Previous.Size, operation.Snapshot.PreviousHash), "The previous video backup changed. Sidecars will not be modified.");
                    }
                }

                _operations.BeginExtras(operationId);
                operation = _operations.Get(operationId);
                for (var index = 0; index < operation.Snapshot.IncomingExtras.Count; index++)
                {
                    RunExtra(movie, operation, index);
                    operation = _operations.Get(operationId);
                }

                if (operation.State == VersionFileImportState.Finalizing)
                {
                    _operations.FinishExtras(operationId);
                }
            }
        }

        private void RunExtra(Movie movie, VersionFileImport operation, int index)
        {
            var extra = operation.Snapshot.IncomingExtras[index];
            var destination = Owned(movie.Path, extra.DestinationRelativePath);
            var stage = Private(movie.Path, extra.StagingRelativePath);
            var backup = Private(movie.Path, extra.BackupRelativePath);
            Require(!destination.PathEquals(stage) && !destination.PathEquals(backup) && !stage.PathEquals(backup), "Sidecar handoff paths overlap.");
            Require(!_movies.AllMoviePaths().Any(m => m.Key != movie.Id && (Path.GetFullPath(m.Value).PathEquals(movie.Path) || Path.GetFullPath(m.Value).IsParentPath(destination))), "The sidecar destination overlaps another movie folder.");
            Require(!_videos.GetFilesByMovie(movie.Id).Any(f => Owned(movie.Path, f.RelativePath).PathEquals(destination)), "A video owns the sidecar destination.");
            var previous = operation.Snapshot.PreviousExtras.Where(e => Owned(movie.Path, e.RelativePath).PathEquals(destination)).ToList();
            Require(previous.All(e => !e.Preserve && string.Equals(e.RelativePath, extra.DestinationRelativePath, StringComparison.OrdinalIgnoreCase)), "A shared or aliased old sidecar cannot be replaced.");
            var old = previous.FirstOrDefault(e => e.SourceExisted);
            Require(previous.All(e => e.SourceExisted == (old != null) && (old == null || (e.Size == old.Size && e.ContentHash == old.ContentHash))), "Old sidecar snapshots conflict.");
            CheckClaims(movie, operation, extra, previous);
            if (extra.State == VersionImportExtraState.Registered)
            {
                Require(Matches(destination, extra.Size, extra.ContentHash), "The registered sidecar changed. Cleanup must remain pending.");
                VerifyBackup(backup, old);
                return;
            }

            Require(_guard.CanRecycle(movie, operation.OldMovieFileId ?? 0, extra.DestinationRelativePath), "Another owner protects the sidecar destination.");
            if (extra.State == VersionImportExtraState.Prepared)
            {
                Require(!_disk.FileExists(stage) && !_disk.FileExists(backup), "A private sidecar path was already occupied before copying.");
                VerifyOriginal(destination, old);
                Require(Matches(extra.SourcePath, extra.Size, extra.ContentHash), "The incoming sidecar source changed.");
                Step(operation, index, extra, VersionImportExtraState.Copying);
            }

            if (extra.State == VersionImportExtraState.Copying)
            {
                if (!_disk.FileExists(stage))
                {
                    Require(Matches(extra.SourcePath, extra.Size, extra.ContentHash), "The incoming sidecar source is missing or changed.");
                    _disk.CreateFolder(Path.GetDirectoryName(stage));
                    _transfer.TransferFile(extra.SourcePath, stage, TransferMode.Copy);
                }

                Require(Matches(stage, extra.Size, extra.ContentHash), "The sidecar staging copy is incomplete or changed. The previous sidecar is retained.");
                Step(operation, index, extra, VersionImportExtraState.Staged);
            }

            if (extra.State == VersionImportExtraState.Staged)
            {
                Require(Matches(stage, extra.Size, extra.ContentHash) && !_disk.FileExists(backup), "The staged sidecar changed or its backup path is occupied.");
                VerifyOriginal(destination, old);
                Step(operation, index, extra, VersionImportExtraState.BackingUp);
            }

            if (extra.State == VersionImportExtraState.BackingUp)
            {
                Require(Matches(stage, extra.Size, extra.ContentHash), "The staging sidecar changed before backup.");
                if (old != null && _disk.FileExists(destination))
                {
                    Require(!_disk.FileExists(backup), "Both old sidecar and backup exist. Neither will be overwritten.");
                    VerifyOriginal(destination, old);
                    _disk.MoveFile(destination, backup);
                }

                VerifyBackup(backup, old);
                Require(!_disk.FileExists(destination), "An unexpected file occupies the sidecar installation path.");
                Step(operation, index, extra, VersionImportExtraState.Installing);
            }

            if (extra.State == VersionImportExtraState.Installing)
            {
                if (_disk.FileExists(stage))
                {
                    Require(!_disk.FileExists(destination) && Matches(stage, extra.Size, extra.ContentHash), "Sidecar installation is ambiguous or staging changed.");
                    _disk.MoveFile(stage, destination);
                }

                Require(Matches(destination, extra.Size, extra.ContentHash), "The installed sidecar does not match its captured source.");
                VerifyBackup(backup, old);
                CheckClaims(movie, operation, extra, previous);
                _operations.RegisterExtra(operation.Id, index);
            }
        }

        private void CheckClaims(Movie movie, VersionFileImport operation, VersionImportSourceExtra extra, List<VersionFileDeletionExtra> previous)
        {
            var claims = _subtitles.GetFilesByMovie(movie.Id).Select(f => (Table: "SubtitleFiles", File: (ExtraFile)f))
                .Concat(_metadata.GetFilesByMovie(movie.Id).Select(f => (Table: "MetadataFiles", File: (ExtraFile)f)))
                .Concat(_others.GetFilesByMovie(movie.Id).Select(f => (Table: "ExtraFiles", File: (ExtraFile)f)))
                .Where(c => Owned(movie.Path, c.File.RelativePath).PathEquals(Owned(movie.Path, extra.DestinationRelativePath))).ToList();
            if (extra.State == VersionImportExtraState.Registered)
            {
                var table = extra.Subtitle == null ? "ExtraFiles" : "SubtitleFiles";
                Require(claims.Count == 1 && claims[0].Table == table && claims[0].File.Id == extra.RegisteredExtraId && claims[0].File.MovieFileId == operation.NewMovieFileId, "The registered sidecar owner changed.");
            }
            else
            {
                Require(claims.Count == previous.Count && claims.All(c => c.File.MovieFileId == operation.OldMovieFileId && previous.Any(p => p.Table == c.Table && p.Id == c.File.Id && p.RelativePath == c.File.RelativePath)), "The sidecar registrations changed. No new owner will be adopted.");
            }
        }

        private void Step(VersionFileImport operation, int index, VersionImportSourceExtra extra, VersionImportExtraState next)
        {
            _operations.AdvanceExtra(operation.Id, index, extra.State, next);
            extra.State = next;
        }

        private void VerifyOriginal(string path, VersionFileDeletionExtra old)
        {
            Require(old == null ? !_disk.FileExists(path) : Matches(path, old.Size, old.ContentHash), "The old sidecar changed or an unregistered destination appeared.");
        }

        private void VerifyBackup(string path, VersionFileDeletionExtra old)
        {
            Require(old == null ? !_disk.FileExists(path) : Matches(path, old.Size, old.ContentHash), "The sidecar backup is missing, changed or unexpectedly occupied.");
        }

        private bool Matches(string path, long size, string hash) => _disk.FileExists(path) && _disk.GetFileSize(path) == size && string.Equals(_verifier.Hash(path), hash, StringComparison.OrdinalIgnoreCase);

        private static string Private(string folder, string relative)
        {
            var path = Owned(folder, relative);
            Require(Path.Combine(folder, ".radarr-version-imports").IsParentPath(path), "Sidecar staging and backups must stay in the private import directory.");
            return path;
        }

        private static string Owned(string folder, string relative)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative), "A relative sidecar path is required.");
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            Require(Path.GetFullPath(folder).IsParentPath(path), "The sidecar path escapes the movie directory.");
            return path;
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
