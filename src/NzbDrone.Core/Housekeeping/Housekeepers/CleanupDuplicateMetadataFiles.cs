using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupDuplicateMetadataFiles : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupDuplicateMetadataFiles(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();
            mapper.Execute(@"DELETE FROM ""MetadataFiles""
                             WHERE ""Type"" = 1
                             AND NOT EXISTS (SELECT 1 FROM ""VersionFileDeletions"" d WHERE d.""MovieId"" = ""MetadataFiles"".""MovieId"" AND d.""MovieFileId"" = ""MetadataFiles"".""MovieFileId"" AND d.""State"" IN (0, 1, 2, 3, 4))
                             AND ""Id"" NOT IN (
                                 SELECT MAX(""Id"") FROM ""MetadataFiles""
                                 WHERE ""Type"" = 1
                                 GROUP BY ""MovieId"", ""MovieFileId"", ""Consumer"", ""RelativePath""
                             )");
        }
    }
}
