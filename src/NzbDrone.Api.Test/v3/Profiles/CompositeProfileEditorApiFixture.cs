using NUnit.Framework;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Profiles.Quality;
using Radarr.Http.REST;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class CompositeProfileEditorApiFixture : TestBase<QualityProfileController>
    {
        [Test]
        public void editor_update_must_reject_mismatched_route_and_resource_ids()
        {
            Assert.Throws<BadRequestException>(() => Subject.UpdateEditor(7, new QualityProfileResource { Id = 8 }));
        }
    }
}
