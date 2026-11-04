using System.Collections.Generic;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class MovieProfileConversionApiFixture : TestBase<MovieController>
    {
        [Test]
        public void legacy_movie_update_must_not_accept_an_explicit_ambiguity_override()
        {
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(1)).Returns(new Movie { Id = 1, QualityProfileId = 7 });
            Mocker.GetMock<IMovieProfileConversionRepository>().Setup(r => r.RequiresConversion(It.IsAny<List<Movie>>(), 8)).Returns(true);
            Mocker.GetMock<IMovieProfileConversionRepository>().Setup(r => r.UpdateProfiles(It.IsAny<List<Movie>>(), null, null)).Throws(new NzbDroneClientException(HttpStatusCode.Conflict, "Ambiguous"));

            Assert.Throws<NzbDroneClientException>(() => Subject.UpdateMovie(new MovieResource { Id = 1, QualityProfileId = 8, RetainChildProfileId = 4 }));

            Mocker.GetMock<IMovieProfileConversionRepository>().Verify(r => r.UpdateProfiles(It.IsAny<List<Movie>>(), null, null), Times.Once());
        }

        [Test]
        public void explicit_ui_route_should_forward_the_selected_child()
        {
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(1)).Returns(new Movie { Id = 1, QualityProfileId = 7 });
            Mocker.GetMock<IMovieProfileConversionRepository>().Setup(r => r.UpdateProfiles(It.IsAny<List<Movie>>(), 4, null)).Throws(new NzbDroneClientException(HttpStatusCode.Conflict, "Verified selection"));

            Assert.Throws<NzbDroneClientException>(() => Subject.UpdateProfileWithSelection(1, new MovieResource { Id = 1, QualityProfileId = 8, RetainChildProfileId = 4 }));

            Mocker.GetMock<IMovieProfileConversionRepository>().Verify(r => r.UpdateProfiles(It.IsAny<List<Movie>>(), 4, null), Times.Once());
        }
    }
}
