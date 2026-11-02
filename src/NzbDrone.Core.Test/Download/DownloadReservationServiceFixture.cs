using System.Collections.Generic;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadReservationServiceFixture : CoreTest<DownloadReservationService>
    {
        private DownloadReservation _row;
        private List<TrackedDownload> _tracked;

        [SetUp]
        public void Setup()
        {
            _row = new DownloadReservation
            {
                Id = 42, MovieId = 1, MovieVersionId = 201, DownloadClientId = 7, SourceTitle = "Alien",
                ReleaseKeys = DownloadReleaseIdentity.GetKeys(new ReleaseInfo { Title = "Alien", Size = 100 }),
                State = DownloadReservationState.Uncertain
            };
            _tracked = new List<TrackedDownload>
            {
                new TrackedDownload
                {
                    DownloadClient = 7,
                    DownloadItem = new DownloadClientItem { DownloadId = "job-1", Title = "Alien", TotalSize = 100, DownloadClientInfo = new DownloadClientItemClientInfo { Protocol = DownloadProtocol.Usenet } }
                }
            };
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.Get(42)).Returns(_row);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(_tracked);
        }

        [Test]
        public void explicit_resolution_must_match_route_movie()
        {
            var exception = Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmNotSubmitted(2, 42, "Checked client"));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Mocker.GetMock<IDownloadReservationRepository>().Verify(s => s.ConfirmNotSubmitted(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void explicit_resolution_requires_verification_note()
        {
            var exception = Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmNotSubmitted(1, 42, ""));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public void binding_verified_client_download_should_refresh_tracking()
        {
            Subject.BindDownload(1, 42, "job-1", "Verified matching client job");

            Mocker.GetMock<IDownloadReservationRepository>().Verify(s => s.BindDownload(42, "job-1", "Verified matching client job", true), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking("job-1"), Times.Once());
        }

        [Test]
        public void binding_must_not_choose_wrong_client()
        {
            _tracked[0].DownloadClient = 8;

            Assert.Throws<NzbDroneClientException>(() => Subject.BindDownload(1, 42, "job-1", "Checked"));
            Mocker.GetMock<IDownloadReservationRepository>().Verify(s => s.BindDownload(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void binding_must_not_choose_an_unrelated_release()
        {
            _tracked[0].DownloadItem.Title = "Different Movie";

            Assert.Throws<NzbDroneClientException>(() => Subject.BindDownload(1, 42, "job-1", "Checked"));
        }

        [Test]
        public void binding_must_not_steal_another_versions_download()
        {
            _tracked[0].MovieVersionId = 202;

            Assert.Throws<NzbDroneClientException>(() => Subject.BindDownload(1, 42, "job-1", "Checked"));
        }
    }
}
