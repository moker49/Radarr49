using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public interface IDownloadReservationRepository : IBasicRepository<DownloadReservation>
    {
        DownloadReservation TryReserve(DownloadReservation intent, List<ActiveVersionDownload> activeDownloads);
        List<DownloadReservation> ForMovie(int movieId);
        List<DownloadReservation> FindForDownload(int clientId, string downloadId);
        void BeginDispatch(int id);
        void BindDownload(int id, string downloadId, string note, bool manual = false);
        void RecordFailure(int id, bool definitive, string note);
        void ConfirmNotSubmitted(int id, string note);
        void MarkImported(int id, int movieVersionId, int fileId);
        void MarkFailed(int id, string note);
        void MarkRemoved(int id, int clientId, string downloadId, string note);
    }

    public class DownloadReservationRepository : BasicRepository<DownloadReservation>, IDownloadReservationRepository
    {
        public DownloadReservationRepository(IMainDatabase database, IEventAggregator events)
            : base(database, events)
        {
        }

        public List<DownloadReservation> ForMovie(int movieId)
        {
            return Query(r => r.MovieId == movieId);
        }

        public DownloadReservation TryReserve(DownloadReservation intent, List<ActiveVersionDownload> activeDownloads)
        {
            if (intent.Id != 0 || intent.MovieId <= 0 || intent.MovieVersionId <= 0 || intent.DownloadClientId <= 0 ||
                string.IsNullOrWhiteSpace(intent.SourceTitle) || intent.ReleaseKeys == null || intent.ReleaseKeys.Count == 0 || intent.ReleaseKeys.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("A persisted movie, version, client and release identity are required.");
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            // Obtain a database write/row lock before checking capacity. This serializes
            // concurrent dispatchers for this movie on SQLite and PostgreSQL.
            if (connection.Execute("UPDATE \"Movies\" SET \"QualityProfileId\" = \"QualityProfileId\" WHERE \"Id\" = @MovieId", intent, transaction) != 1)
            {
                throw new InvalidOperationException("Movie no longer exists.");
            }

            var owned = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" v JOIN \"Movies\" m ON m.\"Id\" = v.\"MovieId\" JOIN \"CompositeProfileVersions\" d ON d.\"Id\" = v.\"CompositeProfileVersionId\" JOIN \"QualityProfiles\" p ON p.\"Id\" = d.\"QualityProfileId\" WHERE v.\"Id\" = @MovieVersionId AND v.\"MovieId\" = @MovieId AND d.\"CompositeProfileId\" = m.\"QualityProfileId\"", intent, transaction);
            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileDeletions\" WHERE \"MovieId\" = @MovieId AND \"MovieVersionId\" = @MovieVersionId AND \"State\" IN (0, 1, 2, 3, 4)", intent, transaction) != 0 ||
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileImports\" WHERE \"MovieId\" = @MovieId AND \"MovieVersionId\" = @MovieVersionId AND \"State\" BETWEEN 0 AND 10", intent, transaction) != 0)
            {
                return null;
            }

            if (owned != 1)
            {
                throw new InvalidOperationException("The requested version no longer belongs to this movie and profile.");
            }

            var rows = connection.Query<DownloadReservation>("SELECT * FROM \"DownloadReservations\" WHERE \"MovieId\" = @MovieId", intent, transaction).ToList();
            var active = rows.Where(r => r.IsActive).ToList();
            var ownedImports = rows.Where(r => r.State == DownloadReservationState.Imported && OwnsFile(connection, transaction, r));
            if (active.Any(r => r.MovieVersionId == intent.MovieVersionId) ||
                active.Concat(ownedImports).Any(r => r.ReleaseKeys.Intersect(intent.ReleaseKeys).Any()))
            {
                return null;
            }

            var unreserved = activeDownloads.Where(q => !active.Any(r => r.DownloadClientId == q.DownloadClientId &&
                !string.IsNullOrWhiteSpace(r.DownloadId) && string.Equals(r.DownloadId, q.DownloadId, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unreserved.Any(q => !q.MovieVersionId.HasValue || q.MovieVersionId == intent.MovieVersionId || q.ReleaseKeys.Intersect(intent.ReleaseKeys).Any()))
            {
                return null;
            }

            intent.State = DownloadReservationState.Prepared;
            intent.Created = DateTime.UtcNow;
            intent.Updated = intent.Created;
            var sql = "INSERT INTO \"DownloadReservations\" (\"MovieId\", \"MovieVersionId\", \"DownloadClientId\", \"SourceTitle\", \"ReleaseKeys\", \"State\", \"Created\", \"Updated\") VALUES (@MovieId, @MovieVersionId, @DownloadClientId, @SourceTitle, @ReleaseKeys, @State, @Created, @Updated)";
            sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            var id = connection.ExecuteScalar<int>(sql, intent, transaction);
            transaction.Commit();
            intent.Id = id;
            return intent;
        }

        public List<DownloadReservation> FindForDownload(int clientId, string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return new List<DownloadReservation>();
            }

            using var connection = _database.OpenConnection();
            var rows = connection.Query<DownloadReservation>("SELECT * FROM \"DownloadReservations\" WHERE \"DownloadClientId\" = @clientId AND UPPER(\"DownloadId\") = UPPER(@downloadId)", new { clientId, downloadId }).ToList();
            var active = rows.Where(r => r.IsActive).ToList();
            return active.Any() ? active : rows.Where(r => r.State == DownloadReservationState.Imported && OwnsFile(connection, null, r)).ToList();
        }

        public void BeginDispatch(int id)
        {
            Change(id, r =>
            {
                Require(r.State == DownloadReservationState.Prepared, "Reservation is not available for dispatch.");
                r.State = DownloadReservationState.Dispatching;
            });
        }

        public void BindDownload(int id, string downloadId, string note, bool manual = false)
        {
            Change(id, r =>
            {
                Require(r.State == DownloadReservationState.Dispatching || (manual && (r.State is DownloadReservationState.Prepared or DownloadReservationState.Uncertain)), "Reservation cannot be bound in its current state.");
                if (string.IsNullOrWhiteSpace(downloadId))
                {
                    Require(!manual, "An explicit download ID is required.");
                    r.State = DownloadReservationState.Uncertain;
                    r.Note = "Client returned no download ID; verify submission before retrying.";
                    return;
                }

                Require(downloadId.Length <= 512, "Download ID is too long.");
                r.DownloadId = downloadId;
                r.State = DownloadReservationState.Submitted;
                r.Note = AppendNote(r.Note, note);
            });
        }

        public void RecordFailure(int id, bool definitive, string note)
        {
            Change(id, r =>
            {
                if (r.State is DownloadReservationState.Failed or DownloadReservationState.Imported)
                {
                    return;
                }

                if (r.State == DownloadReservationState.Prepared || (r.State == DownloadReservationState.Dispatching && definitive))
                {
                    r.State = DownloadReservationState.Failed;
                }
                else if (r.State is DownloadReservationState.Dispatching or DownloadReservationState.Uncertain)
                {
                    r.State = DownloadReservationState.Uncertain;
                }

                r.Note = AppendNote(r.Note, note);
            });
        }

        public void ConfirmNotSubmitted(int id, string note)
        {
            Change(id, r =>
            {
                Require((r.State is DownloadReservationState.Prepared or DownloadReservationState.Dispatching or DownloadReservationState.Uncertain) && string.IsNullOrWhiteSpace(r.DownloadId), "Only an unresolved, unbound dispatch can be confirmed not submitted.");
                r.State = DownloadReservationState.Failed;
                r.Note = AppendNote(r.Note, note);
            });
        }

        public void MarkImported(int id, int movieVersionId, int fileId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = LoadForChange(connection, transaction, id);
            if (row.State == DownloadReservationState.Imported && row.MovieVersionId == movieVersionId && row.ImportedMovieFileId == fileId && OwnsFile(connection, transaction, row))
            {
                return;
            }

            Require(row.MovieVersionId == movieVersionId && row.State == DownloadReservationState.Submitted, "Import does not match a submitted reservation.");
            row.ImportedMovieFileId = fileId;
            Require(OwnsFile(connection, transaction, row), "Imported file is not owned by this managed version.");
            row.State = DownloadReservationState.Imported;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void MarkRemoved(int id, int clientId, string downloadId, string note)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = LoadForChange(connection, transaction, id);
            Require(row.DownloadClientId == clientId && !string.IsNullOrWhiteSpace(downloadId) &&
                string.Equals(row.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase),
                "Removed download does not match the reservation's bound client job.");
            Require(
                row.State is DownloadReservationState.Submitted or DownloadReservationState.Uncertain or DownloadReservationState.Failed,
                "Only a submitted or unresolved bound download can be confirmed removed.");
            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileImports\" WHERE \"MovieId\" = @MovieId AND \"MovieVersionId\" = @MovieVersionId AND \"State\" BETWEEN 0 AND 10", row, transaction) == 0 &&
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileDeletions\" WHERE \"MovieId\" = @MovieId AND \"MovieVersionId\" = @MovieVersionId AND \"State\" BETWEEN 0 AND 4", row, transaction) == 0,
                "Finish or reconcile the active file operation before releasing this download.");
            if (row.State != DownloadReservationState.Failed)
            {
                row.State = DownloadReservationState.Failed;
                row.Note = AppendNote(row.Note, note);
                Save(connection, transaction, row);
            }

            transaction.Commit();
        }

        public void MarkFailed(int id, string note)
        {
            Change(id, r =>
            {
                if (r.State is DownloadReservationState.Submitted or DownloadReservationState.Uncertain)
                {
                    r.State = DownloadReservationState.Failed;
                    r.Note = AppendNote(r.Note, note);
                }
            });
        }

        private void Change(int id, Action<DownloadReservation> change)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = LoadForChange(connection, transaction, id);
            change(row);
            Save(connection, transaction, row);
            transaction.Commit();
        }

        private static DownloadReservation LoadForChange(IDbConnection connection, IDbTransaction transaction, int id)
        {
            // Lock the owning movie before reading the reservation state.
            connection.Execute("UPDATE \"Movies\" SET \"QualityProfileId\" = \"QualityProfileId\" WHERE \"Id\" = (SELECT \"MovieId\" FROM \"DownloadReservations\" WHERE \"Id\" = @id)", new { id }, transaction);
            return connection.QuerySingle<DownloadReservation>("SELECT * FROM \"DownloadReservations\" WHERE \"Id\" = @id", new { id }, transaction);
        }

        private static void Save(IDbConnection connection, IDbTransaction transaction, DownloadReservation row)
        {
            if (row.IsActive && !string.IsNullOrWhiteSpace(row.DownloadId))
            {
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"DownloadReservations\" WHERE \"Id\" <> @Id AND \"MovieId\" = @MovieId AND \"DownloadClientId\" = @DownloadClientId AND UPPER(\"DownloadId\") = UPPER(@DownloadId) AND \"State\" IN (0, 1, 2, 5)", row, transaction) == 0, "This download ID is already claimed by another version.");
            }

            row.Updated = DateTime.UtcNow;
            connection.Execute("UPDATE \"DownloadReservations\" SET \"State\" = @State, \"DownloadId\" = @DownloadId, \"ImportedMovieFileId\" = @ImportedMovieFileId, \"Updated\" = @Updated, \"Note\" = @Note WHERE \"Id\" = @Id", row, transaction);
        }

        private static bool OwnsFile(IDbConnection connection, IDbTransaction transaction, DownloadReservation row)
        {
            return row.ImportedMovieFileId.HasValue && connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" v JOIN \"MovieFiles\" f ON f.\"Id\" = v.\"MovieFileId\" WHERE v.\"Id\" = @MovieVersionId AND v.\"MovieId\" = @MovieId AND f.\"MovieId\" = @MovieId AND f.\"Id\" = @ImportedMovieFileId", row, transaction) == 1;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static string AppendNote(string previous, string note)
        {
            if (string.IsNullOrWhiteSpace(note) || previous == note)
            {
                return previous;
            }

            return string.IsNullOrWhiteSpace(previous) ? note : previous + Environment.NewLine + note;
        }
    }
}
