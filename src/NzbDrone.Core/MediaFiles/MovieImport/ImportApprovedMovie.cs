using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IImportApprovedMovie
    {
        List<ImportResult> Import(List<ImportDecision> decisions, bool newDownload, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto);
    }

    public class ImportApprovedMovie : IImportApprovedMovie
    {
        private readonly IUpgradeMediaFiles _movieFileUpgrader;
        private readonly IMediaFileService _mediaFileService;
        private readonly IExtraService _extraService;
        private readonly IExistingExtraFiles _existingExtraFiles;
        private readonly IDiskProvider _diskProvider;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;
        private readonly IMovieVersionImportService _versionImports;
        private readonly IManagedMovieImportService _managedImports;

        public ImportApprovedMovie(IUpgradeMediaFiles movieFileUpgrader,
                                   IMediaFileService mediaFileService,
                                   IExtraService extraService,
                                   IExistingExtraFiles existingExtraFiles,
                                   IDiskProvider diskProvider,
                                   IHistoryService historyService,
                                   IEventAggregator eventAggregator,
                                   IManageCommandQueue commandQueueManager,
                                   Logger logger,
                                   IMovieVersionImportService versionImports,
                                   IManagedMovieImportService managedImports)
        {
            _movieFileUpgrader = movieFileUpgrader;
            _mediaFileService = mediaFileService;
            _extraService = extraService;
            _existingExtraFiles = existingExtraFiles;
            _diskProvider = diskProvider;
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
            _versionImports = versionImports;
            _managedImports = managedImports;
        }

        public List<ImportResult> Import(List<ImportDecision> decisions, bool newDownload, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto)
        {
            _logger.Debug("Decisions: {0}", decisions.Count);

            // I added a null op for the rare case that the quality is null. TODO: find out why that would even happen in the first place.
            var qualifiedImports = decisions
                .Where(decision => decision.Approved)
                .GroupBy(decision => (decision.LocalMovie.Movie.Id, decision.LocalMovie.MovieVersionId))
                .SelectMany(group => group
                    .OrderByDescending(decision => decision.LocalMovie.Quality ?? new QualityModel { Quality = Quality.Unknown }, new QualityModelComparer(group.First().LocalMovie.Movie.QualityProfile))
                    .ThenByDescending(decision => decision.LocalMovie.Size))
                .ToList();

            var importResults = new List<ImportResult>();

            foreach (var importDecision in qualifiedImports.OrderByDescending(e => e.LocalMovie.Size))
            {
                var localMovie = importDecision.LocalMovie;
                var oldFiles = new List<DeletedMovieFile>();

                lock (MovieVersionImportSynchronization.ForMovie(localMovie.Movie.Id))
                {
                    try
                    {
                        localMovie.DownloadItem = downloadClientItem ?? localMovie.DownloadItem;
                        _versionImports.ValidateForWrite(localMovie, localMovie.DownloadItem, newDownload);
                        var sourceFiles = _mediaFileService.GetFilesWithRelativePath(localMovie.Movie.Id, Path.GetRelativePath(localMovie.Movie.Path, localMovie.Path));
                        if (sourceFiles?.Any(f => f.IsUnmanaged) == true)
                        {
                            throw new MovieVersionImportException("This source is a retained unmanaged copy. Change its management through profile conversion before importing it.");
                        }

                        // check if already imported
                        if (importResults.Any(r => r.Result == ImportResultType.Imported && r.ImportDecision.LocalMovie.Movie.Id == localMovie.Movie.Id &&
                            r.ImportDecision.LocalMovie.MovieVersionId == localMovie.MovieVersionId))
                        {
                            importResults.Add(new ImportResult(importDecision, "Movie has already been imported"));
                            continue;
                        }

                        var movieFile = new MovieFile();
                        movieFile.MovieVersionId = localMovie.MovieVersionId;
                        movieFile.DateAdded = DateTime.UtcNow;
                        movieFile.MovieId = localMovie.Movie.Id;
                        movieFile.Path = localMovie.Path.CleanFilePath();
                        movieFile.Size = _diskProvider.GetFileSize(localMovie.Path);
                        movieFile.Quality = localMovie.Quality;
                        movieFile.Languages = localMovie.Languages;
                        movieFile.MediaInfo = localMovie.MediaInfo;
                        movieFile.Movie = localMovie.Movie;
                        movieFile.ReleaseGroup = localMovie.ReleaseGroup;
                        movieFile.Edition = localMovie.Edition;

                        var client = localMovie.DownloadItem;
                        if (localMovie.MovieVersionId.HasValue)
                        {
                            movieFile.IndexerFlags = localMovie.IndexerFlags;
                        }
                        else if (client?.DownloadId.IsNotNullOrWhiteSpace() == true)
                        {
                            var grabHistory = _historyService.FindByDownloadId(client.DownloadId)
                                .OrderByDescending(h => h.Date)
                                .FirstOrDefault(h => h.EventType == MovieHistoryEventType.Grabbed && h.MovieVersionId == localMovie.MovieVersionId);

                            if (Enum.TryParse(grabHistory?.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                            {
                                movieFile.IndexerFlags = flags;
                            }
                        }
                        else
                        {
                            movieFile.IndexerFlags = localMovie.IndexerFlags;
                        }

                        bool copyOnly;
                        switch (importMode)
                        {
                            default:
                            case ImportMode.Auto:
                                copyOnly = client is { CanMoveFiles: false };
                                break;
                            case ImportMode.Move:
                                copyOnly = false;
                                break;
                            case ImportMode.Copy:
                                copyOnly = true;
                                break;
                        }

                        if (newDownload)
                        {
                            movieFile.SceneName = localMovie.SceneName;
                            movieFile.OriginalFilePath = GetOriginalFilePath(client, localMovie);

                            if (localMovie.MovieVersionId.HasValue)
                            {
                                _managedImports.Import(localMovie, movieFile, copyOnly);
                                importResults.Add(new ImportResult(importDecision));
                                continue;
                            }

                            oldFiles = _movieFileUpgrader.UpgradeMovieFile(movieFile, localMovie, copyOnly).OldFiles;
                        }
                        else
                        {
                            if (localMovie.MovieVersionId.HasValue && localMovie.Movie.MovieFile != null)
                            {
                                var current = localMovie.Movie.MovieFile;
                                movieFile.Id = current.Id;
                                movieFile.RelativePath = current.RelativePath;
                                movieFile.DateAdded = current.DateAdded;
                                movieFile.SceneName = current.SceneName;
                                movieFile.OriginalFilePath = current.OriginalFilePath;
                                _mediaFileService.Update(movieFile);
                                localMovie.Movie.MovieFile = movieFile;
                                importResults.Add(new ImportResult(importDecision));
                                _eventAggregator.PublishEvent(new MovieFileImportedEvent(localMovie, movieFile, oldFiles, false, client));
                                continue;
                            }

                            movieFile.RelativePath = localMovie.Movie.Path.GetRelativePath(movieFile.Path);

                            // Delete existing files from the DB mapped to this path
                            var previousFiles = _mediaFileService.GetFilesWithRelativePath(localMovie.Movie.Id, movieFile.RelativePath);

                            foreach (var previousFile in previousFiles)
                            {
                                _mediaFileService.Delete(previousFile, DeleteMediaFileReason.ManualOverride);
                            }
                        }

                        movieFile = _mediaFileService.Add(movieFile);
                        importResults.Add(new ImportResult(importDecision));

                        localMovie.Movie.MovieFile = movieFile;

                        if (newDownload)
                        {
                            if (localMovie.ScriptImported)
                            {
                                _existingExtraFiles.ImportExtraFiles(localMovie.Movie, localMovie.PossibleExtraFiles, localMovie.FileNameBeforeRename);

                                if (localMovie.FileNameBeforeRename != movieFile.RelativePath)
                                {
                                    _extraService.MoveFilesAfterRename(localMovie.Movie, movieFile);
                                }
                            }

                            if (!localMovie.ScriptImported || localMovie.ShouldImportExtras)
                            {
                                _extraService.ImportMovie(localMovie, movieFile, copyOnly);
                            }
                        }

                        _eventAggregator.PublishEvent(new MovieFileImportedEvent(localMovie, movieFile, oldFiles, newDownload, client));
                    }
                    catch (MovieVersionImportException ex)
                    {
                        importResults.Add(new ImportResult(importDecision, ex.Message));
                    }
                    catch (RootFolderNotFoundException e)
                    {
                        _logger.Warn(e, "Couldn't import movie " + localMovie);
                        _eventAggregator.PublishEvent(new MovieImportFailedEvent(e, localMovie, newDownload, downloadClientItem));

                        importResults.Add(new ImportResult(importDecision, "Failed to import movie, Root folder missing."));
                    }
                    catch (DestinationAlreadyExistsException e)
                    {
                        _logger.Warn(e, "Couldn't import movie " + localMovie);
                        importResults.Add(new ImportResult(importDecision, "Failed to import movie, Destination already exists."));

                        _commandQueueManager.Push(new RescanMovieCommand(localMovie.Movie.Id));
                    }
                    catch (RecycleBinException e)
                    {
                        _logger.Warn(e, "Couldn't import movie " + localMovie);
                        _eventAggregator.PublishEvent(new MovieImportFailedEvent(e, localMovie, newDownload, downloadClientItem));

                        importResults.Add(new ImportResult(importDecision, "Failed to import movie, unable to move existing file to the Recycle Bin."));
                    }
                    catch (Exception e)
                    {
                        _logger.Warn(e, "Couldn't import movie " + localMovie);
                        importResults.Add(new ImportResult(importDecision, "Failed to import movie"));
                    }
                }
            }

            // Adding all the rejected decisions
            importResults.AddRange(decisions.Where(c => !c.Approved)
                                            .Select(d => new ImportResult(d, d.Rejections.Select(r => r.Message).ToArray())));

            return importResults;
        }

        private string GetOriginalFilePath(DownloadClientItem downloadClientItem, LocalMovie localMovie)
        {
            var path = localMovie.Path;

            if (downloadClientItem != null && !downloadClientItem.OutputPath.IsEmpty)
            {
                var outputDirectory = downloadClientItem.OutputPath.Directory.ToString();

                if (outputDirectory.IsParentPath(path))
                {
                    return outputDirectory.GetRelativePath(path);
                }
            }

            var folderMovieInfo = localMovie.FolderMovieInfo;

            if (folderMovieInfo != null)
            {
                var folderPath = path.GetAncestorPath(folderMovieInfo.OriginalTitle);

                if (folderPath != null)
                {
                    return folderPath.GetParentPath().GetRelativePath(path);
                }
            }

            var parentPath = path.GetParentPath();
            var grandparentPath = parentPath.GetParentPath();

            if (grandparentPath != null)
            {
                return grandparentPath.GetRelativePath(path);
            }

            return Path.GetFileName(path);
        }
    }
}
