using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Profiles.Quality;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class QualityProfileSizeScopeFixture : TestBase<QualityProfileController>
    {
        [Test]
        public void should_identify_composite_parents_while_leaving_child_profiles_editable()
        {
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.All()).Returns(new List<QualityProfile>
            {
                new QualityProfile { Id = 4, Name = "SDR", Items = new List<QualityProfileQualityItem>() },
                new QualityProfile { Id = 5, Name = "HDR", Items = new List<QualityProfileQualityItem>() },
                new QualityProfile { Id = 7, Name = "SDR+HDR", Items = new List<QualityProfileQualityItem>() }
            });
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(r => r.All()).Returns(new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { Id = 1, CompositeProfileId = 7, QualityProfileId = 4 },
                new CompositeProfileVersion { Id = 2, CompositeProfileId = 7, QualityProfileId = 5 }
            });

            var profiles = Subject.GetAll();

            Assert.That(profiles.Where(p => p.IsComposite).Select(p => p.Id), Is.EqualTo(new[] { 7 }));
            Assert.That(profiles.Where(p => !p.IsComposite).Select(p => p.Id), Is.EqualTo(new[] { 4, 5 }));
        }
    }
}
