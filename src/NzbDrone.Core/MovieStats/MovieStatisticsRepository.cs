using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.MovieStats
{
    public interface IMovieStatisticsRepository
    {
        List<MovieStatistics> MovieStatistics();
        List<MovieStatistics> MovieStatistics(int movieId);
    }

    public class MovieStatisticsRepository : IMovieStatisticsRepository
    {
        private const string _selectMoviesTemplate = "SELECT /**select**/ FROM \"Movies\" /**join**/ /**innerjoin**/ /**leftjoin**/ /**where**/ /**groupby**/ /**having**/ /**orderby**/";
        private const string _selectMovieFilesTemplate = "SELECT /**select**/ FROM \"MovieFiles\" /**join**/ /**innerjoin**/ /**leftjoin**/ /**where**/ /**groupby**/ /**having**/ /**orderby**/";

        private readonly IMainDatabase _database;

        public MovieStatisticsRepository(IMainDatabase database)
        {
            _database = database;
        }

        public List<MovieStatistics> MovieStatistics()
        {
            return MapResults(Query(MoviesBuilder(), _selectMoviesTemplate),
                Query(MovieFilesBuilder(), _selectMovieFilesTemplate));
        }

        public List<MovieStatistics> MovieStatistics(int movieId)
        {
            return MapResults(Query(MoviesBuilder().Where<Movie>(x => x.Id == movieId), _selectMoviesTemplate),
                Query(MovieFilesBuilder().Where<MovieFile>(x => x.MovieId == movieId), _selectMovieFilesTemplate));
        }

        private List<MovieStatistics> MapResults(List<MovieStatistics> moviesResult, List<MovieStatistics> filesResult)
        {
            moviesResult.ForEach(e =>
            {
                var file = filesResult.SingleOrDefault(f => f.MovieId == e.MovieId);

                e.MovieFileCount = file?.MovieFileCount ?? 0;
                e.SizeOnDisk = file?.SizeOnDisk ?? 0;
                e.ReleaseGroupsString = file?.ReleaseGroupsString;
                e.MovieFileQualitiesString = file?.MovieFileQualitiesString;
            });

            return moviesResult;
        }

        private List<MovieStatistics> Query(SqlBuilder builder, string template)
        {
            var sql = builder.AddTemplate(template).LogQuery();

            using var conn = _database.OpenConnection();

            return conn.Query<MovieStatistics>(sql.RawSql, sql.Parameters).ToList();
        }

        private SqlBuilder MoviesBuilder()
        {
            return new SqlBuilder(_database.DatabaseType)
                .Select(@"""Movies"".""Id"" AS MovieId")
                .Select(@"(SELECT COUNT(*) FROM ""CompositeProfileVersions"" AS d
                        LEFT JOIN ""MovieVersions"" AS v ON v.""CompositeProfileVersionId"" = d.""Id"" AND v.""MovieId"" = ""Movies"".""Id""
                        LEFT JOIN ""MovieFiles"" AS f ON f.""Id"" = v.""MovieFileId"" AND f.""MovieId"" = ""Movies"".""Id"" AND COALESCE(f.""IsUnmanaged"", FALSE) = FALSE
                        WHERE d.""CompositeProfileId"" = ""Movies"".""QualityProfileId"" AND f.""Id"" IS NULL) AS MissingVersionCount")
                .GroupBy<Movie>(x => x.Id);
        }

        private SqlBuilder MovieFilesBuilder()
        {
            if (_database.DatabaseType == DatabaseType.SQLite)
            {
                return new SqlBuilder(_database.DatabaseType)
                    .Select(@"""MovieId"",
                            COUNT(*) AS MovieFileCount,
                            SUM(COALESCE(""Size"", 0)) AS SizeOnDisk,
                            GROUP_CONCAT(""ReleaseGroup"", '|') AS ReleaseGroupsString,
                            GROUP_CONCAT(JSON_EXTRACT(""Quality"", '$.quality'), '|') AS MovieFileQualitiesString")
                    .GroupBy<MovieFile>(x => x.MovieId);
            }

            return new SqlBuilder(_database.DatabaseType)
                .Select(@"""MovieId"",
                        COUNT(*) AS MovieFileCount,
                        SUM(COALESCE(""Size"", 0)) AS SizeOnDisk,
                        string_agg(DISTINCT ""ReleaseGroup"", '|') AS ReleaseGroupsString,
                        string_agg(DISTINCT ""Quality""::json->>'quality', '|') AS MovieFileQualitiesString")
                .GroupBy<MovieFile>(x => x.MovieId);
        }
    }
}
