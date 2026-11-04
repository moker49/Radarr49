using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.Profiles.Quality;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class QualityProfileSizeResourceFixture
    {
        [Test]
        public void should_roundtrip_profile_size_definitions()
        {
            var profile = new QualityProfile
            {
                Items = new List<QualityProfileQualityItem>(),
                UseGlobalSizeDefinitions = true,
                SizeDefinitions = new List<ProfileSizeDefinition>
                {
                    new ProfileSizeDefinition { QualityId = Quality.WEBDL1080p.Id, MinSize = 2, PreferredSize = 5, MaxSize = null }
                }
            };

            var resource = profile.ToResource();
            var restored = resource.ToModel();

            Assert.That(restored.SizeDefinitions.Count, Is.EqualTo(1));
            Assert.That(restored.UseGlobalSizeDefinitions, Is.True);
            Assert.That(restored.SizeDefinitions[0].QualityId, Is.EqualTo(Quality.WEBDL1080p.Id));
            Assert.That(restored.SizeDefinitions[0].MinSize, Is.EqualTo(2));
            Assert.That(restored.SizeDefinitions[0].PreferredSize, Is.EqualTo(5));
            Assert.That(restored.SizeDefinitions[0].MaxSize, Is.Null);
        }

        [Test]
        public void omitted_legacy_field_should_remain_null_for_preservation_on_update()
        {
            var resource = new QualityProfileResource
            {
                Items = new List<QualityProfileQualityItemResource>(),
                FormatItems = new List<ProfileFormatItemResource>()
            };

            Assert.That(resource.ToModel().SizeDefinitions, Is.Null);
            Assert.That(resource.ToModel().UseGlobalSizeDefinitions, Is.Null);
        }
    }
}
