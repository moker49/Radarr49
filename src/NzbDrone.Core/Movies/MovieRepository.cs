using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using FluentValidation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.AlternativeTitles;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Movies
{
    public interface IMovieRepository : IBasicRepository<Movie>
    {
        bool MoviePathExists(string path);
        void InsertWithVersions(IList<Movie> movies);
        List<Movie> FindByTitles(List<string> titles);
        Movie FindByImdbId(string imdbid);
        Movie FindByTmdbId(int tmdbid);
        List<Movie> FindByTmdbId(List<int> tmdbids);
        List<Movie> MoviesBetweenDates(DateTime start, DateTime end, bool includeUnmonitored);
        PagingSpec<Movie> MoviesWithoutFiles(PagingSpec<Movie> pagingSpec);
        List<Movie> GetMoviesByFileId(int fileId);
        List<Movie> GetMoviesByCollectionTmdbId(int collectionId);
        PagingSpec<Movie> MoviesWhereCutoffUnmet(PagingSpec<Movie> pagingSpec, List<QualitiesBelowCutoff> qualitiesBelowCutoff);
        Movie FindByPath(string path);
        Dictionary<int, string> AllMoviePaths();
        List<int> AllMovieTmdbIds();
        Dictionary<int, List<int>> AllMovieTags();
        List<int> GetRecommendations();
        bool ExistsByMetadataId(int metadataId);
        HashSet<int> AllMovieWithCollectionsTmdbIds();
    }

    public class MovieRepository : BasicRepository<Movie>, IMovieRepository
    {
        private readonly IQualityProfileRepository _profileRepository;
        private readonly IAlternativeTitleRepository _alternativeTitleRepository;

        public MovieRepository(IMainDatabase database,
                               IQualityProfileRepository profileRepository,
                               IAlternativeTitleRepository alternativeTitleRepository,
                               IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
            _profileRepository = profileRepository;
            _alternativeTitleRepository = alternativeTitleRepository;
        }

        public void InsertWithVersions(IList<Movie> movies)
        {
            if (movies.Any(m => m.Id != 0) || movies.Distinct().Count() != movies.Count)
            {
                throw new InvalidOperationException("Only distinct new movies can be inserted.");
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                var definitions = new Dictionary<int, List<CompositeProfileVersion>>();
                foreach (var profileId in movies.Select(m => m.QualityProfileId).Distinct().OrderBy(id => id))
                {
                    if (connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\" = \"Name\" WHERE \"Id\" = @profileId", new { profileId }, transaction) != 1)
                    {
                        throw new InvalidOperationException("The selected quality profile no longer exists.");
                    }

                    var children = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction).ToList();
                    definitions.Add(profileId, children);
                    if (children.Any())
                    {
                        new CompositeProfileValidator().ValidateAndThrow(children);
                        var profile = connection.QuerySingle<QualityProfile>("SELECT \"Id\", \"CompositeTestRoot\" FROM \"QualityProfiles\" WHERE \"Id\" = @profileId", new { profileId }, transaction);
                        foreach (var movie in movies.Where(m => m.QualityProfileId == profileId))
                        {
                            CompositeProfileActivation.ValidateNewMovie(movie, profile);
                        }
                    }
                }

                foreach (var movie in movies)
                {
                    Insert(connection, transaction, movie);
                    foreach (var definition in definitions[movie.QualityProfileId])
                    {
                        connection.Execute("INSERT INTO \"MovieVersions\" (\"MovieId\", \"CompositeProfileVersionId\", \"Monitored\", \"MovieFileId\") VALUES (@MovieId, @DefinitionId, @Monitored, NULL)", new { MovieId = movie.Id, DefinitionId = definition.Id, Monitored = true }, transaction);
                    }
                }

                transaction.Commit();
            }
            catch
            {
                foreach (var movie in movies)
                {
                    movie.Id = 0;
                }

                throw;
            }
        }

        protected override IEnumerable<Movie> PagedQuery(SqlBuilder builder) =>
            _database.QueryJoined<Movie, MovieMetadata>(builder, (movie, movieMetadata) =>
            {
                movie.MovieMetadata = movieMetadata;
                return movie;
            });

        protected override SqlBuilder Builder() => new SqlBuilder(_database.DatabaseType)
            .Join<Movie, QualityProfile>((m, p) => m.QualityProfileId == p.Id)
            .Join<Movie, MovieMetadata>((m, p) => m.MovieMetadataId == p.Id)
            .LeftJoin<Movie, MovieFile>((m, f) => m.MovieFileId == f.Id)
            .LeftJoin<MovieMetadata, AlternativeTitle>((mm, t) => mm.Id == t.MovieMetadataId);

        private Movie Map(Dictionary<int, Movie> dict, Movie movie, MovieMetadata metadata, QualityProfile qualityProfile, MovieFile movieFile, AlternativeTitle altTitle = null, MovieTranslation translation = null)
        {
            if (!dict.TryGetValue(movie.Id, out var movieEntry))
            {
                movieEntry = movie;
                movieEntry.MovieMetadata = metadata;
                movieEntry.QualityProfile = qualityProfile;
                movieEntry.MovieFile = movieFile;
                dict.Add(movieEntry.Id, movieEntry);
            }

            if (altTitle != null)
            {
                movieEntry.MovieMetadata.Value.AlternativeTitles.Add(altTitle);
            }

            if (translation != null)
            {
                movieEntry.MovieMetadata.Value.Translations.Add(translation);
            }

            return movieEntry;
        }

        protected override List<Movie> Query(SqlBuilder builder)
        {
            var movieDictionary = new Dictionary<int, Movie>();

            _ = _database.QueryJoined<Movie, MovieMetadata, QualityProfile, MovieFile, AlternativeTitle>(
                builder,
                (movie, metadata, qualityProfile, file, altTitle) => Map(movieDictionary, movie, metadata, qualityProfile, file, altTitle));

            return movieDictionary.Values.ToList();
        }

        public override IEnumerable<Movie> All()
        {
            // the skips the join on profile and alternative title and populates manually
            // to avoid repeatedly deserializing the same profile / movie
            var builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Movie, MovieMetadata>((m, f) => m.MovieMetadataId == f.Id)
                .LeftJoin<Movie, MovieFile>((m, f) => m.MovieFileId == f.Id);

            var qualityProfiles = _profileRepository.All().ToDictionary(x => x.Id);
            var alternativeTitles = _alternativeTitleRepository.All()
                .GroupBy(x => x.MovieMetadataId)
                .ToDictionary(x => x.Key, y => y.ToList());

            return _database.QueryJoined<Movie, MovieMetadata, MovieFile>(
                builder,
                (movie, metadata, file) =>
                {
                    movie.MovieMetadata = metadata;
                    movie.MovieFile = file;
                    movie.QualityProfile = qualityProfiles[movie.QualityProfileId];

                    if (alternativeTitles.TryGetValue(movie.MovieMetadataId, out var altTitles))
                    {
                        movie.MovieMetadata.Value.AlternativeTitles = altTitles;
                    }

                    return movie;
                });
        }

        public bool MoviePathExists(string path)
        {
            return Query(x => x.Path == path).Any();
        }

        public List<Movie> FindByTitles(List<string> titles)
        {
            var distinct = titles.Distinct().ToList();

            var results = new List<Movie>();

            results.AddRange(FindByMovieTitles(distinct));
            results.AddRange(FindByAltTitles(distinct));
            results.AddRange(FindByTransTitles(distinct));

            return results.DistinctBy(x => x.Id).ToList();
        }

        // This is a bit of a hack, but if you try to combine / rationalise these then
        // SQLite makes a mess of the query plan and ends up doing a table scan
        private List<Movie> FindByMovieTitles(List<string> titles)
        {
            var movieDictionary = new Dictionary<int, Movie>();

            var builder = new SqlBuilder(_database.DatabaseType)
                .Join<Movie, QualityProfile>((m, p) => m.QualityProfileId == p.Id)
                .Join<Movie, MovieMetadata>((m, p) => m.MovieMetadataId == p.Id)
                .LeftJoin<Movie, MovieFile>((m, f) => m.MovieFileId == f.Id)
                .Where<MovieMetadata>(x => titles.Contains(x.CleanTitle) || titles.Contains(x.CleanOriginalTitle));

            _ = _database.QueryJoined<Movie, MovieMetadata, QualityProfile, MovieFile>(
                builder,
                (movie, metadata, qualityProfile, file) => Map(movieDictionary, movie, metadata, qualityProfile, file));

            return movieDictionary.Values.ToList();
        }

        private List<Movie> FindByAltTitles(List<string> titles)
        {
            var movieDictionary = new Dictionary<int, Movie>();

            var builder = new SqlBuilder(_database.DatabaseType)
                .Join<AlternativeTitle, MovieMetadata>((t, mm) => t.MovieMetadataId == mm.Id)
                .Join<MovieMetadata, Movie>((mm, m) => mm.Id == m.MovieMetadataId)
                .Join<Movie, QualityProfile>((m, p) => m.QualityProfileId == p.Id)
                .LeftJoin<Movie, MovieFile>((m, f) => m.MovieFileId == f.Id)
                .Where<AlternativeTitle>(x => titles.Contains(x.CleanTitle));

            _ = _database.QueryJoined<AlternativeTitle, QualityProfile, Movie, MovieMetadata, MovieFile>(
                builder,
                (altTitle, qualityProfile, movie, metadata, file) =>
                {
                    _ = Map(movieDictionary, movie, metadata, qualityProfile, file, altTitle);
                    return null;
                });

            return movieDictionary.Values.ToList();
        }

        private List<Movie> FindByTransTitles(List<string> titles)
        {
            var movieDictionary = new Dictionary<int, Movie>();

            var builder = new SqlBuilder(_database.DatabaseType)
                .Join<MovieTranslation, MovieMetadata>((t, mm) => t.MovieMetadataId == mm.Id)
                .Join<MovieMetadata, Movie>((mm, m) => mm.Id == m.MovieMetadataId)
                .Join<Movie, QualityProfile>((m, p) => m.QualityProfileId == p.Id)
                .LeftJoin<Movie, MovieFile>((m, f) => m.MovieFileId == f.Id)
                .Where<MovieTranslation>(x => titles.Contains(x.CleanTitle));

            _ = _database.QueryJoined<MovieTranslation, QualityProfile, Movie, MovieMetadata, MovieFile>(
                builder,
                (trans, qualityProfile, movie, metadata, file) =>
                {
                    _ = Map(movieDictionary, movie, metadata, qualityProfile, file, null, trans);
                    return null;
                });

            return movieDictionary.Values.ToList();
        }

        public Movie FindByImdbId(string imdbid)
        {
            var imdbIdWithPrefix = Parser.Parser.NormalizeImdbId(imdbid);
            return imdbIdWithPrefix == null ? null : Query(x => x.MovieMetadata.Value.ImdbId == imdbIdWithPrefix).FirstOrDefault();
        }

        public Movie FindByTmdbId(int tmdbid)
        {
            return Query(x => x.MovieMetadata.Value.TmdbId == tmdbid).FirstOrDefault();
        }

        public List<Movie> FindByTmdbId(List<int> tmdbids)
        {
            return Query(x => tmdbids.Contains(x.TmdbId));
        }

        public List<Movie> GetMoviesByFileId(int fileId)
        {
            return Query(x => x.MovieFileId == fileId);
        }

        public List<Movie> GetMoviesByCollectionTmdbId(int collectionId)
        {
            return Query(x => x.MovieMetadata.Value.CollectionTmdbId == collectionId);
        }

        public List<Movie> MoviesBetweenDates(DateTime start, DateTime end, bool includeUnmonitored)
        {
            var builder = Builder()
                .Where<Movie>(m =>
                              (m.MovieMetadata.Value.InCinemas >= start && m.MovieMetadata.Value.InCinemas <= end) ||
                              (m.MovieMetadata.Value.PhysicalRelease >= start && m.MovieMetadata.Value.PhysicalRelease <= end) ||
                              (m.MovieMetadata.Value.DigitalRelease >= start && m.MovieMetadata.Value.DigitalRelease <= end));

            if (!includeUnmonitored)
            {
                builder.Where<Movie>(x => x.Monitored == true);
            }

            return Query(builder);
        }

        public SqlBuilder MoviesWithoutFilesBuilder() => Builder()
            .Where("((NOT EXISTS (SELECT 1 FROM \"CompositeProfileVersions\" d WHERE d.\"CompositeProfileId\" = \"Movies\".\"QualityProfileId\") AND \"Movies\".\"MovieFileId\" = 0) OR EXISTS (SELECT 1 FROM \"CompositeProfileVersions\" d LEFT JOIN \"MovieVersions\" v ON v.\"CompositeProfileVersionId\" = d.\"Id\" AND v.\"MovieId\" = \"Movies\".\"Id\" WHERE d.\"CompositeProfileId\" = \"Movies\".\"QualityProfileId\" AND (v.\"Id\" IS NULL OR (v.\"Monitored\" = TRUE AND NOT EXISTS (SELECT 1 FROM \"MovieFiles\" f WHERE f.\"Id\" = v.\"MovieFileId\" AND f.\"MovieId\" = \"Movies\".\"Id\")))))")
            .Where<Movie>(m => m.MovieMetadata.Value.Year > 0)
            .GroupBy<Movie>(m => m.Id)
            .GroupBy<MovieMetadata>(m => m.Id);

        public PagingSpec<Movie> MoviesWithoutFiles(PagingSpec<Movie> pagingSpec)
        {
            pagingSpec.Records = GetPagedRecords(MoviesWithoutFilesBuilder(), pagingSpec, PagedQuery);
            pagingSpec.TotalRecords = GetPagedRecordCount(MoviesWithoutFilesBuilder().SelectCountDistinct<Movie>(x => x.Id), pagingSpec);

            return pagingSpec;
        }

        public SqlBuilder MoviesWhereCutoffUnmetBuilder(List<QualitiesBelowCutoff> qualitiesBelowCutoff) => Builder()
            .Where("((NOT EXISTS (SELECT 1 FROM \"CompositeProfileVersions\" d WHERE d.\"CompositeProfileId\" = \"Movies\".\"QualityProfileId\") AND \"Movies\".\"MovieFileId\" <> 0 AND " +
                BuildQualityCutoffWhereClause(qualitiesBelowCutoff, "\"Movies\".\"QualityProfileId\"", "\"MovieFiles\".\"Quality\"") +
                ") OR EXISTS (SELECT 1 FROM \"MovieVersions\" v JOIN \"CompositeProfileVersions\" d ON d.\"Id\" = v.\"CompositeProfileVersionId\" JOIN \"MovieFiles\" f ON f.\"Id\" = v.\"MovieFileId\" AND f.\"MovieId\" = v.\"MovieId\" WHERE v.\"MovieId\" = \"Movies\".\"Id\" AND d.\"CompositeProfileId\" = \"Movies\".\"QualityProfileId\" AND v.\"Monitored\" = TRUE AND " +
                BuildQualityCutoffWhereClause(qualitiesBelowCutoff, "d.\"QualityProfileId\"", "f.\"Quality\"") + "))")
            .GroupBy<Movie>(m => m.Id)
            .GroupBy<MovieMetadata>(m => m.Id);

        public PagingSpec<Movie> MoviesWhereCutoffUnmet(PagingSpec<Movie> pagingSpec, List<QualitiesBelowCutoff> qualitiesBelowCutoff)
        {
            pagingSpec.Records = GetPagedRecords(MoviesWhereCutoffUnmetBuilder(qualitiesBelowCutoff), pagingSpec, PagedQuery);
            pagingSpec.TotalRecords = GetPagedRecordCount(MoviesWhereCutoffUnmetBuilder(qualitiesBelowCutoff).SelectCountDistinct<Movie>(x => x.Id), pagingSpec);

            return pagingSpec;
        }

        private static string BuildQualityCutoffWhereClause(List<QualitiesBelowCutoff> qualitiesBelowCutoff, string profileColumn, string qualityColumn)
        {
            var clauses = new List<string>();

            foreach (var profile in qualitiesBelowCutoff)
            {
                foreach (var belowCutoff in profile.QualityIds)
                {
                    clauses.Add($"({profileColumn} = {profile.ProfileId} AND {qualityColumn} LIKE '%_quality_: {belowCutoff},%')");
                }
            }

            return clauses.Count == 0 ? "(1 = 0)" : string.Format("({0})", string.Join(" OR ", clauses));
        }

        public Movie FindByPath(string path)
        {
            return Query(x => x.Path == path).FirstOrDefault();
        }

        public Dictionary<int, string> AllMoviePaths()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS \"Key\", \"Path\" AS \"Value\" FROM \"Movies\"";
                return conn.Query<KeyValuePair<int, string>>(strSql).ToDictionary(x => x.Key, x => x.Value);
            }
        }

        public List<int> AllMovieTmdbIds()
        {
            using (var conn = _database.OpenConnection())
            {
                return conn.Query<int>("SELECT \"TmdbId\" FROM \"MovieMetadata\" JOIN \"Movies\" ON (\"Movies\".\"MovieMetadataId\" = \"MovieMetadata\".\"Id\")").ToList();
            }
        }

        public Dictionary<int, List<int>> AllMovieTags()
        {
            using (var conn = _database.OpenConnection())
            {
                var strSql = "SELECT \"Id\" AS \"Key\", \"Tags\" AS \"Value\" FROM \"Movies\" WHERE \"Tags\" IS NOT NULL";
                return conn.Query<KeyValuePair<int, List<int>>>(strSql).ToDictionary(x => x.Key, x => x.Value);
            }
        }

        public List<int> GetRecommendations()
        {
            var recommendations = new List<int>();

            if (_database.DatabaseType == DatabaseType.SQLite && _database.Version < new Version("3.9.0"))
            {
                return recommendations;
            }

            using (var conn = _database.OpenConnection())
            {
                if (_database.DatabaseType == DatabaseType.PostgreSQL)
                {
                    recommendations = conn.Query<int>(@"SELECT DISTINCT ""Rec"" FROM (
                                                    SELECT DISTINCT ""Rec"" FROM
                                                    (
                                                    SELECT DISTINCT CAST(""value"" AS INT) AS ""Rec"" FROM ""MovieMetadata"" JOIN ""Movies"" ON ""Movies"".""MovieMetadataId"" = ""MovieMetadata"".""Id"", json_array_elements_text((""MovieMetadata"".""Recommendations"")::json)
                                                    WHERE CAST(""value"" AS INT) NOT IN (SELECT ""TmdbId"" FROM ""MovieMetadata"" union SELECT ""TmdbId"" from ""ImportExclusions"" as sub1) LIMIT 10
                                                    ) as sub2
                                                    UNION
                                                    SELECT ""Rec"" FROM
                                                    (
                                                    SELECT CAST(""value"" AS INT) AS ""Rec"" FROM ""MovieMetadata"" JOIN ""Movies"" ON ""Movies"".""MovieMetadataId"" = ""MovieMetadata"".""Id"", json_array_elements_text((""MovieMetadata"".""Recommendations"")::json)
                                                    WHERE CAST(""value"" AS INT) NOT IN (SELECT ""TmdbId"" FROM ""MovieMetadata"" union SELECT ""TmdbId"" from ""ImportExclusions"" as sub2)
                                                    GROUP BY ""Rec"" ORDER BY count(*) DESC LIMIT 120
                                                    ) as sub4
                                                    ) as sub5
                                                    LIMIT 100;").ToList();
                }
                else
                {
                    recommendations = conn.Query<int>(@"SELECT DISTINCT ""Rec"" FROM (
                                                    SELECT DISTINCT ""Rec"" FROM
                                                    (
                                                    SELECT DISTINCT CAST(""j"".""value"" AS INT) AS ""Rec"" FROM ""MovieMetadata"" JOIN ""Movies"" ON ""Movies"".""MovieMetadataId"" == ""MovieMetadata"".""Id""
                                                    CROSS JOIN json_each(""MovieMetadata"".""Recommendations"") AS ""j""
                                                    WHERE ""Rec"" NOT IN (SELECT ""TmdbId"" FROM ""MovieMetadata"" union SELECT ""TmdbId"" from ""ImportExclusions"") LIMIT 10
                                                    )
                                                    UNION
                                                    SELECT ""Rec"" FROM
                                                    (
                                                    SELECT CAST(""j"".""value"" AS INT) AS ""Rec"" FROM ""MovieMetadata"" JOIN ""Movies"" ON ""Movies"".""MovieMetadataId"" == ""MovieMetadata"".""Id""
                                                    CROSS JOIN json_each(""MovieMetadata"".""Recommendations"") AS ""j""
                                                    WHERE ""Rec"" NOT IN (SELECT ""TmdbId"" FROM ""MovieMetadata"" union SELECT ""TmdbId"" from ""ImportExclusions"")
                                                    GROUP BY ""Rec"" ORDER BY count(*) DESC LIMIT 120
                                                    )
                                                    )
                                                    LIMIT 100;").ToList();
                }
            }

            return recommendations;
        }

        public bool ExistsByMetadataId(int metadataId)
        {
            return Query(x => x.MovieMetadataId == metadataId).Any();
        }

        public HashSet<int> AllMovieWithCollectionsTmdbIds()
        {
            using var conn = _database.OpenConnection();

            return conn.Query<int>("SELECT \"TmdbId\" FROM \"MovieMetadata\" JOIN \"Movies\" ON (\"Movies\".\"MovieMetadataId\" = \"MovieMetadata\".\"Id\") WHERE \"CollectionTmdbId\" > 0").ToHashSet();
        }
    }
}
