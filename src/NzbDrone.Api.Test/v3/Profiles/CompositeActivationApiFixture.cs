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
            Mocker.GetMock<ICompositeProfileService>().Setup(s => s.GetMaximumConcurrentDownloads(10)).Returns(1);
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
