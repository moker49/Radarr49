using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;

namespace NzbDrone.Core.MediaFiles
{
    public partial class VersionFileImportRepository
    {
        public void RecordEventHistory(int id)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            CheckEventReady(connection, transaction, row);
            if (row.Snapshot.Events != null)
            {
                CheckEventHistory(connection, transaction, row);
                transaction.Commit();
                return;
            }

            var now = DateTime.UtcNow;
            var progress = new VersionImportEventProgress
            {
                ImportHistoryId = InsertHistory(connection, transaction, VersionImportEventHistory.Imported(row, now))
            };
            if (row.OldMovieFileId.HasValue)
            {
                progress.DeletedHistoryId = InsertHistory(connection, transaction, VersionImportEventHistory.Deleted(row, now));
                progress.Deliveries.Add(new VersionImportDelivery { Kind = VersionImportEventKind.OldFileDeleted });
                progress.Deliveries.Add(new VersionImportDelivery { Kind = VersionImportEventKind.DeleteCompleted });
            }

            if (row.DownloadReservationId.HasValue)
            {
                var history = VersionImportEventHistory.Download(row, now);
                var insert = "INSERT INTO \"DownloadHistory\" (\"MovieId\", \"MovieVersionId\", \"SourceTitle\", \"Date\", \"EventType\", \"Data\", \"DownloadId\", \"Protocol\", \"DownloadClientId\") VALUES (@MovieId, @MovieVersionId, @SourceTitle, @Date, @EventType, @Data, @DownloadId, @Protocol, @DownloadClientId)";
                progress.DownloadHistoryId = connection.ExecuteScalar<int>(insert + ReturningId(), history, transaction);
            }
            else
            {
                Require(string.IsNullOrWhiteSpace(row.Snapshot.DownloadId) && row.Snapshot.DownloadClientInfo == null, "An unreserved import cannot infer a download identity for its audit history.");
            }

            progress.Deliveries.Add(new VersionImportDelivery { Kind = VersionImportEventKind.FileAdded });
            progress.Deliveries.Add(new VersionImportDelivery { Kind = VersionImportEventKind.FileImported });
            row.Snapshot.Events = progress;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void BeginEventDelivery(int id, int index)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            var delivery = EventDelivery(connection, transaction, row, index);
            Require(delivery.State is VersionImportDeliveryState.Prepared or VersionImportDeliveryState.Dispatching, "This event was already dispatched.");
            delivery.State = VersionImportDeliveryState.Dispatching;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void FinishEventDelivery(int id, int index)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            var delivery = EventDelivery(connection, transaction, row, index);
            Require(delivery.State == VersionImportDeliveryState.Dispatching, "The event has not reached its dispatch checkpoint.");
            delivery.State = VersionImportDeliveryState.Dispatched;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void CompleteEvents(int id)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            if (row.State == VersionFileImportState.Completed)
            {
                transaction.Commit();
                return;
            }

            CheckEventReady(connection, transaction, row);
            CheckEventHistory(connection, transaction, row);
            Require(row.Snapshot.Events.Deliveries.All(d => d.State == VersionImportDeliveryState.Dispatched), "Import events are still pending dispatch.");
            row.State = VersionFileImportState.Completed;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        private int InsertHistory(IDbConnection connection, IDbTransaction transaction, MovieHistory history)
        {
            var insert = "INSERT INTO \"History\" (\"MovieId\", \"MovieVersionId\", \"SourceTitle\", \"Quality\", \"Languages\", \"Date\", \"EventType\", \"Data\", \"DownloadId\") VALUES (@MovieId, @MovieVersionId, @SourceTitle, @Quality, @Languages, @Date, @EventType, @Data, @DownloadId)";
            return connection.ExecuteScalar<int>(insert + ReturningId(), history, transaction);
        }

        private string ReturningId() => _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";

        private static VersionImportDelivery EventDelivery(IDbConnection connection, IDbTransaction transaction, VersionFileImport row, int index)
        {
            CheckEventReady(connection, transaction, row);
            CheckEventHistory(connection, transaction, row);
            Require(index >= 0 && index < row.Snapshot.Events.Deliveries.Count && row.Snapshot.Events.Deliveries.Take(index).All(d => d.State == VersionImportDeliveryState.Dispatched), "The event delivery is out of sequence.");
            return row.Snapshot.Events.Deliveries[index];
        }

        private static void CheckEventReady(IDbConnection connection, IDbTransaction transaction, VersionFileImport row)
        {
            Require(row.State == VersionFileImportState.EventsPending && row.Snapshot.Cleanup != null && row.Snapshot.Cleanup.All(i => i.State == VersionImportCleanupState.Finished), "Physical import finalization is still pending.");
            CheckRegisteredOwnership(connection, transaction, row);
            CheckInstalledExtras(connection, transaction, row);
        }

        private static void CheckEventHistory(IDbConnection connection, IDbTransaction transaction, VersionFileImport row)
        {
            var progress = row.Snapshot.Events;
            Require(progress != null, "The durable import audit has not been recorded.");
            var import = connection.QuerySingleOrDefault<MovieHistory>("SELECT * FROM \"History\" WHERE \"Id\" = @id", new { id = progress.ImportHistoryId }, transaction);
            Require(MatchesHistory(import, row, MovieHistoryEventType.DownloadFolderImported, row.NewMovieFileId) && import.DownloadId == row.Snapshot.DownloadId, "The import audit record changed or is missing.");
            Require(progress.DeletedHistoryId.HasValue == row.OldMovieFileId.HasValue && progress.DownloadHistoryId.HasValue == row.DownloadReservationId.HasValue, "The import audit identity is incomplete.");
            if (progress.DeletedHistoryId.HasValue)
            {
                var deleted = connection.QuerySingleOrDefault<MovieHistory>("SELECT * FROM \"History\" WHERE \"Id\" = @id", new { id = progress.DeletedHistoryId.Value }, transaction);
                Require(MatchesHistory(deleted, row, MovieHistoryEventType.MovieFileDeleted, row.OldMovieFileId), "The upgrade deletion audit changed or is missing.");
            }

            if (progress.DownloadHistoryId.HasValue)
            {
                var download = connection.QuerySingleOrDefault<DownloadHistory>("SELECT * FROM \"DownloadHistory\" WHERE \"Id\" = @id", new { id = progress.DownloadHistoryId.Value }, transaction);
                Require(download != null && download.MovieId == row.MovieId && download.MovieVersionId == row.MovieVersionId && download.EventType == DownloadHistoryEventType.FileImported &&
                    download.DownloadClientId == row.Snapshot.DownloadClientInfo.Id && download.DownloadId == row.Snapshot.DownloadId && download.Data?.GetValueOrDefault(VersionImportEventHistory.OperationKey) == row.Id.ToString() && download.Data.GetValueOrDefault("fileId") == row.NewMovieFileId.Value.ToString(),
                    "The exact download audit changed or is missing.");
            }

            var kinds = progress.Deliveries.Select(d => d.Kind).ToArray();
            var expected = row.OldMovieFileId.HasValue
                ? new[] { VersionImportEventKind.OldFileDeleted, VersionImportEventKind.DeleteCompleted, VersionImportEventKind.FileAdded, VersionImportEventKind.FileImported }
                : new[] { VersionImportEventKind.FileAdded, VersionImportEventKind.FileImported };
            Require(kinds.SequenceEqual(expected), "The persisted event sequence changed.");
        }

        private static bool MatchesHistory(MovieHistory history, VersionFileImport row, MovieHistoryEventType type, int? fileId) =>
            history != null && history.MovieId == row.MovieId && history.MovieVersionId == row.MovieVersionId && history.EventType == type &&
            history.Data?.GetValueOrDefault(VersionImportEventHistory.OperationKey) == row.Id.ToString() && history.Data.GetValueOrDefault("fileId") == fileId?.ToString();
    }
}
