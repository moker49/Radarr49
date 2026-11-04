using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadReservationRemovalFixture : CoreTest<DownloadReservationService>
    {
        private DownloadReservation _row;
        private TrackedDownload _tracked;
        private Mock<IDownloadClient> _client;

        [SetUp]
        public void Setup()
        {
            _row = new DownloadReservation { Id = 42, MovieId = 1, MovieVersionId = 201, DownloadClientId = 7, DownloadId = "job-1", State = DownloadReservationState.Submitted };
            _tracked = new TrackedDownload { DownloadClient = 7, MovieVersionId = 201, DownloadReservationId = 42, DownloadItem = new DownloadClientItem { DownloadId = "job-1" } };
            _client = new Mock<IDownloadClient>();
            _client.Setup(s => s.GetItems()).Returns(new List<DownloadClientItem>());
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(_client.Object);
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.Get(42)).Returns(_row);
        }

        private void VerifyRetained()
        {
            Mocker.GetMock<IDownloadReservationRepository>().Verify(s => s.MarkRemoved(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void client_removal_must_succeed_before_releasing_the_bound_slot()
        {
            var order = new List<string>();
            _client.Setup(s => s.RemoveItem(_tracked.DownloadItem, true)).Callback(() => order.Add("client"));
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.MarkRemoved(42, 7, "job-1", It.IsAny<string>())).Callback(() => order.Add("reservation"));

            Subject.RemoveFromClient(_tracked);

            Assert.That(order, Is.EqualTo(new[] { "client", "reservation" }));
        }

        [Test]
        public void client_failure_must_retain_reservation()
        {
            _client.Setup(s => s.RemoveItem(_tracked.DownloadItem, true)).Throws(new IOException("Client unavailable"));
            Assert.Throws<IOException>(() => Subject.RemoveFromClient(_tracked));
            VerifyRetained();
        }

        [TestCase(8, 201, "job-1")]
        [TestCase(7, 202, "job-1")]
        [TestCase(7, 201, "other-job")]
        public void stale_identity_must_not_remove_a_client_job(int clientId, int versionId, string downloadId)
        {
            _tracked.DownloadClient = clientId;
            _tracked.MovieVersionId = versionId;
            _tracked.DownloadItem.DownloadId = downloadId;
            Assert.Throws<NzbDroneClientException>(() => Subject.RemoveFromClient(_tracked));
            _client.Verify(s => s.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
            VerifyRetained();
        }

        [Test]
        public void active_import_must_block_removal_before_touching_client()
        {
            Mocker.GetMock<IVersionFileDeletionRepository>().Setup(s => s.CheckReady(1, 201)).Throws(new InvalidOperationException("Import pending"));
            Assert.Throws<InvalidOperationException>(() => Subject.RemoveFromClient(_tracked));
            _client.Verify(s => s.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
            VerifyRetained();
        }

        [Test]
        public void removing_completed_client_history_must_preserve_imported_file_ownership()
        {
            _row.State = DownloadReservationState.Imported;
            Subject.RemoveFromClient(_tracked);
            _client.Verify(s => s.RemoveItem(_tracked.DownloadItem, true), Times.Once());
            VerifyRetained();
        }

        [Test]
        public void explicit_verified_absence_can_release_an_orphaned_bound_reservation()
        {
            Subject.ConfirmRemoved(1, 42, "job-1", "Verified user cancellation");
            Mocker.GetMock<IDownloadReservationRepository>().Verify(s => s.MarkRemoved(42, 7, "job-1", "Verified user cancellation"), Times.Once());
        }

        [Test]
        public void job_still_present_in_client_history_must_not_be_released()
        {
            _client.Setup(s => s.GetItems()).Returns(new[] { new DownloadClientItem { DownloadId = "JOB-1", Status = DownloadItemStatus.Completed } });
            Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmRemoved(1, 42, "job-1", "Checked"));
            VerifyRetained();
        }

        [Test]
        public void client_query_failure_must_not_be_interpreted_as_absence()
        {
            _client.Setup(s => s.GetItems()).Throws(new IOException("Offline"));
            Assert.Throws<IOException>(() => Subject.ConfirmRemoved(1, 42, "job-1", "Checked"));
            VerifyRetained();
        }

        [TestCase(2, "job-1", "Checked")]
        [TestCase(1, "wrong-job", "Checked")]
        [TestCase(1, "job-1", "")]
        public void recovery_requires_route_ownership_exact_bound_id_and_note(int movieId, string downloadId, string note)
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.ConfirmRemoved(movieId, 42, downloadId, note));
            VerifyRetained();
        }
    }
}
