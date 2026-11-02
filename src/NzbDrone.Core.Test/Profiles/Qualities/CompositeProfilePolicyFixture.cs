using System.Collections.Generic;
using System.Linq;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles.Qualities
{
    [TestFixture]
    public class CompositeProfilePolicyFixture : CoreTest<CompositeProfilePolicy>
    {
        [SetUp]
        public void Setup()
        {
            var definitions = new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { Id = 101, CompositeProfileId = 10, QualityProfileId = 1 }
            };
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.All()).Returns(definitions);
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.ForProfile(It.IsAny<int>()))
                .Returns((int id) => definitions.Where(v => v.CompositeProfileId == id).ToList());
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.ForMovie(It.IsAny<int>()))
                .Returns((int id) => id == 3 ? new List<MovieVersion> { new MovieVersion { MovieId = 3 } } : new List<MovieVersion>());
        }

        [Test]
        public void ordinary_profiles_remain_assignable()
        {
            Assert.DoesNotThrow(() => Subject.EnsureAssignable(1));
        }

        [Test]
        public void draft_composite_assignment_returns_conflict()
        {
            var exception = Assert.Throws<NzbDroneClientException>(() => Subject.EnsureAssignable(10));
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        }

        [Test]
        public void ordinary_legacy_mutations_remain_allowed()
        {
            Assert.DoesNotThrow(() => Subject.EnsureLegacyMutationAllowed(new[] { new Movie { Id = 1, QualityProfileId = 1 } }));
        }

        [Test]
        public void mixed_legacy_requests_should_be_rejected()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureLegacyMutationAllowed(new[]
            {
                new Movie { Id = 1, QualityProfileId = 1 },
                new Movie { Id = 2, QualityProfileId = 10 }
            }));
        }

        [Test]
        public void existing_version_assignments_protect_movie_even_if_profile_was_changed()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureLegacyMutationAllowed(new[] { new Movie { Id = 3, QualityProfileId = 1 } }));
        }

        [TestCase(1)]
        [TestCase(10)]
        public void parent_and_child_profiles_cannot_be_deleted(int profileId)
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureProfileDeletionAllowed(profileId));
        }

        [Test]
        public void unrelated_profiles_can_be_deleted()
        {
            Assert.DoesNotThrow(() => Subject.EnsureProfileDeletionAllowed(20));
        }
    }
}
