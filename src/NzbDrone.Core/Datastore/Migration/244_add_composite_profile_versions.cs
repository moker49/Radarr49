using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(244)]
    public class add_composite_profile_versions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Additive only: existing profiles, movies and files remain untouched.
            Create.Table("CompositeProfileVersions")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("CompositeProfileId").AsInt32().NotNullable()
                .WithColumn("QualityProfileId").AsInt32().NotNullable()
                .WithColumn("Label").AsString().NotNullable()
                .WithColumn("Priority").AsInt32().NotNullable()
                .WithColumn("IsPrimary").AsBoolean().NotNullable();

            Create.Index().OnTable("CompositeProfileVersions")
                .OnColumn("CompositeProfileId").Ascending()
                .OnColumn("Priority").Ascending()
                .WithOptions().Unique();

            Create.Table("MovieVersions")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("CompositeProfileVersionId").AsInt32().NotNullable()
                .WithColumn("Monitored").AsBoolean().NotNullable()
                .WithColumn("MovieFileId").AsInt32().Nullable();

            Create.Index().OnTable("MovieVersions")
                .OnColumn("MovieId").Ascending()
                .OnColumn("CompositeProfileVersionId").Ascending()
                .WithOptions().Unique();

            Create.Index().OnTable("MovieVersions")
                .OnColumn("MovieFileId").Ascending()
                .WithOptions().Unique();
        }
    }
}
