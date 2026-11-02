using System;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class add_download_movie_version_identityFixture : MigrationTest<add_download_movie_version_identity>
    {
        [Test]
        public void existing_history_and_pending_rows_should_remain_unversioned()
        {
            using var connection = WithDapperMigrationTestDb(m =>
            {
                m.Insert.IntoTable("History").Row(new
                {
                    MovieId = 1, SourceTitle = "Legacy", Quality = "{}", Languages = "[]", Date = DateTime.UtcNow,
                    EventType = 1, Data = "{}", DownloadId = "legacy-download"
                });
                m.Insert.IntoTable("DownloadHistory").Row(new
                {
                    MovieId = 1, SourceTitle = "Legacy", Date = DateTime.UtcNow, EventType = 1, Protocol = 1,
                    IndexerId = 1, DownloadClientId = 1, Release = "{}", Data = "{}", DownloadId = "legacy-download"
                });
                m.Insert.IntoTable("PendingReleases").Row(new
                {
                    MovieId = 1, Title = "Legacy", Added = DateTime.UtcNow, ParsedMovieInfo = "{}", Release = "{}",
                    Reason = 1, AdditionalInfo = "{}"
                });
            });

            foreach (var table in new[] { "History", "DownloadHistory", "PendingReleases" })
            {
                Assert.That(connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM \"{table}\" WHERE \"MovieVersionId\" IS NULL"), Is.EqualTo(1));
            }
        }
    }
}
