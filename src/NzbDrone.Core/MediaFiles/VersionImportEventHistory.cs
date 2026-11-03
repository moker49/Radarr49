using System;
using System.IO;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;

namespace NzbDrone.Core.MediaFiles
{
    internal static class VersionImportEventHistory
    {
        public const string OperationKey = "versionFileImportId";

        public static MovieHistory Imported(VersionFileImport operation, DateTime date)
        {
            var snapshot = operation.Snapshot;
            var file = snapshot.Incoming;
            var history = new MovieHistory
            {
                MovieId = operation.MovieId, MovieVersionId = operation.MovieVersionId, Date = date,
                EventType = MovieHistoryEventType.DownloadFolderImported, Quality = file.Quality, Languages = file.Languages,
                SourceTitle = file.SceneName ?? Path.GetFileNameWithoutExtension(operation.SourcePath), DownloadId = snapshot.DownloadId
            };
            history.Data[OperationKey] = operation.Id.ToString();
            history.Data["FileId"] = operation.NewMovieFileId.Value.ToString();
            history.Data["DroppedPath"] = operation.SourcePath;
            history.Data["ImportedPath"] = Path.Combine(operation.MoviePath, operation.DestinationRelativePath);
            history.Data["DownloadClient"] = snapshot.DownloadClientInfo?.Type;
            history.Data["DownloadClientName"] = snapshot.DownloadClientInfo?.Name;
            history.Data["ReleaseGroup"] = file.ReleaseGroup;
            history.Data["CustomFormatScore"] = snapshot.CustomFormatScore.ToString();
            history.Data["Size"] = file.Size.ToString();
            history.Data["IndexerFlags"] = file.IndexerFlags.ToString();
            return history;
        }

        public static MovieHistory Deleted(VersionFileImport operation, DateTime date)
        {
            var file = operation.Snapshot.Previous;
            var history = new MovieHistory
            {
                MovieId = operation.MovieId, MovieVersionId = operation.MovieVersionId, Date = date,
                EventType = MovieHistoryEventType.MovieFileDeleted, Quality = file.Quality, Languages = file.Languages,
                SourceTitle = Path.Combine(operation.MoviePath, file.RelativePath)
            };
            history.Data[OperationKey] = operation.Id.ToString();
            history.Data["FileId"] = operation.OldMovieFileId.Value.ToString();
            history.Data["Reason"] = DeleteMediaFileReason.Upgrade.ToString();
            history.Data["ReleaseGroup"] = file.ReleaseGroup;
            history.Data["Size"] = file.Size.ToString();
            history.Data["IndexerFlags"] = file.IndexerFlags.ToString();
            return history;
        }

        public static DownloadHistory Download(VersionFileImport operation, DateTime date)
        {
            var client = operation.Snapshot.DownloadClientInfo;
            var history = new DownloadHistory
            {
                MovieId = operation.MovieId, MovieVersionId = operation.MovieVersionId, Date = date,
                EventType = DownloadHistoryEventType.FileImported, SourceTitle = operation.SourcePath,
                DownloadId = operation.Snapshot.DownloadId, DownloadClientId = client.Id, Protocol = client.Protocol
            };
            history.Data[OperationKey] = operation.Id.ToString();
            history.Data["FileId"] = operation.NewMovieFileId.Value.ToString();
            history.Data["DownloadClient"] = client.Type;
            history.Data["DownloadClientName"] = client.Name;
            return history;
        }
    }
}
