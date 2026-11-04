using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies
{
    public interface IMovieVersionRepository : IBasicRepository<MovieVersion>
    {
        List<MovieVersion> ForMovie(int movieId);
        void AssignComposite(Movie movie, int profileId, List<MovieVersion> versions, int? primaryFileId);
    }

    public class MovieVersionRepository : BasicRepository<MovieVersion>, IMovieVersionRepository
    {
        public MovieVersionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieVersion> ForMovie(int movieId)
        {
            return Query(v => v.MovieId == movieId && v.Retired == false);
        }

        public void AssignComposite(Movie movie, int profileId, List<MovieVersion> versions, int? primaryFileId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

            var definitionIds = connection.Query<int>("SELECT \"Id\" FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction).ToHashSet();
            var fileIds = connection.Query<int>("SELECT \"Id\" FROM \"MovieFiles\" WHERE \"MovieId\" = @Id", movie, transaction).ToHashSet();
            if (versions.Count != 2 || versions.Any(v => v.Id != 0 || v.MovieId != movie.Id) ||
                !definitionIds.SetEquals(versions.Select(v => v.CompositeProfileVersionId)) ||
                !fileIds.SetEquals(versions.Where(v => v.MovieFileId.HasValue).Select(v => v.MovieFileId.Value)) ||
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" WHERE \"MovieId\" = @Id", movie, transaction) != 0)
            {
                throw new System.InvalidOperationException("Movie files or version definitions changed while preparing assignments. Refresh and try again.");
            }

            var parameters = new
            {
                movie.Id,
                profileId,
                primaryFileId = primaryFileId ?? 0,
                oldProfileId = movie.QualityProfileId,
                oldFileId = movie.MovieFileId
            };
            var updated = connection.Execute("UPDATE \"Movies\" SET \"QualityProfileId\" = @profileId, \"MovieFileId\" = @primaryFileId WHERE \"Id\" = @Id AND \"QualityProfileId\" = @oldProfileId AND \"MovieFileId\" = @oldFileId", parameters, transaction);
            if (updated != 1)
            {
                throw new System.InvalidOperationException("The movie changed while preparing assignments. Refresh and try again.");
            }

            var insertedIds = new List<int>();
            foreach (var version in versions)
            {
                var sql = "INSERT INTO \"MovieVersions\" (\"MovieId\", \"CompositeProfileVersionId\", \"Monitored\", \"MovieFileId\") VALUES (@MovieId, @CompositeProfileVersionId, @Monitored, @MovieFileId)";
                sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
                insertedIds.Add(connection.ExecuteScalar<int>(sql, version, transaction));
            }

            transaction.Commit();
            for (var i = 0; i < versions.Count; i++)
            {
                versions[i].Id = insertedIds[i];
            }
        }
    }
}
