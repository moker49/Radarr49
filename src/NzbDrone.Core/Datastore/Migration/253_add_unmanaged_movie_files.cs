using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(253)]
    public class add_unmanaged_movie_files : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("MovieFiles").AddColumn("IsUnmanaged").AsBoolean().NotNullable().WithDefaultValue(false);
            Alter.Table("MovieFiles").AddColumn("ManagementProfileId").AsInt32().Nullable();
            Alter.Table("MovieVersions").AddColumn("Retired").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }
}
