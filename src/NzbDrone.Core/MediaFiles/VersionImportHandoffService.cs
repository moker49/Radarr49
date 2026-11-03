using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    // This primitive intentionally stops at Registered. Runtime routing is deferred until
    // durable extras, source cleanup and event finalization can complete the operation.
    public interface IVersionImportHandoffService
    {
        VersionFileImport Prepare(LocalMovie local, MovieFile incoming, bool copyOnly);
        MovieFile Resume(int movieId, int operationId);
        void CancelBeforeBackup(int movieId, int operationId, string note);
    }

    public class VersionImportHandoffService : IVersionImportHandoffService
    {
        private readonly IVersionFileImportRepository _operations;
        private readonly IMovieService _movies;
        private readonly IMovieVersionService _versions;
        private readonly IMediaFileService _files;
        private readonly IMovieVersionImportService _validation;
        private readonly ManagedVersionProfileSpecification _profile;
        private readonly ICustomFormatCalculationService _formats;
        private readonly IDiskProvider _disk;
        private readonly IDiskTransferService _transfer;
        private readonly IVersionImportFileVerifier _verifier;
        private readonly IRootFolderService _roots;
        private readonly IConfigService _config;
        private readonly IExtraFileRepository<SubtitleFile> _subtitles;
        private readonly IExtraFileRepository<MetadataFile> _metadata;
        private readonly IExtraFileRepository<OtherExtraFile> _others;
        private readonly IExtraFileDeletionGuard _extraGuard;

        public VersionImportHandoffService(IVersionFileImportRepository operations,
                                          IMovieService movies,
                                          IMovieVersionService versions,
                                          IMediaFileService files,
                                          IMovieVersionImportService validation,
                                          ManagedVersionProfileSpecification profile,
                                          ICustomFormatCalculationService formats,
                                          IDiskProvider disk,
                                          IDiskTransferService transfer,
                                          IVersionImportFileVerifier verifier,
                                          IRootFolderService roots,
                                          IConfigService config,
                                          IExtraFileRepository<SubtitleFile> subtitles,
                                          IExtraFileRepository<MetadataFile> metadata,
                                          IExtraFileRepository<OtherExtraFile> others,
                                          IExtraFileDeletionGuard extraGuard)
        {
            _operations = operations;
            _movies = movies;
            _versions = versions;
            _files = files;
            _validation = validation;
            _profile = profile;
            _formats = formats;
            _disk = disk;
            _transfer = transfer;
            _verifier = verifier;
            _roots = roots;
            _config = config;
            _subtitles = subtitles;
            _metadata = metadata;
            _others = others;
            _extraGuard = extraGuard;
        }

        public VersionFileImport Prepare(LocalMovie local, MovieFile incoming, bool copyOnly)
        {
            lock (MovieVersionImportSynchronization.ForMovie(local.Movie.Id))
            {
                _validation.ValidateForWrite(local, local.DownloadItem, true);
                Require(local.MovieVersionId > 0 && incoming.Id == 0 && incoming.MovieId == local.Movie.Id, "An explicit managed version and new incoming file are required.");
                var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(local.Movie.Path));
                var source = Path.GetFullPath(local.Path);
                Require(!_files.GetFilesByMovie(local.Movie.Id).Any(f => Path.GetFullPath(Path.Combine(folder, f.RelativePath)).PathEquals(source)), "The source already belongs to a registered movie file.");
                Require(!_movies.AllMoviePaths().Any(m => m.Key != local.Movie.Id && Path.GetFullPath(m.Value).IsParentPath(source)), "The source belongs to another movie folder.");
                var destination = MovieVersionImportService.VersionFileName(local.Movie) + Path.GetExtension(source);
                Require(!source.PathEquals(Path.Combine(folder, destination)), "The source is already at the managed destination.");
                var area = Path.Combine(".radarr-version-imports", Guid.NewGuid().ToString("N"));
                var snapshot = new VersionFileImportSnapshot
                {
                    Incoming = Copy(incoming), Previous = local.Movie.MovieFile == null ? null : Copy(local.Movie.MovieFile),
                    FileMovieInfo = local.FileMovieInfo, FolderMovieInfo = local.FolderMovieInfo, SceneSource = local.SceneSource,
                    DownloadClientInfo = local.DownloadItem?.DownloadClientInfo, DownloadId = local.DownloadItem?.DownloadId,
                    CustomFormatScore = local.CustomFormatScore, Release = local.Release,
                    CustomFormats = (local.CustomFormats ?? new List<CustomFormat>()).Select(f => new VersionImportFormat { Id = f.Id, Name = f.Name }).ToList()
                };
                snapshot.Incoming.Quality = local.Quality;
                snapshot.Incoming.Languages = local.Languages;
                snapshot.Incoming.MediaInfo = local.MediaInfo;
                snapshot.Incoming.ReleaseGroup = local.ReleaseGroup;
                snapshot.Incoming.Edition = local.Edition;
                snapshot.Incoming.SceneName = local.SceneName;
                snapshot.Incoming.Size = _disk.GetFileSize(source);
                var sourceModified = _disk.FileGetLastWrite(source);
                snapshot.SourceHash = _verifier.Hash(source);
                Require(snapshot.Incoming.Size > 0 && _disk.GetFileSize(source) == snapshot.Incoming.Size && _disk.FileGetLastWrite(source) == sourceModified, "The incoming source changed while its identity was captured.");
                if (snapshot.Previous != null)
                {
                    var previous = OwnedPath(folder, snapshot.Previous.RelativePath);
                    snapshot.PreviousExisted = _disk.FileExists(previous);
                    if (snapshot.PreviousExisted)
                    {
                        snapshot.Previous.Size = _disk.GetFileSize(previous);
                        snapshot.PreviousHash = _verifier.Hash(previous);
                    }

                    CapturePreviousExtras(snapshot, local.Movie, snapshot.Previous.Id, "SubtitleFiles", _subtitles.GetFilesByMovieFile(snapshot.Previous.Id) ?? new List<SubtitleFile>());
                    CapturePreviousExtras(snapshot, local.Movie, snapshot.Previous.Id, "MetadataFiles", _metadata.GetFilesByMovieFile(snapshot.Previous.Id) ?? new List<MetadataFile>());
                    CapturePreviousExtras(snapshot, local.Movie, snapshot.Previous.Id, "ExtraFiles", _others.GetFilesByMovieFile(snapshot.Previous.Id) ?? new List<OtherExtraFile>());
                }

                if (_config.ImportExtraFiles)
                {
                    var extensions = (_config.ExtraFileExtensions ?? string.Empty).Split(',').Select(e => "." + e.Trim(' ', '.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var extra in _disk.GetFiles(Path.GetDirectoryName(source), false).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!extensions.Contains(Path.GetExtension(extra)) || MediaFileExtensions.Extensions.Contains(Path.GetExtension(extra)) || !ExtraFileVersionService.Matches(source, extra))
                        {
                            continue;
                        }

                        snapshot.IncomingExtras.Add(new VersionImportSourceExtra { SourcePath = Path.GetFullPath(extra), Size = _disk.GetFileSize(extra), LastWriteUtcTicks = _disk.FileGetLastWrite(extra).ToUniversalTime().Ticks, ContentHash = _verifier.Hash(extra) });
                    }
                }

                return _operations.Begin(new VersionFileImport
                {
                    MovieId = local.Movie.Id, MovieVersionId = local.MovieVersionId.Value, OldMovieFileId = snapshot.Previous?.Id,
                    DownloadReservationId = local.DownloadReservationId, MoviePath = local.Movie.Path, SourcePath = source,
                    DestinationRelativePath = destination, StagingRelativePath = Path.Combine(area, "incoming" + Path.GetExtension(source)),
                    BackupRelativePath = snapshot.Previous == null ? null : Path.Combine(area, "previous" + Path.GetExtension(snapshot.Previous.RelativePath)),
                    CopyOnly = copyOnly, Snapshot = snapshot
                });
            }
        }

        public MovieFile Resume(int movieId, int operationId)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                var operation = GetOwned(movieId, operationId);
                Require(operation.State != VersionFileImportState.Cancelled, "This import was cancelled.");
                Require(operation.State <= VersionFileImportState.Registered, "This operation has moved beyond its main-file handoff.");
                var movie = _movies.GetMovie(movieId);
                Require(movie.Path == operation.MoviePath, "The movie path changed. No handoff paths were adopted.");
                var root = _roots.GetBestRootFolderPath(movie.Path);
                Require(!string.IsNullOrWhiteSpace(root) && _disk.FolderExists(root) && Path.GetFullPath(root).IsParentPath(Path.GetFullPath(movie.Path)), "The configured movie root is unavailable or does not own this folder.");
                var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(movie.Path));
                var stage = PrivatePath(folder, operation.StagingRelativePath);
                var backup = operation.BackupRelativePath == null ? null : PrivatePath(folder, operation.BackupRelativePath);
                var destination = OwnedPath(folder, operation.DestinationRelativePath);
                var previous = operation.Snapshot.Previous == null ? null : OwnedPath(folder, operation.Snapshot.Previous.RelativePath);
                CheckPaths(movie, operation, destination, previous);
                if (operation.State == VersionFileImportState.Registered)
                {
                    Require(Matches(destination, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The registered destination changed; finalization must remain pending.");
                    return _operations.Register(operation.Id);
                }

                ValidateProfile(movie, operation);
                Require(_versions.Get(movie, operation.MovieVersionId).MovieFileId == operation.OldMovieFileId, "The target assignment changed. A replacement will not be adopted.");
                if (operation.State == VersionFileImportState.Prepared)
                {
                    Require(!_disk.FileExists(stage), "The private staging path already exists before copying began.");
                    Require(Matches(operation.SourcePath, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The original incoming source changed.");
                    _operations.Advance(operation.Id, operation.State, VersionFileImportState.Staging);
                    operation = _operations.Get(operation.Id);
                }

                if (operation.State == VersionFileImportState.Staging)
                {
                    if (!_disk.FileExists(stage))
                    {
                        Require(Matches(operation.SourcePath, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The source is missing or changed; no staging copy was attempted.");
                        _disk.CreateFolder(Path.GetDirectoryName(stage));
                        _transfer.TransferFile(operation.SourcePath, stage, TransferMode.Copy);
                    }

                    Require(Matches(stage, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The staging file is incomplete or changed. The original version has not been moved; inspect or cancel this operation.");
                    _operations.Advance(operation.Id, operation.State, VersionFileImportState.Staged);
                    operation = _operations.Get(operation.Id);
                }

                if (operation.State == VersionFileImportState.Staged)
                {
                    Require(Matches(stage, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The verified staging file changed before backup.");
                    Require(backup == null || !_disk.FileExists(backup), "The private backup path is already occupied.");
                    ValidateProfile(movie, operation);
                    _operations.Advance(operation.Id, operation.State, VersionFileImportState.BackingUp);
                    operation = _operations.Get(operation.Id);
                }

                if (operation.State == VersionFileImportState.BackingUp)
                {
                    Require(Matches(stage, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The incoming staging file is not verified; the previous version will not be moved.");
                    if (operation.Snapshot.PreviousExisted)
                    {
                        if (_disk.FileExists(previous))
                        {
                            Require(!_disk.FileExists(backup), "Both the original and backup paths exist. Inspect the handoff instead of overwriting either.");
                            Require(Matches(previous, operation.Snapshot.Previous.Size, operation.Snapshot.PreviousHash), "The previous file changed; it will not be backed up.");
                            _disk.CreateFolder(Path.GetDirectoryName(backup));
                            _disk.MoveFile(previous, backup);
                        }

                        Require(Matches(backup, operation.Snapshot.Previous.Size, operation.Snapshot.PreviousHash), "The original version is not present in its verified backup.");
                    }
                    else
                    {
                        Require(previous == null || !_disk.FileExists(previous), "A file appeared at the originally missing previous path. It will not be overwritten.");
                    }

                    Require(!_disk.FileExists(destination), "A file now occupies the installation destination. No overwrite was authorized.");
                    _operations.Advance(operation.Id, operation.State, VersionFileImportState.Installing);
                    operation = _operations.Get(operation.Id);
                }

                if (operation.State == VersionFileImportState.Installing)
                {
                    if (_disk.FileExists(stage))
                    {
                        Require(!_disk.FileExists(destination), "Both staging and destination exist. Inspect the installation before proceeding.");
                        Require(Matches(stage, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The staging file changed before installation.");
                        _disk.MoveFile(stage, destination);
                    }

                    Require(Matches(destination, operation.Snapshot.Incoming.Size, operation.Snapshot.SourceHash), "The installed destination does not match the verified source.");
                    if (operation.Snapshot.PreviousExisted)
                    {
                        Require(Matches(backup, operation.Snapshot.Previous.Size, operation.Snapshot.PreviousHash), "The previous backup changed before registration.");
                    }

                    ValidateProfile(movie, operation);
                    return _operations.Register(operation.Id);
                }

                throw new InvalidOperationException("Unsupported handoff checkpoint.");
            }
        }

        public void CancelBeforeBackup(int movieId, int operationId, string note)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                GetOwned(movieId, operationId);

                // Private staging files are deliberately retained for inspection, not
                // blindly deleted. The directory is excluded from ordinary media scans.
                _operations.CancelBeforeBackup(operationId, note);
            }
        }

        private void ValidateProfile(Movie root, VersionFileImport operation)
        {
            var version = _versions.Get(root, operation.MovieVersionId);
            var incoming = operation.Snapshot.Incoming;
            var local = new LocalMovie
            {
                Movie = root.WithVersion(version, null), MovieVersionId = version.Id, Path = operation.SourcePath,
                Size = incoming.Size, Quality = incoming.Quality, Languages = incoming.Languages,
                MediaInfo = incoming.MediaInfo, ReleaseGroup = incoming.ReleaseGroup, Edition = incoming.Edition,
                FileMovieInfo = operation.Snapshot.FileMovieInfo, FolderMovieInfo = operation.Snapshot.FolderMovieInfo,
                SceneName = incoming.SceneName, SceneSource = operation.Snapshot.SceneSource
            };
            local.CustomFormats = _formats.ParseCustomFormat(local);
            local.CustomFormatScore = local.Movie.QualityProfile.CalculateCustomFormatScore(local.CustomFormats);
            var decision = _profile.IsSatisfiedBy(local, null);
            Require(decision.Accepted, decision.Message);
        }

        private void CheckPaths(Movie movie, VersionFileImport operation, string destination, string previous)
        {
            Require(!_movies.AllMoviePaths().Any(m => m.Key != movie.Id &&
                (Path.GetFullPath(m.Value).PathEquals(movie.Path) || Path.GetFullPath(m.Value).IsParentPath(destination))),
                "The destination overlaps another movie folder.");
            Require(!_files.GetFilesByMovie(movie.Id).Any(f => f.Id != operation.OldMovieFileId && f.Id != operation.NewMovieFileId &&
                (Path.GetFullPath(Path.Combine(movie.Path, f.RelativePath)).PathEquals(destination) || (previous != null && Path.GetFullPath(Path.Combine(movie.Path, f.RelativePath)).PathEquals(previous)))),
                "Another file claims a handoff path.");
        }

        private bool Matches(string path, long size, string hash) => _disk.FileExists(path) && _disk.GetFileSize(path) == size && string.Equals(_verifier.Hash(path), hash, StringComparison.OrdinalIgnoreCase);

        private void CapturePreviousExtras<T>(VersionFileImportSnapshot snapshot, Movie movie, int fileId, string table, IEnumerable<T> extras)
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
                        entry.LastWriteUtcTicks = _disk.FileGetLastWrite(path).ToUniversalTime().Ticks;
                        entry.ContentHash = _verifier.Hash(path);
                    }
                }

                snapshot.PreviousExtras.Add(entry);
            }
        }

        private VersionFileImport GetOwned(int movieId, int operationId)
        {
            var operation = _operations.Get(operationId);
            Require(operation.MovieId == movieId, "The import operation belongs to another movie.");
            return operation;
        }

        private static string PrivatePath(string folder, string relative)
        {
            var path = OwnedPath(folder, relative);
            Require(Path.Combine(folder, ".radarr-version-imports").IsParentPath(path), "Staging and backup paths must remain inside the private import directory.");
            return path;
        }

        private static string OwnedPath(string folder, string relative)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative), "A relative path inside the movie directory is required.");
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            Require(Path.GetFullPath(folder).IsParentPath(path), "The handoff path escapes the movie directory.");
            return path;
        }

        private static MovieFile Copy(MovieFile file) => new MovieFile
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
