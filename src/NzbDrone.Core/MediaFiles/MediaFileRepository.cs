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
    public interface IMediaFileRepository : IBasicRepository<MovieFile>
    {
        List<MovieFile> GetFilesByMovie(int movieId);
        List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds);
        List<MovieFile> GetFilesWithoutMediaInfo();
        void DeleteForMovies(List<int> movieIds);

        List<MovieFile> GetFilesWithRelativePath(int movieId, string relativePath);
        MovieFile InsertForVersion(MovieFile movieFile, int movieVersionId);
        int? DeleteForVersion(MovieFile movieFile, bool unmonitor);
    }

    public class MediaFileRepository : BasicRepository<MovieFile>, IMediaFileRepository
    {
        public MediaFileRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieFile> GetFilesByMovie(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public MovieFile InsertForVersion(MovieFile movieFile, int movieVersionId)
        {
            if (movieFile.Id != 0)
            {
                throw new InvalidOperationException("A new movie file is required.");
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockMovie(connection, transaction, movieFile.MovieId);
            var version = connection.QuerySingleOrDefault<MovieVersion>("SELECT v.* FROM \"MovieVersions\" v JOIN \"Movies\" m ON m.\"Id\" = v.\"MovieId\" JOIN \"CompositeProfileVersions\" d ON d.\"Id\" = v.\"CompositeProfileVersionId\" WHERE v.\"Id\" = @movieVersionId AND v.\"MovieId\" = @MovieId AND d.\"CompositeProfileId\" = m.\"QualityProfileId\"", new { movieVersionId, movieFile.MovieId }, transaction);
            if (version == null || version.MovieFileId.HasValue)
            {
                throw new InvalidOperationException("The target version changed or already owns a file. Re-evaluate the import.");
            }

            if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieFiles\" WHERE \"MovieId\" = @MovieId AND LOWER(\"RelativePath\") = LOWER(@RelativePath)", movieFile, transaction) != 0)
            {
                throw new InvalidOperationException("The destination path already belongs to a movie file.");
            }

            try
            {
                Insert(connection, transaction, movieFile);
                connection.Execute("UPDATE \"MovieVersions\" SET \"MovieFileId\" = @fileId WHERE \"Id\" = @movieVersionId", new { fileId = movieFile.Id, movieVersionId }, transaction);
                connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = @fileId WHERE \"Id\" = @MovieId AND EXISTS (SELECT 1 FROM \"CompositeProfileVersions\" WHERE \"Id\" = @definitionId AND \"IsPrimary\" = @primary)", new { fileId = movieFile.Id, movieFile.MovieId, definitionId = version.CompositeProfileVersionId, primary = true }, transaction);
                transaction.Commit();
            }
            catch
            {
                movieFile.Id = 0;
                throw;
            }

            movieFile.MovieVersionId = movieVersionId;
            ModelCreated(movieFile);
            return movieFile;
        }

        public int? DeleteForVersion(MovieFile movieFile, bool unmonitor)
        {
            using var connection = _database.OpenConnection();
            var versionId = connection.QuerySingleOrDefault<int?>("SELECT \"Id\" FROM \"MovieVersions\" WHERE \"MovieId\" = @MovieId AND \"MovieFileId\" = @Id", movieFile);
            if (!versionId.HasValue)
            {
                return null;
            }

            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            LockMovie(connection, transaction, movieFile.MovieId);
            versionId = connection.QuerySingleOrDefault<int?>("SELECT \"Id\" FROM \"MovieVersions\" WHERE \"MovieId\" = @MovieId AND \"MovieFileId\" = @Id", movieFile, transaction);
            if (!versionId.HasValue)
            {
                return null;
            }

            connection.Execute("UPDATE \"MovieVersions\" SET \"MovieFileId\" = NULL WHERE \"Id\" = @versionId", new { versionId }, transaction);
            if (unmonitor)
            {
                connection.Execute("UPDATE \"MovieVersions\" SET \"Monitored\" = @monitored WHERE \"Id\" = @versionId", new { monitored = false, versionId }, transaction);
            }

            connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = 0 WHERE \"Id\" = @MovieId AND \"MovieFileId\" = @Id", movieFile, transaction);
            connection.Execute("DELETE FROM \"MovieFiles\" WHERE \"Id\" = @Id AND \"MovieId\" = @MovieId", movieFile, transaction);
            transaction.Commit();
            return versionId;
        }

        private static void LockMovie(IDbConnection connection, IDbTransaction transaction, int movieId)
        {
            if (connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = \"MovieFileId\" WHERE \"Id\" = @movieId", new { movieId }, transaction) != 1)
            {
                throw new InvalidOperationException("Movie no longer exists.");
            }
        }

        public List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds)
        {
            return Query(x => movieIds.Contains(x.MovieId));
        }

        public List<MovieFile> GetFilesWithoutMediaInfo()
        {
            return Query(x => x.MediaInfo == null);
        }

        public void DeleteForMovies(List<int> movieIds)
        {
            Delete(x => movieIds.Contains(x.MovieId));
        }

        public List<MovieFile> GetFilesWithRelativePath(int movieId, string relativePath)
        {
            return Query(c => c.MovieId == movieId && c.RelativePath == relativePath);
        }
    }
}
