using System.Collections.Generic;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests
{
    [TestFixture]
    public class CompositeAssignmentGuardFixture : CoreTest<MovieService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICompositeProfilePolicy>().Setup(s => s.EnsureAssignable(10))
                .Throws(new NzbDroneClientException(HttpStatusCode.Conflict, "Draft composite"));
        }

        [Test]
        public void single_add_should_be_rejected_before_persistence()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.AddMovie(new Movie { QualityProfileId = 10 }));

            Mocker.GetMock<IMovieRepository>().Verify(s => s.Insert(It.IsAny<Movie>()), Times.Never());
        }

        [Test]
        public void mixed_bulk_add_should_be_rejected_before_persistence()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.AddMovies(new List<Movie>
            {
                new Movie { QualityProfileId = 1 }, new Movie { QualityProfileId = 10 }
            }));

            Mocker.GetMock<IMovieRepository>().Verify(s => s.InsertMany(It.IsAny<IList<Movie>>()), Times.Never());
        }

        [Test]
        public void mixed_bulk_profile_change_should_be_rejected_before_tag_or_path_updates()
        {
            var movie = new Movie { QualityProfileId = 1, RootFolderPath = "C:/NewRoot" };

            var movies = new List<Movie>
            {
                movie, new Movie { QualityProfileId = 10 }
            };

            Assert.Throws<NzbDroneClientException>(() => Subject.UpdateMovie(movies, false));

            Assert.That(movie.Path, Is.Null);
            Mocker.GetMock<IMovieRepository>().Verify(s => s.UpdateMany(It.IsAny<IList<Movie>>()), Times.Never());
        }
    }
}
