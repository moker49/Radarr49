using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadProcessingPipelineFixture : CoreTest<DownloadProcessingService>
    {
        [TestCase(false)]
        [TestCase(true)]
        public void completed_download_must_be_queued_without_blocking_the_command_and_removed_jobs_must_be_skipped(bool removed)
        {
            var download = new TrackedDownload
            {
                IsTrackable = true,
                State = TrackedDownloadState.ImportPending,
                DownloadItem = new DownloadClientItem { DownloadId = "download", Title = "Movie" }
            };
            var tracked = new List<TrackedDownload> { download };
            Mocker.GetMock<IConfigService>().Setup(c => c.EnableCompletedDownloadHandling).Returns(true);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(tracked);
            Action queued = null;
            Mocker.GetMock<IDownloadImportPipeline>().Setup(p => p.QueueCopy(It.IsAny<string>(), It.IsAny<Action>()))
                .Callback<string, Action>((_, action) => queued = action).Returns(true);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());
            Assert.That(queued, Is.Not.Null);
            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.Import(download), Times.Never());
            if (removed)
            {
                tracked.Clear();
            }

            queued();
            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.Import(download), removed ? Times.Never() : Times.Once());
        }
    }
}
