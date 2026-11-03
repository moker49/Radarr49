using System.Collections.Generic;
using System.Linq;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
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

        private Movie ActivatedMovie()
        {
            Mocker.GetMock<IQualityProfileRepository>().Setup(s => s.Get(10)).Returns(new QualityProfile { CompositeTestRoot = "C:/CompositeTest" });
            Mocker.GetMock<IRootFolderService>().Setup(s => s.All()).Returns(new List<RootFolder> { new RootFolder { Path = "C:/CompositeTest" } });
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists("C:/CompositeTest")).Returns(true);
            return new Movie { QualityProfileId = 10, Path = "C:/CompositeTest/Movie" };
        }

        [Test]
        public void active_new_movie_in_test_root_is_allowed()
        {
            Assert.That(Subject.EnsureNewMovieAllowed(ActivatedMovie()), Is.True);
        }

        [TestCase("C:/Other/Movie")]
        [TestCase("C:/CompositeTest")]
        [TestCase("C:/CompositeTestSibling/Movie")]
        [TestCase("C:/CompositeTest/../Other/Movie")]
        [TestCase("relative/Movie")]
        public void active_movie_outside_test_root_is_rejected(string path)
        {
            var movie = ActivatedMovie();
            movie.Path = path;
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureNewMovieAllowed(movie));
        }

        [Test]
        public void existing_folder_with_any_files_is_rejected()
        {
            var movie = ActivatedMovie();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(movie.Path)).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFiles(movie.Path, true)).Returns(new[] { movie.Path + "/existing.mkv" });
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureNewMovieAllowed(movie));
        }

        [Test]
        public void draft_new_movie_is_rejected()
        {
            var movie = ActivatedMovie();
            Mocker.GetMock<IQualityProfileRepository>().Setup(s => s.Get(10)).Returns(new QualityProfile());
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureNewMovieAllowed(movie));
        }

        [Test]
        public void active_profile_still_rejects_legacy_conversion_and_delete()
        {
            var movie = ActivatedMovie();
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureAssignable(10));
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureLegacyMutationAllowed(new[] { movie }));
        }

        [Test]
        public void active_acquisition_requires_its_own_version_assignment()
        {
            var movie = ActivatedMovie();
            movie.Id = 7;
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.ForMovie(7)).Returns(new List<MovieVersion>
            {
                new MovieVersion { Id = 201, MovieId = 7, CompositeProfileVersionId = 101 }
            });

            Assert.DoesNotThrow(() => Subject.EnsureDownloadAllowed(movie, 201));
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureDownloadAllowed(movie, 202));
            Assert.Throws<NzbDroneClientException>(() => Subject.EnsureDownloadAllowed(movie, null));
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
