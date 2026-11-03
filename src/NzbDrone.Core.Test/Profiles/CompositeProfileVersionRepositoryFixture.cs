using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class CompositeProfileVersionRepositoryFixture : DbTest<CompositeProfileVersionRepository, CompositeProfileVersion>
    {
        [SetUp]
        public void Setup()
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("INSERT INTO \"QualityProfiles\" (\"Id\", \"Name\", \"Cutoff\", \"Items\", \"MinFormatScore\", \"CutoffFormatScore\", \"FormatItems\", \"Language\", \"UpgradeAllowed\", \"MinUpgradeFormatScore\") VALUES (10, 'Composite', 0, '[]', 0, 0, '[]', 0, 0, 0)");
        }

        private static List<CompositeProfileVersion> Definitions()
        {
            return new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 }
            };
        }

        [Test]
        public void should_save_concurrency_with_definition_configuration()
        {
            var profile = new QualityProfile { Name = "Other composite", Items = new List<QualityProfileQualityItem>() };
            Db.Insert(profile);
            var definitions = Definitions();
            definitions.ForEach(v => v.CompositeProfileId = profile.Id);

            Subject.SaveDefinitions(profile.Id, definitions, 2);

            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            Assert.That(connection.ExecuteScalar<int>("SELECT \"MaximumConcurrentDownloads\" FROM \"QualityProfiles\" WHERE \"Id\" = @Id", profile), Is.EqualTo(2));
        }

        [Test]
        public void active_definitions_cannot_be_edited_or_deleted()
        {
            var definitions = Definitions();
            Subject.SaveDefinitions(10, definitions);
            Subject.SetTestRoot(10, "C:/CompositeTest");

            Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(() => Subject.SaveDefinitions(10, definitions));
            Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(() => Subject.DeleteDefinitions(10));
            Subject.SetTestRoot(10, null);
            Assert.DoesNotThrow(() => Subject.DeleteDefinitions(10));
        }

        [Test]
        public void reorder_should_preserve_ids()
        {
            var definitions = Definitions();
            Subject.SaveDefinitions(10, definitions);
            var ids = definitions.Select(v => v.Id).ToArray();
            definitions[0].Priority = 1;
            definitions[1].Priority = 0;

            Subject.SaveDefinitions(10, definitions);

            Assert.That(Subject.ForProfile(10).OrderBy(v => v.Priority).Select(v => v.Id), Is.EqualTo(ids.Reverse()));
        }

        [Test]
        public void failed_save_should_roll_back_all_priorities_and_labels()
        {
            var definitions = Definitions();
            Subject.SaveDefinitions(10, definitions);
            definitions[0].Label = "Changed";
            definitions[0].Priority = 1;

            Assert.That(() => Subject.SaveDefinitions(10, definitions), Throws.Exception);
            var stored = Subject.ForProfile(10).OrderBy(v => v.Priority).ToList();
            Assert.That(stored.Select(v => v.Label), Is.EqualTo(new[] { "SDR", "HDR" }));
            Assert.That(stored.Select(v => v.Priority), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void stale_save_should_not_replace_existing_definitions()
        {
            Subject.SaveDefinitions(10, Definitions());

            Assert.Throws<InvalidOperationException>(() => Subject.SaveDefinitions(10, Definitions()));
            Assert.That(Subject.ForProfile(10).Count, Is.EqualTo(2));
        }
    }
}
