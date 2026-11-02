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
                             WHERE ""Type"" = 1 AND ""Id"" NOT IN (
                                 SELECT MAX(""Id"") FROM ""MetadataFiles""
                                 WHERE ""Type"" = 1
                                 GROUP BY ""MovieId"", ""MovieFileId"", ""Consumer"", ""RelativePath""
                             )");
        }
    }
}
