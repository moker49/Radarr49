using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(250)]
    public class add_version_file_imports : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("VersionFileImports")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("MovieVersionId").AsInt32().NotNullable()
                .WithColumn("DownloadReservationId").AsInt32().Nullable()
                .WithColumn("OldMovieFileId").AsInt32().Nullable()
                .WithColumn("NewMovieFileId").AsInt32().Nullable()
                .WithColumn("MoviePath").AsString().NotNullable()
                .WithColumn("SourcePath").AsString().NotNullable()
                .WithColumn("DestinationRelativePath").AsString().NotNullable()
                .WithColumn("StagingRelativePath").AsString().NotNullable()
                .WithColumn("BackupRelativePath").AsString().Nullable()
                .WithColumn("CopyOnly").AsBoolean().NotNullable()
                .WithColumn("State").AsInt32().NotNullable()
                .WithColumn("Snapshot").AsString().NotNullable()
                .WithColumn("Created").AsDateTime().NotNullable()
                .WithColumn("Updated").AsDateTime().NotNullable()
                .WithColumn("Note").AsString().Nullable();
            Create.Index().OnTable("VersionFileImports").OnColumn("MovieId").Ascending();
            Execute.Sql("CREATE UNIQUE INDEX \"IX_VersionFileImports_ActiveVersion\" ON \"VersionFileImports\" (\"MovieVersionId\") WHERE \"State\" BETWEEN 0 AND 10");
        }
    }
}
