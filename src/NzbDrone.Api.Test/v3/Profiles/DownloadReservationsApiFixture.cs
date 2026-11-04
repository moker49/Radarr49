using System.Collections.Generic;
using System.Linq;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class DownloadReservationsApiFixture : TestBase<DownloadReservationsController>
    {
        [SetUp]
        public void Setup()
        {
            var reservation = new DownloadReservation
            {
                Id = 42, MovieId = 1, MovieVersionId = 201, DownloadClientId = 7,
                SourceTitle = "Alien", State = DownloadReservationState.Uncertain,
                ReleaseKeys = DownloadReleaseIdentity.GetKeys(new ReleaseInfo { Title = "Alien", Size = 100 })
            };
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.Get(42)).Returns(reservation);
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.ForMovie(1)).Returns(new List<DownloadReservation> { reservation });
            Mocker.GetMock<IMovieRepository>().Setup(s => s.Get(1)).Returns(new Movie { Id = 1 });
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload>());
            Mocker.SetConstant<IDownloadReservationService>(Mocker.Resolve<DownloadReservationService>());
        }

        [Test]
        public void confirm_removed_route_requires_verification_note()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmRemoved(1, 42, new ReservationResolutionResource { DownloadId = "job" }));
        }

        [Test]
        public void confirm_removed_route_cannot_clear_another_movies_reservation()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmRemoved(2, 42, new ReservationResolutionResource { DownloadId = "job", Note = "Checked" }));
        }

        [Test]
        public void should_expose_unresolved_version_and_client_identity()
        {
            var resource = Subject.Get(1).Single();

            Assert.That(resource.MovieVersionId, Is.EqualTo(201));
            Assert.That(resource.DownloadClientId, Is.EqualTo(7));
            Assert.That(resource.State, Is.EqualTo(DownloadReservationState.Uncertain));
        }

        [Test]
        public void resolution_route_cannot_modify_another_movies_reservation()
        {
            var exception = Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmNotSubmitted(2, 42, new ReservationResolutionResource { Note = "Checked client" }));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Mocker.GetMock<IDownloadReservationRepository>().Verify(s => s.ConfirmNotSubmitted(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void resolution_route_requires_explicit_verification()
        {
            var exception = Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmNotSubmitted(1, 42, new ReservationResolutionResource()));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public void bind_route_cannot_claim_an_unverified_download_id()
        {
            var exception = Assert.Throws<NzbDroneClientException>(() => Subject.BindDownload(1, 42, new ReservationResolutionResource { DownloadId = "unrelated-job", Note = "Checked client" }));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Mocker.GetMock<IDownloadReservationRepository>().Verify(s => s.BindDownload(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
