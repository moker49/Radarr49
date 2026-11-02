using System.Data.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class add_composite_profile_versionsFixture : MigrationTest<add_composite_profile_versions>
    {
        [Test]
        public void should_add_empty_tables_without_rewriting_existing_profiles()
        {
            using var connection = WithDapperMigrationTestDb(m =>
            {
                m.Insert.IntoTable("QualityProfiles").Row(new
                {
                    Id = 123,
                    Name = "Existing SDR",
                    Cutoff = 1,
                    Items = "[]",
                    UpgradeAllowed = true,
                    Language = 1,
                    FormatItems = "[]",
                    MinFormatScore = 0,
                    CutoffFormatScore = 0,
                    MinUpgradeFormatScore = 1
                });
            });

            Assert.That(connection.ExecuteScalar<string>("SELECT \"Name\" FROM \"QualityProfiles\" WHERE \"Id\" = 123"), Is.EqualTo("Existing SDR"));
            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"CompositeProfileVersions\""), Is.Zero);
            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\""), Is.Zero);
        }

        [Test]
        public void should_allow_multiple_missing_versions()
        {
            using var connection = WithDapperMigrationTestDb();

            connection.Execute("INSERT INTO \"MovieVersions\" (\"MovieId\", \"CompositeProfileVersionId\", \"Monitored\") VALUES (1, 101, @monitored), (1, 102, @monitored)", new { monitored = true });

            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" WHERE \"MovieFileId\" IS NULL"), Is.EqualTo(2));
        }

        [Test]
        public void should_prevent_assigning_one_file_to_multiple_versions()
        {
            using var connection = WithDapperMigrationTestDb();

            connection.Execute("INSERT INTO \"MovieVersions\" (\"MovieId\", \"CompositeProfileVersionId\", \"Monitored\", \"MovieFileId\") VALUES (1, 101, @monitored, 50)", new { monitored = true });

            Assert.That(() => connection.Execute("INSERT INTO \"MovieVersions\" (\"MovieId\", \"CompositeProfileVersionId\", \"Monitored\", \"MovieFileId\") VALUES (1, 102, @monitored, 50)", new { monitored = true }), Throws.InstanceOf<DbException>());
        }

        [Test]
        public void should_prevent_duplicate_movie_version_assignments()
        {
            using var connection = WithDapperMigrationTestDb();
            const string insert = "INSERT INTO \"MovieVersions\" (\"MovieId\", \"CompositeProfileVersionId\", \"Monitored\") VALUES (1, 101, @monitored)";
            connection.Execute(insert, new { monitored = true });

            Assert.That(() => connection.Execute(insert, new { monitored = true }), Throws.InstanceOf<DbException>());
        }
    }
}
