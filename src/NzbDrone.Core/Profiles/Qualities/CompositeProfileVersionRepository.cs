using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using Dapper;
using FluentValidation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileVersionRepository : IBasicRepository<CompositeProfileVersion>
    {
        List<CompositeProfileVersion> ForProfile(int profileId);
        void SaveDefinitions(int profileId, List<CompositeProfileVersion> definitions, int maximumConcurrentDownloads = 1);
        void DeleteDefinitions(int profileId);
        void SetTestRoot(int profileId, string rootFolderPath);
    }

    public class CompositeProfileVersionRepository : BasicRepository<CompositeProfileVersion>, ICompositeProfileVersionRepository
    {
        public CompositeProfileVersionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<CompositeProfileVersion> ForProfile(int profileId)
        {
            return Query(v => v.CompositeProfileId == profileId);
        }

        public void SaveDefinitions(int profileId, List<CompositeProfileVersion> definitions, int maximumConcurrentDownloads = 1)
        {
            if (definitions.Any(v => v.CompositeProfileId != profileId))
            {
                throw new System.ArgumentException("Definitions must belong to the requested composite profile.", nameof(definitions));
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            EnsureEditable(connection, transaction, profileId);
            var existingIds = connection.Query<int>("SELECT \"Id\" FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction).OrderBy(id => id);
            if (!existingIds.SequenceEqual(definitions.Where(v => v.Id != 0).Select(v => v.Id).OrderBy(id => id)))
            {
                throw new System.InvalidOperationException("Composite definition changed while saving. Refresh and try again.");
            }

            // Move existing priorities out of the way so reordering retains stable IDs.
            connection.Execute("UPDATE \"CompositeProfileVersions\" SET \"Priority\" = -\"Id\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction);

            foreach (var definition in definitions.Where(v => v.Id != 0))
            {
                var updated = connection.Execute("UPDATE \"CompositeProfileVersions\" SET \"QualityProfileId\" = @QualityProfileId, \"Label\" = @Label, \"Priority\" = @Priority, \"IsPrimary\" = @IsPrimary WHERE \"Id\" = @Id AND \"CompositeProfileId\" = @CompositeProfileId", definition, transaction);
                if (updated != 1)
                {
                    throw new System.InvalidOperationException("Composite definition changed while saving. Refresh and try again.");
                }
            }

            foreach (var definition in definitions.Where(v => v.Id == 0))
            {
                var sql = "INSERT INTO \"CompositeProfileVersions\" (\"CompositeProfileId\", \"QualityProfileId\", \"Label\", \"Priority\", \"IsPrimary\") VALUES (@CompositeProfileId, @QualityProfileId, @Label, @Priority, @IsPrimary)";
                sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
                definition.Id = connection.ExecuteScalar<int>(sql, definition, transaction);
            }

            connection.Execute("UPDATE \"QualityProfiles\" SET \"MaximumConcurrentDownloads\" = @maximumConcurrentDownloads WHERE \"Id\" = @profileId", new { maximumConcurrentDownloads, profileId }, transaction);
            transaction.Commit();
        }

        public void DeleteDefinitions(int profileId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            EnsureEditable(connection, transaction, profileId);
            connection.Execute("DELETE FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction);
            transaction.Commit();
        }

        public void SetTestRoot(int profileId, string rootFolderPath)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockUnused(connection, transaction, profileId);
            if (rootFolderPath != null)
            {
                var definitions = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction).ToList();
                new CompositeProfileValidator().ValidateAndThrow(definitions);
            }

            connection.Execute("UPDATE \"QualityProfiles\" SET \"CompositeTestRoot\" = @rootFolderPath WHERE \"Id\" = @profileId", new { profileId, rootFolderPath }, transaction);
            transaction.Commit();
        }

        private static void LockUnused(IDbConnection connection, IDbTransaction transaction, int profileId)
        {
            if (connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\" = \"Name\" WHERE \"Id\" = @profileId", new { profileId }, transaction) != 1 ||
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Movies\" WHERE \"QualityProfileId\" = @profileId", new { profileId }, transaction) != 0 ||
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" v JOIN \"CompositeProfileVersions\" d ON v.\"CompositeProfileVersionId\" = d.\"Id\" WHERE d.\"CompositeProfileId\" = @profileId AND v.\"Retired\"=FALSE", new { profileId }, transaction) != 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "The composite profile is missing or already assigned.");
            }
        }

        private static void EnsureEditable(IDbConnection connection, IDbTransaction transaction, int profileId)
        {
            LockUnused(connection, transaction, profileId);
            if (!string.IsNullOrWhiteSpace(connection.ExecuteScalar<string>("SELECT \"CompositeTestRoot\" FROM \"QualityProfiles\" WHERE \"Id\" = @profileId", new { profileId }, transaction)))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Deactivate the unused composite before editing its definitions.");
            }
        }
    }
}
