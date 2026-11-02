using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Test.Profiles.Qualities
{
    [TestFixture]
    public class ProfileSizeDefinitionFixture
    {
        [Test]
        public void should_resolve_profiles_independently_without_modifying_global_definition()
        {
            var global = new QualityDefinition(Quality.WEBDL1080p) { MinSize = 1, PreferredSize = 10, MaxSize = 20 };
            var sdr = new QualityProfile
            {
                SizeDefinitions = new List<ProfileSizeDefinition>
                {
                    new ProfileSizeDefinition { QualityId = Quality.WEBDL1080p.Id, MinSize = 2, PreferredSize = 5, MaxSize = 8 }
                }
            };
            var hdr = new QualityProfile
            {
                SizeDefinitions = new List<ProfileSizeDefinition>
                {
                    new ProfileSizeDefinition { QualityId = Quality.WEBDL1080p.Id, MinSize = 15, PreferredSize = 25, MaxSize = null }
                }
            };

            Assert.That(sdr.GetSizeDefinition(Quality.WEBDL1080p, global).PreferredSize, Is.EqualTo(5));
            Assert.That(hdr.GetSizeDefinition(Quality.WEBDL1080p, global).PreferredSize, Is.EqualTo(25));
            Assert.That(hdr.GetSizeDefinition(Quality.WEBDL1080p, global).MaxSize, Is.Null);
            Assert.That(global.PreferredSize, Is.EqualTo(10));
            Assert.That(global.MaxSize, Is.EqualTo(20));
        }

        [Test]
        public void unconfigured_quality_should_keep_global_settings()
        {
            var global = new QualityDefinition(Quality.WEBDL1080p) { PreferredSize = 10 };
            var profile = new QualityProfile { SizeDefinitions = new List<ProfileSizeDefinition>() };

            Assert.That(profile.GetSizeDefinition(Quality.WEBDL1080p, global), Is.SameAs(global));
        }

        [TestCase(1, 5, 10, true)]
        [TestCase(1, null, 10, true)]
        [TestCase(1, 5, null, true)]
        [TestCase(1, 5, 0, true)]
        [TestCase(10, null, 5, false)]
        [TestCase(10, 5, 20, false)]
        [TestCase(1, 20, 10, false)]
        [TestCase(-1, 5, 10, false)]
        [TestCase(1, 5, 2001, false)]
        [TestCase(double.NaN, 5, 10, false)]
        [TestCase(1, double.PositiveInfinity, null, false)]
        public void should_validate_size_bounds(double? minimum, double? preferred, double? maximum, bool valid)
        {
            var definition = new ProfileSizeDefinition
            {
                QualityId = Quality.WEBDL1080p.Id, MinSize = minimum, PreferredSize = preferred, MaxSize = maximum
            };

            Assert.That(new ProfileSizeDefinitionValidator().Validate(definition).IsValid, Is.EqualTo(valid));
        }

        [Test]
        public void should_reject_duplicate_quality_overrides()
        {
            var definitions = new List<ProfileSizeDefinition>
            {
                new ProfileSizeDefinition { QualityId = Quality.WEBDL1080p.Id },
                new ProfileSizeDefinition { QualityId = Quality.WEBDL1080p.Id }
            };

            Assert.That(new ProfileSizeDefinitionsValidator().Validate(definitions).IsValid, Is.False);
        }

        [Test]
        public void should_reject_unknown_quality()
        {
            Assert.That(new ProfileSizeDefinitionValidator().Validate(new ProfileSizeDefinition { QualityId = -1 }).IsValid, Is.False);
        }
    }
}
