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
    public interface IVersionImportCleanupService
    {
        void Resume(int movieId, int operationId, bool retryPhysical = false, bool preserveChangedExtras = false, bool preserveChangedSources = false, string note = null);
    }

    // Stops at EventsPending; notification/history delivery and runtime routing are separate.
    public class VersionImportCleanupService : IVersionImportCleanupService
    {
        private readonly IVersionFileImportRepository _operations;
        private readonly IMovieService _movies;
        private readonly IMediaFileService _videos;
        private readonly IDiskProvider _disk;
        private readonly IVersionImportFileVerifier _verifier;
        private readonly IRootFolderService _roots;
        private readonly IRecycleBinProvider _recycle;
        private readonly IExtraFileDeletionGuard _guard;
        private readonly IExtraFileRepository<SubtitleFile> _subtitles;
        private readonly IExtraFileRepository<MetadataFile> _metadata;
        private readonly IExtraFileRepository<OtherExtraFile> _others;

        public VersionImportCleanupService(IVersionFileImportRepository operations,
                                           IMovieService movies,
                                           IMediaFileService videos,
                                           IDiskProvider disk,
                                           IVersionImportFileVerifier verifier,
                                           IRootFolderService roots,
                                           IRecycleBinProvider recycle,
                                           IExtraFileDeletionGuard guard,
                                           IExtraFileRepository<SubtitleFile> subtitles,
                                           IExtraFileRepository<MetadataFile> metadata,
                                           IExtraFileRepository<OtherExtraFile> others)
        {
            _operations = operations;
            _movies = movies;
            _videos = videos;
            _disk = disk;
            _verifier = verifier;
            _roots = roots;
            _recycle = recycle;
            _guard = guard;
            _subtitles = subtitles;
            _metadata = metadata;
            _others = others;
        }

        public void Resume(int movieId, int operationId, bool retryPhysical = false, bool preserveChangedExtras = false, bool preserveChangedSources = false, string note = null)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                Require(!(retryPhysical || preserveChangedExtras || preserveChangedSources) || !string.IsNullOrWhiteSpace(note), "A verification note is required for physical retries or preservation overrides.");
                var operation = _operations.Get(operationId);
                Require(operation.MovieId == movieId, "The cleanup operation belongs to another movie.");
                var movie = _movies.GetMovie(movieId);
                Require(movie.Path == operation.MoviePath, "The movie folder changed. Cleanup paths will not be adopted.");
                var root = _roots.GetBestRootFolderPath(movie.Path);
                Require(!string.IsNullOrWhiteSpace(root) && _disk.FolderExists(root) && Path.GetFullPath(root).IsParentPath(Path.GetFullPath(movie.Path)), "The configured movie root is unavailable.");
                if (operation.State == VersionFileImportState.EventsPending)
                {
                    VerifyInstalled(movie, operation);
                }

                _operations.BeginCleanup(operationId);
                operation = _operations.Get(operationId);
                while (operation.State is VersionFileImportState.CleaningOldExtras or VersionFileImportState.RecyclingBackup or VersionFileImportState.RemovingSource)
                {
                    // Bound full-video hashing to phase boundaries instead of repeating
                    // it for every sidecar; the movie mutation lock spans the whole run.
                    VerifyInstalled(movie, operation);
                    for (var index = 0; index < operation.Snapshot.Cleanup.Count; index++)
                    {
                        var item = operation.Snapshot.Cleanup[index];
                        if (item.State == VersionImportCleanupState.Finished || VersionImportCleanupPlan.Phase(item.Kind) != operation.State)
                        {
                            continue;
                        }

                        RunItem(movie, operation, index, retryPhysical, preserveChangedExtras, preserveChangedSources, note);
                        operation = _operations.Get(operationId);
                    }

                    var next = operation.State switch
                    {
                        VersionFileImportState.CleaningOldExtras => VersionFileImportState.RecyclingBackup,
                        VersionFileImportState.RecyclingBackup => VersionFileImportState.RemovingSource,
                        VersionFileImportState.RemovingSource => VersionFileImportState.EventsPending,
                        _ => throw new InvalidOperationException("Unknown import cleanup phase.")
                    };
                    _operations.AdvanceCleanup(operationId, operation.State, next);
                    operation = _operations.Get(operationId);
                }
            }
        }

        private void RunItem(Movie movie, VersionFileImport operation, int index, bool retryPhysical, bool preserveChangedExtras, bool preserveChangedSources, string note)
        {
            var item = operation.Snapshot.Cleanup[index];
            if (item.Kind == VersionImportCleanupKind.OldExtra && (item.Preserve || !OwnsOldExtra(movie, operation, item) || !_guard.CanRecycle(movie, operation.OldMovieFileId ?? 0, item.Path)))
            {
                Finish(operation, index, item, true, null, "Retained protected/replaced extra; removed only its exact previous registration.");
                return;
            }

            var path = CleanupPath(movie, item);
            Require(!InstalledPaths(movie, operation).Any(p => p.PathEquals(path)), "Cleanup would touch an installed replacement.");
            Require(!_movies.AllMoviePaths().Any(m => m.Key != movie.Id && (Path.GetFullPath(m.Value).PathEquals(Path.GetFullPath(movie.Path)) || Path.GetFullPath(m.Value).IsParentPath(path))), "The cleanup path belongs to another movie folder.");
            var claimed = _videos.GetFilesByMovie(movie.Id).Any(f => Owned(movie.Path, f.RelativePath).PathEquals(path)) ||
                AllExtras(movie).Any(e => !(item.Kind == VersionImportCleanupKind.OldExtra && e.File.MovieFileId == operation.OldMovieFileId && operation.Snapshot.Cleanup.Any(c => c.Kind == VersionImportCleanupKind.OldExtra && c.Table == e.Table && c.ExtraId == e.File.Id && c.Path == e.File.RelativePath)) && Owned(movie.Path, e.File.RelativePath).PathEquals(path));
            if (claimed)
            {
                if (item.Kind == VersionImportCleanupKind.OldExtra)
                {
                    Finish(operation, index, item, true, null, "Retained extra protected by another registration.");
                    return;
                }

                Require(item.Kind == VersionImportCleanupKind.Source && preserveChangedSources, "A current registration protects the cleanup path.");
                Finish(operation, index, item, true, null, "Preserved newly claimed source: " + note);
                return;
            }

            if (!_disk.FileExists(path))
            {
                Finish(operation, index, item, false, null, note);
                return;
            }

            if (!item.Existed || !Matches(path, item.Size, item.ContentHash))
            {
                var preserve = (item.Kind == VersionImportCleanupKind.OldExtra && preserveChangedExtras) || (item.Kind == VersionImportCleanupKind.Source && preserveChangedSources);
                Require(preserve, "A cleanup file changed or appeared after capture. It will not be recycled.");
                Finish(operation, index, item, true, null, "Preserved changed cleanup file: " + note);
                return;
            }

            Require(item.State == VersionImportCleanupState.Prepared || retryPhysical, "Recycling may already have begun. Verify the existing file and explicitly retry physical cleanup.");
            _operations.StartCleanupItem(operation.Id, index, note);
            Require(Matches(path, item.Size, item.ContentHash), "The cleanup file changed after its recycling marker was persisted.");
            var subfolder = Path.Combine(Path.GetFileName(Path.TrimEndingDirectorySeparator(movie.Path)), ".radarr-import-" + operation.Id, item.Kind.ToString());
            var recycled = _recycle.DeleteFile(path, subfolder);
            Require(!_disk.FileExists(path), "The recycle provider returned while the original cleanup path still exists.");
            item.State = VersionImportCleanupState.Recycling;
            Finish(operation, index, item, false, recycled, note);
        }

        private void VerifyInstalled(Movie movie, VersionFileImport operation)
        {
            Require(Matches(Owned(movie.Path, operation.DestinationRelativePath), operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The installed video changed. Cleanup remains blocked.");
            foreach (var extra in operation.Snapshot.IncomingExtras)
            {
                Require(extra.State == VersionImportExtraState.Registered && Matches(Owned(movie.Path, extra.DestinationRelativePath), extra.Size, extra.ContentHash), "An installed sidecar is incomplete or changed. Cleanup remains blocked.");
            }
        }

        private static IEnumerable<string> InstalledPaths(Movie movie, VersionFileImport operation) =>
            operation.Snapshot.IncomingExtras.Select(e => Owned(movie.Path, e.DestinationRelativePath)).Prepend(Owned(movie.Path, operation.DestinationRelativePath));

        private bool OwnsOldExtra(Movie movie, VersionFileImport operation, VersionImportCleanupItem item) =>
            AllExtras(movie).Any(e => e.Table == item.Table && e.File.Id == item.ExtraId && e.File.MovieFileId == operation.OldMovieFileId && e.File.RelativePath == item.Path);

        private IEnumerable<(string Table, ExtraFile File)> AllExtras(Movie movie) =>
            _subtitles.GetFilesByMovie(movie.Id).Select(f => ("SubtitleFiles", (ExtraFile)f))
                .Concat(_metadata.GetFilesByMovie(movie.Id).Select(f => ("MetadataFiles", (ExtraFile)f)))
                .Concat(_others.GetFilesByMovie(movie.Id).Select(f => ("ExtraFiles", (ExtraFile)f)));

        private void Finish(VersionFileImport operation, int index, VersionImportCleanupItem item, bool preserve, string recyclePath, string note) =>
            _operations.FinishCleanupItem(operation.Id, index, item.State, preserve, recyclePath, note);

        private bool Matches(string path, long size, string hash) => _disk.FileExists(path) && _disk.GetFileSize(path) == size && string.Equals(_verifier.Hash(path), hash, StringComparison.OrdinalIgnoreCase);

        private static string CleanupPath(Movie movie, VersionImportCleanupItem item)
        {
            if (item.Kind == VersionImportCleanupKind.Source)
            {
                Require(Path.IsPathRooted(item.Path), "A captured absolute source path is required.");
                var source = Path.GetFullPath(item.Path);
                Require(!Path.Combine(movie.Path, ".radarr-version-imports").IsParentPath(source), "An import source cannot remove private journal files.");
                return source;
            }

            var path = Owned(movie.Path, item.Path);
            if (item.Kind == VersionImportCleanupKind.Backup)
            {
                Require(Path.Combine(movie.Path, ".radarr-version-imports").IsParentPath(path), "A backup cleanup path must stay in the private import directory.");
            }

            return path;
        }

        private static string Owned(string folder, string relative)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative), "A relative path inside the movie folder is required.");
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            Require(Path.GetFullPath(folder).IsParentPath(path), "A cleanup path escapes the movie folder.");
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
