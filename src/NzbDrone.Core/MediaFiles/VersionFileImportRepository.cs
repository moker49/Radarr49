using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
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
        void BeginExtras(int id);
        void AdvanceExtra(int id, int index, VersionImportExtraState expected, VersionImportExtraState next);
        void RegisterExtra(int id, int index);
        void FinishExtras(int id);
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
                CheckRegisteredOwnership(connection, transaction, row);
                var existing = connection.QuerySingleOrDefault<MovieFile>("SELECT f.* FROM \"MovieFiles\" f JOIN \"MovieVersions\" v ON v.\"MovieFileId\" = f.\"Id\" WHERE f.\"Id\" = @NewMovieFileId AND v.\"Id\" = @MovieVersionId AND f.\"MovieId\" = @MovieId", row, transaction);
                Require(existing != null, "The registered import is no longer owned by this version.");
                existing.MovieVersionId = row.MovieVersionId;
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

        public void BeginExtras(int id)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            Require(row.State is VersionFileImportState.Registered or VersionFileImportState.Finalizing or VersionFileImportState.CleaningOldExtras, "The main-file handoff must be registered before installing sidecars.");
            CheckRegisteredOwnership(connection, transaction, row);
            if (row.State == VersionFileImportState.Registered)
            {
                VersionImportExtraPlan.Create(row);
                row.State = VersionFileImportState.Finalizing;
                Save(connection, transaction, row);
            }

            transaction.Commit();
        }

        public void AdvanceExtra(int id, int index, VersionImportExtraState expected, VersionImportExtraState next)
        {
            Require((expected == VersionImportExtraState.Prepared && next == VersionImportExtraState.Copying) ||
                (expected == VersionImportExtraState.Copying && next == VersionImportExtraState.Staged) ||
                (expected == VersionImportExtraState.Staged && next == VersionImportExtraState.BackingUp) ||
                (expected == VersionImportExtraState.BackingUp && next == VersionImportExtraState.Installing),
                "Invalid sidecar handoff transition.");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            CheckRegisteredOwnership(connection, transaction, row);
            Require(row.State == VersionFileImportState.Finalizing && index >= 0 && index < row.Snapshot.IncomingExtras.Count, "The sidecar checkpoint is no longer available.");
            var extra = row.Snapshot.IncomingExtras[index];
            Require(extra.State == expected, "The sidecar checkpoint changed. Refresh before continuing.");
            extra.State = next;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void RegisterExtra(int id, int index)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            CheckRegisteredOwnership(connection, transaction, row);
            Require(row.State == VersionFileImportState.Finalizing && index >= 0 && index < row.Snapshot.IncomingExtras.Count, "The sidecar registration is no longer available.");
            var extra = row.Snapshot.IncomingExtras[index];
            Require(extra.State == VersionImportExtraState.Installing, "The sidecar has not reached its installation checkpoint.");
            var previous = row.Snapshot.PreviousExtras.Where(e => string.Equals(e.RelativePath, extra.DestinationRelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
            Require(previous.All(e => !e.Preserve), "A shared sidecar cannot be replaced.");
            foreach (var table in new[] { "SubtitleFiles", "MetadataFiles", "ExtraFiles" })
            {
                var claims = connection.Query<OtherExtraFile>($"SELECT * FROM \"{table}\" WHERE \"MovieId\" = @MovieId AND LOWER(\"RelativePath\") = LOWER(@relative)", new { row.MovieId, relative = extra.DestinationRelativePath }, transaction).ToList();
                Require(claims.Count == previous.Count(e => e.Table == table) && claims.All(c => c.MovieFileId == row.OldMovieFileId && previous.Any(e => e.Table == table && e.Id == c.Id)), "Sidecar registrations changed before commit.");
                foreach (var claim in claims)
                {
                    connection.Execute($"DELETE FROM \"{table}\" WHERE \"Id\" = @Id", claim, transaction);
                }
            }

            var incoming = (ExtraFile)extra.Subtitle ?? new OtherExtraFile();
            incoming.MovieId = row.MovieId;
            incoming.MovieFileId = row.NewMovieFileId;
            incoming.RelativePath = extra.DestinationRelativePath;
            incoming.Extension = System.IO.Path.GetExtension(extra.SourcePath);
            incoming.Added = incoming.LastUpdated = DateTime.UtcNow;
            var insert = extra.Subtitle != null
                ? "INSERT INTO \"SubtitleFiles\" (\"MovieId\", \"MovieFileId\", \"RelativePath\", \"Extension\", \"Added\", \"LastUpdated\", \"Language\", \"LanguageTags\", \"Copy\", \"Title\") VALUES (@MovieId, @MovieFileId, @RelativePath, @Extension, @Added, @LastUpdated, @Language, @LanguageTags, @Copy, @Title)"
                : "INSERT INTO \"ExtraFiles\" (\"MovieId\", \"MovieFileId\", \"RelativePath\", \"Extension\", \"Added\", \"LastUpdated\") VALUES (@MovieId, @MovieFileId, @RelativePath, @Extension, @Added, @LastUpdated)";
            insert += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
            extra.RegisteredExtraId = connection.ExecuteScalar<int>(insert, incoming, transaction);
            extra.State = VersionImportExtraState.Registered;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        public void FinishExtras(int id)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var row = Load(connection, transaction, id);
            CheckRegisteredOwnership(connection, transaction, row);
            Require(row.State == VersionFileImportState.Finalizing && row.Snapshot.IncomingExtras.All(e => e.State == VersionImportExtraState.Registered), "All incoming sidecars must be registered before old-extra cleanup.");
            foreach (var extra in row.Snapshot.IncomingExtras)
            {
                var table = extra.Subtitle == null ? "ExtraFiles" : "SubtitleFiles";
                foreach (var category in new[] { "SubtitleFiles", "MetadataFiles", "ExtraFiles" })
                {
                    var claims = connection.Query<OtherExtraFile>($"SELECT * FROM \"{category}\" WHERE \"MovieId\" = @MovieId AND LOWER(\"RelativePath\") = LOWER(@relative)", new { row.MovieId, relative = extra.DestinationRelativePath }, transaction).ToList();
                    Require(category == table ? claims.Count == 1 && claims[0].Id == extra.RegisteredExtraId && claims[0].MovieFileId == row.NewMovieFileId : claims.Count == 0, "An installed sidecar registration changed before finalization.");
                }
            }

            row.State = VersionFileImportState.CleaningOldExtras;
            Save(connection, transaction, row);
            transaction.Commit();
        }

        private static void CheckRegisteredOwnership(IDbConnection connection, IDbTransaction transaction, VersionFileImport row)
        {
            Require(row.NewMovieFileId.HasValue && connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" v JOIN \"Movies\" m ON m.\"Id\" = v.\"MovieId\" JOIN \"MovieFiles\" f ON f.\"Id\" = v.\"MovieFileId\" JOIN \"CompositeProfileVersions\" d ON d.\"Id\" = v.\"CompositeProfileVersionId\" WHERE v.\"Id\" = @MovieVersionId AND v.\"MovieId\" = @MovieId AND m.\"Path\" = @MoviePath AND f.\"MovieId\" = @MovieId AND f.\"Id\" = @NewMovieFileId AND f.\"RelativePath\" = @DestinationRelativePath AND d.\"CompositeProfileId\" = m.\"QualityProfileId\"", row, transaction) == 1, "The registered movie, version, file or path changed. Finalization is blocked.");
            if (row.DownloadReservationId.HasValue)
            {
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"DownloadReservations\" WHERE \"Id\" = @id AND \"MovieId\" = @MovieId AND \"MovieVersionId\" = @MovieVersionId AND \"ImportedMovieFileId\" = @NewMovieFileId AND \"DownloadClientId\" = @client AND UPPER(\"DownloadId\") = UPPER(@download) AND \"State\" = 3", new { id = row.DownloadReservationId.Value, row.MovieId, row.MovieVersionId, row.NewMovieFileId, client = row.Snapshot.DownloadClientInfo?.Id, download = row.Snapshot.DownloadId }, transaction) == 1, "The exact imported reservation changed. Finalization is blocked.");
            }
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
            connection.Execute("UPDATE \"VersionFileImports\" SET \"State\" = @State, \"NewMovieFileId\" = @NewMovieFileId, \"Snapshot\" = @Snapshot, \"Updated\" = @Updated, \"Note\" = @Note WHERE \"Id\" = @Id", row, transaction);
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
