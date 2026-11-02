using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(246)]
    public class add_download_movie_version_identity : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            foreach (var table in new[] { "History", "DownloadHistory", "PendingReleases" })
            {
                Alter.Table(table).AddColumn("MovieVersionId").AsInt32().Nullable();
                Create.Index().OnTable(table).OnColumn("MovieVersionId").Ascending();
            }
        }
    }
}
