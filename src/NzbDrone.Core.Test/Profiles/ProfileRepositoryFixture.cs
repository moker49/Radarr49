using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.CustomFormats;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class ProfileRepositoryFixture : DbTest<QualityProfileRepository, QualityProfile>
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void should_be_able_to_read_and_write()
        {
            var profile = new QualityProfile
            {
                Items = NzbDrone.Core.Test.Qualities.QualityFixture.GetDefaultQualities(Quality.Bluray1080p, Quality.DVD, Quality.HDTV720p),
                MinFormatScore = 0,
                FormatItems = CustomFormatsTestHelpers.GetDefaultFormatItems(),
                Cutoff = Quality.Bluray1080p.Id,
                Name = "TestProfile"
            };

            profile.SizeDefinitions = new List<ProfileSizeDefinition>
            {
                new ProfileSizeDefinition { QualityId = Quality.Bluray1080p.Id, MinSize = 5, PreferredSize = 10, MaxSize = null }
            };

            Subject.Insert(profile);

            StoredModel.Name.Should().Be(profile.Name);
            StoredModel.Cutoff.Should().Be(profile.Cutoff);
            StoredModel.SizeDefinitions.Should().BeEquivalentTo(profile.SizeDefinitions);

            StoredModel.Items.Should().Equal(profile.Items, (a, b) => a.Quality == b.Quality && a.Allowed == b.Allowed);
        }
    }
}
