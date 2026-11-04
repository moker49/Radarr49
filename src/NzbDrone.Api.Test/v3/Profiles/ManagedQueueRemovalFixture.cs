using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Queue;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Queue;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class ManagedQueueRemovalFixture : TestBase<QueueController>
    {
        private TrackedDownload _tracked;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            _tracked = new TrackedDownload { DownloadClient = 7, MovieVersionId = 201, DownloadReservationId = 42, DownloadItem = new DownloadClientItem { DownloadId = "job" } };
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(1)).Returns(new NzbDrone.Core.Queue.Queue { Id = 1, DownloadId = "job" });
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find("job")).Returns(_tracked);
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(new Mock<IDownloadClient>().Object);
        }

        [Test]
        public void single_removal_should_use_managed_client_removal_before_stopping_tracking()
        {
            var order = new List<string>();
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.RemoveFromClient(_tracked)).Callback(() => order.Add("remove"));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.StopTracking("job")).Callback(() => order.Add("untrack"));
            Subject.RemoveAction(1);
            Assert.That(order, Is.EqualTo(new[] { "remove", "untrack" }));
        }

        [Test]
        public void bulk_removal_should_use_the_same_managed_cancellation_path()
        {
            Subject.RemoveMany(new QueueBulkResource { Ids = new List<int> { 1 } });
            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.RemoveFromClient(_tracked), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.Is<List<string>>(ids => ids.Count == 1 && ids[0] == "job")), Times.Once());
        }

        [Test]
        public void failed_managed_removal_should_not_discard_tracking()
        {
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.RemoveFromClient(_tracked)).Throws(new InvalidOperationException("Removal failed"));
            Assert.Throws<InvalidOperationException>(() => Subject.RemoveAction(1));
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void ordinary_client_removal_should_keep_existing_behavior()
        {
            _tracked.MovieVersionId = null;
            _tracked.DownloadReservationId = null;
            Subject.RemoveAction(1);
            Mock.Get(Mocker.GetMock<IProvideDownloadClient>().Object.Get(7)).Verify(s => s.RemoveItem(_tracked.DownloadItem, true), Times.Once());
            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.RemoveFromClient(It.IsAny<TrackedDownload>()), Times.Never());
        }
    }
}
