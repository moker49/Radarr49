using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(248)]
    public class add_download_reservations : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("DownloadReservations")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("MovieId").AsInt32().NotNullable()
                .WithColumn("MovieVersionId").AsInt32().NotNullable()
                .WithColumn("DownloadClientId").AsInt32().NotNullable()
                .WithColumn("DownloadId").AsString().Nullable()
                .WithColumn("SourceTitle").AsString().NotNullable()
                .WithColumn("ReleaseKeys").AsString().NotNullable()
                .WithColumn("State").AsInt32().NotNullable()
                .WithColumn("ImportedMovieFileId").AsInt32().Nullable()
                .WithColumn("Created").AsDateTime().NotNullable()
                .WithColumn("Updated").AsDateTime().NotNullable()
                .WithColumn("Note").AsString().Nullable();

            Create.Index().OnTable("DownloadReservations").OnColumn("MovieId").Ascending();
            Create.Index().OnTable("DownloadReservations").OnColumn("MovieVersionId").Ascending();
            Create.Index().OnTable("DownloadReservations").OnColumn("DownloadClientId").Ascending().OnColumn("DownloadId").Ascending();
            Execute.Sql("CREATE UNIQUE INDEX \"IX_DownloadReservations_ActiveVersion\" ON \"DownloadReservations\" (\"MovieVersionId\") WHERE \"State\" IN (0, 1, 2, 5)");
            Execute.Sql("CREATE UNIQUE INDEX \"IX_DownloadReservations_ActiveDownload\" ON \"DownloadReservations\" (\"MovieId\", \"DownloadClientId\", UPPER(\"DownloadId\")) WHERE \"DownloadId\" IS NOT NULL AND \"State\" IN (0, 1, 2, 5)");
        }
    }
}
