using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IVersionFileDeletionRepository : IBasicRepository<VersionFileDeletion>
    {
        List<VersionFileDeletion> ForMovie(int movieId);
        List<VersionFileDeletion> Pending();
        VersionFileDeletion FindRequest(int movieId, int versionId, int fileId);
        VersionFileDeletion Begin(VersionFileDeletion intent);
        void CheckReady(int movieId, int? versionId = null);
        void Advance(int id, VersionFileDeletionState expected, VersionFileDeletionState next, string recyclePath = null, string note = null);
        void Detach(int id, string recyclePath = null);
        void FinishExtra(int id, bool removeRegistration, string note = null);
        bool OwnsExtra(VersionFileDeletion operation);
        void Cancel(int id, string note);
    }

    public class VersionFileDeletionRepository : BasicRepository<VersionFileDeletion>, IVersionFileDeletionRepository
    {
        public VersionFileDeletionRepository(IMainDatabase database, IEventAggregator events)
            : base(database, events)
        {
        }

        public List<VersionFileDeletion> ForMovie(int movieId) => Query(r => r.MovieId == movieId);
        public List<VersionFileDeletion> Pending() => Query(r => r.State != VersionFileDeletionState.Completed && r.State != VersionFileDeletionState.Cancelled);
        public VersionFileDeletion FindRequest(int movieId, int versionId, int fileId) =>
            Query(r => r.MovieId == movieId && r.MovieVersionId == versionId && r.MovieFileId == fileId && r.State != VersionFileDeletionState.Cancelled).OrderByDescending(r => r.Id).FirstOrDefault();

        public void CheckReady(int movieId, int? versionId = null)
        {
            using var connection = _database.OpenConnection();
            var sql = "SELECT COUNT(*) FROM \"VersionFileDeletions\" WHERE \"MovieId\" = @movieId AND \"State\" IN (0, 1, 2, 3, 4)";
            if (versionId.HasValue)
            {
                sql += " AND \"MovieVersionId\" = @versionId";
            }

            if (connection.ExecuteScalar<int>(sql, new { movieId, versionId }) != 0)
            {
                throw new VersionFileDeletionPendingException();
            }
        }

        public VersionFileDeletion Begin(VersionFileDeletion intent)
        {
            Require(intent.Id == 0 && intent.Snapshot?.File?.Id == intent.MovieFileId && intent.Snapshot.File.MovieId == intent.MovieId &&
                intent.Snapshot.File.Movie == null && !string.IsNullOrWhiteSpace(intent.MoviePath),
                "A detached file snapshot and owning movie path are required.");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockMovie(connection, transaction, intent.MovieId);
            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileDeletions\" WHERE \"MovieVersionId\" = @MovieVersionId AND \"State\" IN (0, 1, 2, 3, 4)", intent, transaction) == 0, "This version already has an unfinished deletion.");
            CheckOwnership(connection, transaction, intent);
            intent.State = VersionFileDeletionState.Prepared;
            intent.Created = intent.Updated = DateTime.UtcNow;
            Insert(connection, transaction, intent);
            transaction.Commit();
            return intent;
        }

        public void Advance(int id, VersionFileDeletionState expected, VersionFileDeletionState next, string recyclePath = null, string note = null)
        {
            var allowed = (expected == VersionFileDeletionState.Prepared && next == VersionFileDeletionState.Recycling) ||
                (expected == VersionFileDeletionState.Detached && next is VersionFileDeletionState.RecyclingExtra or VersionFileDeletionState.EventsPending) ||
                (expected == VersionFileDeletionState.EventsPending && next == VersionFileDeletionState.Completed) ||
                (expected == next && expected is VersionFileDeletionState.Recycling or VersionFileDeletionState.RecyclingExtra && !string.IsNullOrWhiteSpace(note));
            Require(allowed, "Invalid deletion checkpoint transition.");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            Require(row.State == expected, "Deletion checkpoint changed. Refresh its state.");
            Require(next != VersionFileDeletionState.EventsPending || row.ExtraIndex == row.Snapshot.Extras.Count, "Extras cleanup is not complete.");
            Require(next != VersionFileDeletionState.RecyclingExtra || row.ExtraIndex < row.Snapshot.Extras.Count, "There is no extra file to recycle.");
            row.State = next;
            row.Note = note == null ? row.Note : (row.Note == null ? note : row.Note + "\n" + note);
            row.RecyclePath = recyclePath ?? row.RecyclePath;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void Detach(int id, string recyclePath = null)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            Require(row.State == VersionFileDeletionState.Recycling, "File recycling has not begun.");
            CheckOwnership(connection, transaction, row);
            connection.Execute("UPDATE \"MovieVersions\" SET \"MovieFileId\" = NULL WHERE \"Id\" = @MovieVersionId", row, transaction);
            if (row.Unmonitor)
            {
                connection.Execute("UPDATE \"MovieVersions\" SET \"Monitored\" = @monitored WHERE \"Id\" = @MovieVersionId", new { monitored = false, row.MovieVersionId }, transaction);
            }

            connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = 0 WHERE \"Id\" = @MovieId AND \"MovieFileId\" = @MovieFileId", row, transaction);
            connection.Execute("DELETE FROM \"MovieFiles\" WHERE \"Id\" = @MovieFileId AND \"MovieId\" = @MovieId", row, transaction);
            row.State = VersionFileDeletionState.Detached;
            row.RecyclePath = recyclePath ?? row.RecyclePath;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public bool OwnsExtra(VersionFileDeletion operation)
        {
            var extra = operation.Snapshot.Extras[operation.ExtraIndex];
            using var connection = _database.OpenConnection();
            return OwnsExtra(connection, null, operation, extra);
        }

        public void FinishExtra(int id, bool removeRegistration, string note = null)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            Require(row.State is VersionFileDeletionState.Detached or VersionFileDeletionState.RecyclingExtra, "Deletion is not processing extras.");
            var extra = row.Snapshot.Extras[row.ExtraIndex];
            if (removeRegistration && OwnsExtra(connection, transaction, row, extra))
            {
                connection.Execute($"DELETE FROM \"{ExtraTable(extra.Table)}\" WHERE \"Id\" = @Id AND \"MovieFileId\" = @fileId", new { extra.Id, fileId = row.MovieFileId }, transaction);
            }

            row.ExtraIndex++;
            row.State = VersionFileDeletionState.Detached;
            row.Note = note == null ? row.Note : (row.Note == null ? note : row.Note + "\n" + note);
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void Cancel(int id, string note)
        {
            Require(!string.IsNullOrWhiteSpace(note), "A cancellation verification note is required.");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            Require(row.State == VersionFileDeletionState.Prepared, "Only a deletion that has not begun recycling can be cancelled.");
            row.State = VersionFileDeletionState.Cancelled;
            row.Note = note;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        private static void CheckOwnership(IDbConnection connection, IDbTransaction transaction, VersionFileDeletion row)
        {
            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" v JOIN \"Movies\" m ON m.\"Id\" = v.\"MovieId\" JOIN \"CompositeProfileVersions\" d ON d.\"Id\" = v.\"CompositeProfileVersionId\" JOIN \"MovieFiles\" f ON f.\"Id\" = v.\"MovieFileId\" WHERE v.\"Id\" = @MovieVersionId AND v.\"MovieId\" = @MovieId AND f.\"MovieId\" = @MovieId AND f.\"Id\" = @MovieFileId AND m.\"Path\" = @MoviePath AND f.\"RelativePath\" = @relativePath AND d.\"CompositeProfileId\" = m.\"QualityProfileId\"", new { row.MovieVersionId, row.MovieId, row.MovieFileId, row.MoviePath, relativePath = row.Snapshot.File.RelativePath }, transaction) == 1,
                "The movie, version, file or path changed. No ownership was modified.");
            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"DownloadReservations\" WHERE \"MovieVersionId\" = @MovieVersionId AND \"State\" IN (0, 1, 2, 5)", row, transaction) == 0, "This version has an active download reservation.");
        }

        private static bool OwnsExtra(IDbConnection connection, IDbTransaction transaction, VersionFileDeletion row, VersionFileDeletionExtra extra) =>
            connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM \"{ExtraTable(extra.Table)}\" WHERE \"Id\" = @Id AND \"MovieId\" = @MovieId AND \"MovieFileId\" = @MovieFileId AND \"RelativePath\" = @RelativePath", new { extra.Id, row.MovieId, row.MovieFileId, extra.RelativePath }, transaction) == 1;

        private static string ExtraTable(string table) => table is "SubtitleFiles" or "MetadataFiles" or "ExtraFiles" ? table : throw new InvalidOperationException("Unknown extras table in deletion snapshot.");

        private static VersionFileDeletion Load(IDbConnection connection, IDbTransaction transaction, int id)
        {
            connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = \"MovieFileId\" WHERE \"Id\" = (SELECT \"MovieId\" FROM \"VersionFileDeletions\" WHERE \"Id\" = @id)", new { id }, transaction);
            return connection.QuerySingle<VersionFileDeletion>("SELECT * FROM \"VersionFileDeletions\" WHERE \"Id\" = @id", new { id }, transaction);
        }

        private static void LockMovie(IDbConnection connection, IDbTransaction transaction, int movieId) =>
            Require(connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = \"MovieFileId\" WHERE \"Id\" = @movieId", new { movieId }, transaction) == 1, "Movie no longer exists.");

        private static void Save(IDbConnection connection, IDbTransaction transaction, VersionFileDeletion row)
        {
            row.Updated = DateTime.UtcNow;
            connection.Execute("UPDATE \"VersionFileDeletions\" SET \"State\" = @State, \"ExtraIndex\" = @ExtraIndex, \"RecyclePath\" = @RecyclePath, \"Note\" = @Note, \"Updated\" = @Updated WHERE \"Id\" = @Id", row, transaction);
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
