using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedSubtitleFiles : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupOrphanedSubtitleFiles(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            DeleteOrphanedByMovie();
            DeleteOrphanedByMovieFile();
        }

        private void DeleteOrphanedByMovie()
        {
            using var mapper = _database.OpenConnection();
            mapper.Execute(@"DELETE FROM ""SubtitleFiles""
                             WHERE ""Id"" IN (
                             SELECT ""SubtitleFiles"".""Id"" FROM ""SubtitleFiles""
                             LEFT OUTER JOIN ""Movies""
                             ON ""SubtitleFiles"".""MovieId"" = ""Movies"".""Id""
                             WHERE ""Movies"".""Id"" IS NULL)");
        }

        private void DeleteOrphanedByMovieFile()
        {
            using var mapper = _database.OpenConnection();
            mapper.Execute(@"DELETE FROM ""SubtitleFiles""
                             WHERE ""Id"" IN (
                             SELECT ""SubtitleFiles"".""Id"" FROM ""SubtitleFiles""
                             LEFT OUTER JOIN ""MovieFiles""
                             ON ""SubtitleFiles"".""MovieFileId"" = ""MovieFiles"".""Id""
                             WHERE ""SubtitleFiles"".""MovieFileId"" > 0
                             AND ""MovieFiles"".""Id"" IS NULL
                             AND NOT EXISTS (SELECT 1 FROM ""VersionFileImports"" i WHERE i.""OldMovieFileId"" = ""SubtitleFiles"".""MovieFileId"" AND i.""MovieId"" = ""SubtitleFiles"".""MovieId"" AND i.""State"" BETWEEN 0 AND 10)
                             AND NOT EXISTS (SELECT 1 FROM ""VersionFileDeletions"" d WHERE d.""MovieFileId"" = ""SubtitleFiles"".""MovieFileId"" AND d.""MovieId"" = ""SubtitleFiles"".""MovieId"" AND d.""State"" IN (0, 1, 2, 3, 4)))");
        }
    }
}
