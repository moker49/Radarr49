using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(252)]
    public class add_profile_global_size_preference : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("QualityProfiles").AddColumn("UseGlobalSizeDefinitions").AsBoolean().Nullable();
        }
    }
}
