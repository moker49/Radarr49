using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class CompositeProfileVersionRepositoryFixture : DbTest<CompositeProfileVersionRepository, CompositeProfileVersion>
    {
        private static List<CompositeProfileVersion> Definitions()
        {
            return new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 }
            };
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
