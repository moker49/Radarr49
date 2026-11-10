using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class ManagedMovieDeletionApiFixture : TestBase<ManagedMovieDeletionController>
    {
        [TestCase(false)]
        [TestCase(true)]
        public void explicit_delete_should_pass_file_and_exclusion_choices_to_version_aware_service(bool deleteFiles)
        {
            Assert.That(Subject.Delete(19, deleteFiles, true), Is.TypeOf<NoContentResult>());

            Mocker.GetMock<IManagedMovieDeletionService>().Verify(s => s.Delete(It.Is<List<int>>(ids => ids.SequenceEqual(new[] { 19 })), deleteFiles, true), Times.Once());
        }

        [Test]
        public void bulk_delete_should_use_the_version_aware_service()
        {
            var request = new ManagedMovieDeletionResource { MovieIds = new List<int> { 19, 64 }, DeleteFiles = true };

            Assert.That(Subject.DeleteMany(request), Is.TypeOf<NoContentResult>());

            Mocker.GetMock<IManagedMovieDeletionService>().Verify(s => s.Delete(request.MovieIds, true, false), Times.Once());
        }

        [Test]
        public void empty_bulk_delete_should_not_invoke_deletion()
        {
            Assert.That(Subject.DeleteMany(new ManagedMovieDeletionResource()), Is.TypeOf<BadRequestObjectResult>());

            Mocker.GetMock<IManagedMovieDeletionService>().Verify(s => s.Delete(It.IsAny<List<int>>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
