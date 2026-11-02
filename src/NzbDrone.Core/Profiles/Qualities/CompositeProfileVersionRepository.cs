using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileVersionRepository : IBasicRepository<CompositeProfileVersion>
    {
        List<CompositeProfileVersion> ForProfile(int profileId);
        void SaveDefinitions(int profileId, List<CompositeProfileVersion> definitions);
        void DeleteDefinitions(int profileId);
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

        public void SaveDefinitions(int profileId, List<CompositeProfileVersion> definitions)
        {
            if (definitions.Any(v => v.CompositeProfileId != profileId))
            {
                throw new System.ArgumentException("Definitions must belong to the requested composite profile.", nameof(definitions));
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

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

            transaction.Commit();
        }

        public void DeleteDefinitions(int profileId)
        {
            Delete(v => v.CompositeProfileId == profileId);
        }
    }
}
