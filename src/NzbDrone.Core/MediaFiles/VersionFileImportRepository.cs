using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MediaFiles
{
    public interface IVersionFileImportRepository : IBasicRepository<VersionFileImport>
    {
        List<VersionFileImport> ForMovie(int movieId);
        VersionFileImport Begin(VersionFileImport intent);
        void Advance(int id, VersionFileImportState expected, VersionFileImportState next);
        MovieFile Register(int id);
        void CancelBeforeBackup(int id, string note);
    }

    public class VersionFileImportRepository : BasicRepository<VersionFileImport>, IVersionFileImportRepository
    {
        public VersionFileImportRepository(IMainDatabase database, IEventAggregator events)
            : base(database, events)
        {
        }

        public List<VersionFileImport> ForMovie(int movieId) => Query(r => r.MovieId == movieId);

        public VersionFileImport Begin(VersionFileImport intent)
        {
            Require(intent.Id == 0 && intent.NewMovieFileId == null && intent.Snapshot?.Incoming?.Id == 0 && intent.Snapshot.Incoming.Movie == null &&
                intent.Snapshot.Incoming.MovieId == intent.MovieId && intent.Snapshot.Previous?.Id == intent.OldMovieFileId && intent.Snapshot.Previous?.Movie == null &&
                ValidHash(intent.Snapshot.SourceHash) && (!intent.Snapshot.PreviousExisted || ValidHash(intent.Snapshot.PreviousHash)),
                "A detached incoming file, exact previous file and SHA-256 identities are required.");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockMovie(connection, transaction, intent.MovieId);
            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileImports\" WHERE \"MovieVersionId\" = @MovieVersionId AND \"State\" BETWEEN 0 AND 10", intent, transaction) == 0, "This version already has an unfinished import.");
            CheckOwnership(connection, transaction, intent);
            intent.State = VersionFileImportState.Prepared;
            intent.Created = intent.Updated = DateTime.UtcNow;
            Insert(connection, transaction, intent);
            transaction.Commit();
            return intent;
        }

        public void Advance(int id, VersionFileImportState expected, VersionFileImportState next)
        {
            Require((expected == VersionFileImportState.Prepared && next == VersionFileImportState.Staging) ||
                (expected == VersionFileImportState.Staging && next == VersionFileImportState.Staged) ||
                (expected == VersionFileImportState.Staged && next == VersionFileImportState.BackingUp) ||
                (expected == VersionFileImportState.BackingUp && next == VersionFileImportState.Installing),
                "Invalid import handoff transition.");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            Require(row.State == expected, "The import checkpoint changed. Refresh it before continuing.");
            CheckOwnership(connection, transaction, row);
            row.State = next;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public MovieFile Register(int id)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            if (row.State == VersionFileImportState.Registered)
            {
                var existing = connection.QuerySingleOrDefault<MovieFile>("SELECT f.* FROM \"MovieFiles\" f JOIN \"MovieVersions\" v ON v.\"MovieFileId\" = f.\"Id\" WHERE f.\"Id\" = @NewMovieFileId AND v.\"Id\" = @MovieVersionId AND f.\"MovieId\" = @MovieId", row, transaction);
                Require(existing != null, "The registered import is no longer owned by this version.");
                return existing;
            }

            Require(row.State == VersionFileImportState.Installing, "The replacement has not reached its installation checkpoint.");
            CheckOwnership(connection, transaction, row);
            var incoming = row.Snapshot.Incoming;
            incoming.Id = 0;
            incoming.RelativePath = row.DestinationRelativePath;
            var sql = "INSERT INTO \"MovieFiles\" (\"MovieId\", \"RelativePath\", \"Size\", \"DateAdded\", \"OriginalFilePath\", \"SceneName\", \"ReleaseGroup\", \"Quality\", \"IndexerFlags\", \"MediaInfo\", \"Edition\", \"Languages\") VALUES (@MovieId, @RelativePath, @Size, @DateAdded, @OriginalFilePath, @SceneName, @ReleaseGroup, @Quality, @IndexerFlags, @MediaInfo, @Edition, @Languages)";
            sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            incoming.Id = connection.ExecuteScalar<int>(sql, incoming, transaction);
            connection.Execute("UPDATE \"MovieVersions\" SET \"MovieFileId\" = @fileId WHERE \"Id\" = @versionId", new { fileId = incoming.Id, versionId = row.MovieVersionId }, transaction);
            connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = @fileId WHERE \"Id\" = @MovieId AND EXISTS (SELECT 1 FROM \"MovieVersions\" v JOIN \"CompositeProfileVersions\" d ON d.\"Id\" = v.\"CompositeProfileVersionId\" WHERE v.\"Id\" = @versionId AND d.\"IsPrimary\" = @primary)", new { fileId = incoming.Id, row.MovieId, versionId = row.MovieVersionId, primary = true }, transaction);
            if (row.OldMovieFileId.HasValue)
            {
                connection.Execute("DELETE FROM \"MovieFiles\" WHERE \"Id\" = @OldMovieFileId AND \"MovieId\" = @MovieId", row, transaction);
            }

            if (row.DownloadReservationId.HasValue)
            {
                connection.Execute("UPDATE \"DownloadReservations\" SET \"State\" = 3, \"ImportedMovieFileId\" = @fileId, \"Updated\" = @updated WHERE \"Id\" = @id", new { fileId = incoming.Id, updated = DateTime.UtcNow, id = row.DownloadReservationId.Value }, transaction);
            }

            row.NewMovieFileId = incoming.Id;
            row.State = VersionFileImportState.Registered;
            Save(connection, transaction, row);
            transaction.Commit();
            incoming.MovieVersionId = row.MovieVersionId;
            return incoming;
        }

        public void CancelBeforeBackup(int id, string note)
        {
            Require(!string.IsNullOrWhiteSpace(note), "A cancellation note is required.");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            Require(row.State is VersionFileImportState.Prepared or VersionFileImportState.Staging or VersionFileImportState.Staged, "The import may already have moved the previous file. Reconcile that handoff before cancelling.");
            CheckOwnership(connection, transaction, row);
            row.State = VersionFileImportState.Cancelled;
            row.Note = note;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        private static void CheckOwnership(IDbConnection connection, IDbTransaction transaction, VersionFileImport row)
        {
            var version = connection.QuerySingleOrDefault<MovieVersion>("SELECT v.* FROM \"MovieVersions\" v JOIN \"Movies\" m ON m.\"Id\" = v.\"MovieId\" JOIN \"CompositeProfileVersions\" d ON d.\"Id\" = v.\"CompositeProfileVersionId\" WHERE v.\"Id\" = @MovieVersionId AND v.\"MovieId\" = @MovieId AND m.\"Path\" = @MoviePath AND d.\"CompositeProfileId\" = m.\"QualityProfileId\"", row, transaction);
            Require(version != null && version.MovieFileId == row.OldMovieFileId, "The movie path, version or previous file changed. No replacement was registered.");
            if (row.OldMovieFileId.HasValue)
            {
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieFiles\" WHERE \"Id\" = @OldMovieFileId AND \"MovieId\" = @MovieId AND \"RelativePath\" = @relative", new { row.OldMovieFileId, row.MovieId, relative = row.Snapshot.Previous.RelativePath }, transaction) == 1, "The previous file path changed.");
            }

            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieFiles\" WHERE \"MovieId\" = @MovieId AND LOWER(\"RelativePath\") = LOWER(@DestinationRelativePath) AND (@oldId IS NULL OR \"Id\" <> @oldId)", new { row.MovieId, row.DestinationRelativePath, oldId = row.OldMovieFileId }, transaction) == 0, "The destination now belongs to another file.");
            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileDeletions\" WHERE \"MovieVersionId\" = @MovieVersionId AND \"State\" IN (0, 1, 2, 3, 4)", row, transaction) == 0, "This version has an unfinished deletion.");
            if (row.DownloadReservationId.HasValue)
            {
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"DownloadReservations\" WHERE \"Id\" = @id AND \"MovieId\" = @MovieId AND \"MovieVersionId\" = @MovieVersionId AND \"DownloadClientId\" = @client AND UPPER(\"DownloadId\") = UPPER(@download) AND \"State\" = 2", new { id = row.DownloadReservationId.Value, row.MovieId, row.MovieVersionId, client = row.Snapshot.DownloadClientInfo?.Id, download = row.Snapshot.DownloadId }, transaction) == 1, "The exact submitted download reservation changed.");
            }
            else
            {
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"DownloadReservations\" WHERE \"MovieVersionId\" = @MovieVersionId AND \"State\" IN (0, 1, 2, 5)", row, transaction) == 0, "This version has an active reserved download.");
            }
        }

        private static VersionFileImport Load(IDbConnection connection, IDbTransaction transaction, int id)
        {
            connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = \"MovieFileId\" WHERE \"Id\" = (SELECT \"MovieId\" FROM \"VersionFileImports\" WHERE \"Id\" = @id)", new { id }, transaction);
            return connection.QuerySingle<VersionFileImport>("SELECT * FROM \"VersionFileImports\" WHERE \"Id\" = @id", new { id }, transaction);
        }

        private static void LockMovie(IDbConnection connection, IDbTransaction transaction, int movieId) => Require(connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = \"MovieFileId\" WHERE \"Id\" = @movieId", new { movieId }, transaction) == 1, "Movie no longer exists.");

        private static void Save(IDbConnection connection, IDbTransaction transaction, VersionFileImport row)
        {
            row.Updated = DateTime.UtcNow;
            connection.Execute("UPDATE \"VersionFileImports\" SET \"State\" = @State, \"NewMovieFileId\" = @NewMovieFileId, \"Updated\" = @Updated, \"Note\" = @Note WHERE \"Id\" = @Id", row, transaction);
        }

        private static bool ValidHash(string hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
