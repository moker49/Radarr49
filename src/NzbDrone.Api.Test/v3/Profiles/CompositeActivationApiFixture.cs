using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Profiles.Quality;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class CompositeActivationApiFixture : TestBase<CompositeProfileController>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICompositeProfileService>().Setup(s => s.Get(10)).Returns(new List<CompositeProfileVersion>());
            Mocker.GetMock<ICompositeProfileService>().Setup(s => s.GetMaximumConcurrentDownloads(10)).Returns(2);
        }

        [Test]
        public void legacy_limit_one_should_return_two_independent_slots()
        {
            var definitions = new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { QualityProfileId = 4, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { QualityProfileId = 5, Label = "HDR", Priority = 1 }
            };
            Mocker.GetMock<ICompositeProfileService>().Setup(s => s.Save(10, definitions, 1)).Returns(definitions);

            var resource = Subject.Save(10, new CompositeProfileResource { Versions = definitions, MaximumConcurrentDownloads = 1 });

            Assert.That(resource.MaximumConcurrentDownloads, Is.EqualTo(2));
            Assert.That(resource.MaximumConcurrentDownloadsPerVersion, Is.EqualTo(1));
        }

        [Test]
        public void should_expose_activation_and_root_after_activation()
        {
            Mocker.GetMock<ICompositeProfileService>().Setup(s => s.GetTestRoot(10)).Returns("C:/CompositeTest");
            var resource = Subject.Activate(10, new CompositeActivationResource { RootFolderPath = "C:/CompositeTest" });

            Mocker.GetMock<ICompositeProfileService>().Verify(s => s.SetTestRoot(10, "C:/CompositeTest"), Times.Once());
            Assert.That(resource.IsDraft, Is.False);
            Assert.That(resource.TestRootFolderPath, Is.EqualTo("C:/CompositeTest"));
        }

        [Test]
        public void should_reject_missing_activation_body_instead_of_deactivating()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.Activate(10, null));
            Mocker.GetMock<ICompositeProfileService>().Verify(s => s.SetTestRoot(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_deactivate_through_explicit_endpoint()
        {
            Assert.That(Subject.Deactivate(10).IsDraft, Is.True);
            Mocker.GetMock<ICompositeProfileService>().Verify(s => s.SetTestRoot(10, null), Times.Once());
        }
    }
}
