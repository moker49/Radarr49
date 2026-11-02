using System;
using System.IO;
using System.Linq;
using System.Net;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Manual
{
    public interface IManualImportVersionService
    {
        TrackedDownload GetTrackedDownload(string downloadId, int? clientId);
        TrackedDownload Prepare(LocalMovie local, string downloadId, ManualImportVersionSelection selection);
    }

    public class ManualImportVersionService : IManualImportVersionService
    {
        private readonly IMovieVersionService _versions;
        private readonly IMovieVersionImportService _imports;
        private readonly ITrackedDownloadService _tracked;
        private readonly IDownloadReservationRepository _reservations;

        public ManualImportVersionService(IMovieVersionService versions, IMovieVersionImportService imports, ITrackedDownloadService tracked, IDownloadReservationRepository reservations)
        {
            _versions = versions;
            _imports = imports;
            _tracked = tracked;
            _reservations = reservations;
        }

        public TrackedDownload GetTrackedDownload(string downloadId, int? clientId)
        {
            if (downloadId.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (!clientId.HasValue)
            {
                return _tracked.Find(downloadId);
            }

            var matches = _tracked.GetTrackedDownloads().Where(t => t.DownloadClient == clientId &&
                string.Equals(t.DownloadItem?.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase)).ToList();
            Require(matches.Count == 1, "The selected client download is not tracked unambiguously. Refresh the queue.");
            return matches[0];
        }

        public TrackedDownload Prepare(LocalMovie local, string downloadId, ManualImportVersionSelection selection)
        {
            selection ??= new ManualImportVersionSelection();
            if (downloadId.IsNullOrWhiteSpace())
            {
                Require(!_tracked.GetTrackedDownloads().Any(t => (t.MovieVersionId.HasValue || t.DownloadReservationId.HasValue) &&
                    t.State is not (TrackedDownloadState.Imported or TrackedDownloadState.Failed or TrackedDownloadState.Ignored) &&
                    ContainsSource(t, local.Path)),
                    "This source belongs to a managed client download. Keep its download, client, version and reservation identity when importing it.");
            }

            var managed = local.Movie != null && _versions.Resolve(local.Movie).Any(v => v.Id.HasValue);
            if (!managed)
            {
                Require(!selection.MovieVersionId.HasValue && !selection.DownloadReservationId.HasValue, "The selected movie has no managed version for this import.");
                var ordinaryDownload = GetTrackedDownload(downloadId, selection.DownloadClientId);
                Require(ordinaryDownload == null || (!ordinaryDownload.MovieVersionId.HasValue && !ordinaryDownload.DownloadReservationId.HasValue &&
                    ordinaryDownload.RemoteMovie?.MovieVersionId == null),
                    "This download belongs to a managed version. Select its bound movie and version.");
                if (ordinaryDownload != null)
                {
                    Require((_reservations.FindForDownload(ordinaryDownload.DownloadClient, downloadId)?.Count ?? 0) == 0,
                        "This client download has a managed reservation. Keep its bound movie and version identity.");
                }

                return ordinaryDownload;
            }

            Require(selection.MovieVersionId > 0, "Select an explicit destination version for this manual import.");
            local.MovieVersionId = selection.MovieVersionId;
            local.DownloadReservationId = selection.DownloadReservationId;
            TrackedDownload tracked = null;
            if (downloadId.IsNotNullOrWhiteSpace())
            {
                Require(selection.DownloadClientId > 0 && selection.DownloadReservationId > 0, "A managed download import requires its client and reservation IDs.");
                tracked = GetTrackedDownload(downloadId, selection.DownloadClientId);
                Require(tracked?.DownloadItem?.DownloadClientInfo?.Id == selection.DownloadClientId &&
                    tracked.RemoteMovie?.Movie?.Id == local.Movie.Id && tracked.MovieVersionId == selection.MovieVersionId &&
                    tracked.DownloadReservationId == selection.DownloadReservationId,
                    "The tracked download does not match the selected movie, version and reservation.");
                Require(ContainsSource(tracked, local.Path), "The selected file is outside this download's import path.");
                local.DownloadItem = tracked.DownloadItem;
            }
            else
            {
                Require(!selection.DownloadClientId.HasValue && !selection.DownloadReservationId.HasValue, "Client and reservation IDs require a download ID.");
                Require(!_reservations.ForMovie(local.Movie.Id).Any(r => r.MovieVersionId == selection.MovieVersionId && r.IsActive),
                    "This version has an active reserved download. Select that download or reconcile it before an unrelated manual import.");
            }

            try
            {
                _imports.Prepare(local, tracked?.DownloadItem);
            }
            catch (MovieVersionImportException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, ex.Message);
            }

            return tracked;
        }

        private static bool ContainsSource(TrackedDownload tracked, string path)
        {
            var output = tracked.ImportItem?.OutputPath.FullPath ?? tracked.DownloadItem?.OutputPath.FullPath;
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            var source = Path.GetFullPath(path);
            var root = Path.GetFullPath(output);
            return root.PathEquals(source) || root.IsParentPath(source);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, message);
            }
        }
    }
}
