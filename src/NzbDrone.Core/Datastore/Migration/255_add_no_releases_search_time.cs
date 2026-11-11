using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(255)]
    public class add_no_releases_search_time : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("Movies").AddColumn("LastNoReleasesSearchTime").AsDateTime().Nullable();
        }
    }
}
