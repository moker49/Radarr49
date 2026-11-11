using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Download;

public interface IRejectedImportService
{
    bool Process(TrackedDownload trackedDownload, ImportResult importResult);
    bool Process(TrackedDownload trackedDownload, List<ImportResult> importResults);
}

public class RejectedImportService : IRejectedImportService
{
    private readonly ICachedIndexerSettingsProvider _cachedIndexerSettingsProvider;
    private readonly Logger _logger;
    private readonly IVersionFileImportRepository _imports;
    private readonly ICompositeProfileVersionRepository _definitions;

    public RejectedImportService(ICachedIndexerSettingsProvider cachedIndexerSettingsProvider, Logger logger, IVersionFileImportRepository imports, ICompositeProfileVersionRepository definitions)
    {
        _cachedIndexerSettingsProvider = cachedIndexerSettingsProvider;
        _logger = logger;
        _imports = imports;
        _definitions = definitions;
    }

    private static bool Retryable(ImportRejectionReason reason) => reason is ImportRejectionReason.NoImportableVideo or ImportRejectionReason.UnsupportedExtension or ImportRejectionReason.VersionLanguageNotSatisfied or ImportRejectionReason.VersionSizeNotSatisfied;

    public bool Process(TrackedDownload trackedDownload, List<ImportResult> importResults)
    {
        var candidates = importResults.Where(r => !(r.ImportDecision.Rejections.Any() && r.ImportDecision.Rejections.All(x => x.Reason == ImportRejectionReason.Sample))).ToList();
        return candidates.Any() && candidates.All(r => r.Result == ImportResultType.Rejected && r.ImportDecision.Rejections.Any() && r.ImportDecision.Rejections.All(x => Retryable(x.Reason))) &&
            Process(trackedDownload, candidates.First());
    }

    public bool Process(TrackedDownload trackedDownload, ImportResult importResult)
    {
        if (importResult.Result != ImportResultType.Rejected || trackedDownload.RemoteMovie?.Release == null)
        {
            return false;
        }

        var rejectionReason = importResult.ImportDecision.Rejections.FirstOrDefault()?.Reason;
        var retryable = importResult.ImportDecision.Rejections.Any() && importResult.ImportDecision.Rejections.All(r => Retryable(r.Reason));
        var unresolvedVersion = !trackedDownload.MovieVersionId.HasValue && (_definitions.ForProfile(trackedDownload.RemoteMovie.Movie.QualityProfileId)?.Any() ?? false);
        if (retryable && !unresolvedVersion && trackedDownload.DownloadItem.Status == DownloadItemStatus.Completed &&
            !(_imports.ForMovie(trackedDownload.RemoteMovie.Movie.Id)?.Any(i => i.IsActive && i.MovieVersionId == trackedDownload.MovieVersionId) ?? false))
        {
            trackedDownload.ImportFailureReason = rejectionReason;
            trackedDownload.Warn(new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, importResult.Errors));
            trackedDownload.Fail();
            _logger.Info("Completed release {0} cannot be imported ({1}); blocklisting and searching for a replacement", trackedDownload.DownloadItem.Title, rejectionReason);
            return true;
        }

        var indexerSettings = _cachedIndexerSettingsProvider.GetSettings(trackedDownload.RemoteMovie.Release.IndexerId);
        if (indexerSettings == null)
        {
            trackedDownload.Warn(new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, importResult.Errors));
            return true;
        }

        if (rejectionReason == ImportRejectionReason.DangerousFile &&
            indexerSettings.FailDownloads.Contains(FailDownloads.PotentiallyDangerous))
        {
            _logger.Trace("Download '{0}' contains potentially dangerous file, marking as failed", trackedDownload.DownloadItem.Title);
            trackedDownload.Fail();
        }
        else if (rejectionReason == ImportRejectionReason.ExecutableFile &&
            indexerSettings.FailDownloads.Contains(FailDownloads.Executables))
        {
            _logger.Trace("Download '{0}' contains executable file, marking as failed", trackedDownload.DownloadItem.Title);
            trackedDownload.Fail();
        }
        else
        {
            trackedDownload.Warn(new TrackedDownloadStatusMessage(trackedDownload.DownloadItem.Title, importResult.Errors));
        }

        return true;
    }
}
