using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(249)]
    public class add_version_file_deletions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("VersionFileDeletions")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("MovieVersionId").AsInt32().NotNullable()
                .WithColumn("MovieFileId").AsInt32().NotNullable()
                .WithColumn("MoviePath").AsString().NotNullable()
                .WithColumn("Unmonitor").AsBoolean().NotNullable()
                .WithColumn("State").AsInt32().NotNullable()
                .WithColumn("Snapshot").AsString().NotNullable()
                .WithColumn("ExtraIndex").AsInt32().NotNullable()
                .WithColumn("RecyclePath").AsString().Nullable()
                .WithColumn("Created").AsDateTime().NotNullable()
                .WithColumn("Updated").AsDateTime().NotNullable()
                .WithColumn("Note").AsString().Nullable();
            Create.Index().OnTable("VersionFileDeletions").OnColumn("MovieId").Ascending();
            Execute.Sql("CREATE UNIQUE INDEX \"IX_VersionFileDeletions_ActiveVersion\" ON \"VersionFileDeletions\" (\"MovieVersionId\") WHERE \"State\" IN (0, 1, 2, 3, 4)");
        }
    }
}
