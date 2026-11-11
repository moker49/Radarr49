using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class RedownloadFailedDownloadServiceFixture : CoreTest<RedownloadFailedDownloadService>
    {
        [TestCase(null, 7, 7)]
        [TestCase(8, 7, 8)]
        [TestCase(null, null, null)]
        public void retry_should_preserve_failure_intent_and_known_version(int? eventVersion, int? trackedVersion, int? expectedVersion)
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.AutoRedownloadFailed).Returns(true);

            Subject.Handle(new DownloadFailedEvent
            {
                MovieId = 78,
                MovieVersionId = eventVersion,
                TrackedDownload = new TrackedDownload { MovieVersionId = trackedVersion }
            });

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.Is<MoviesSearchCommand>(c => c.MovieIds.Single() == 78 && c.MovieVersionId == expectedVersion && c.IsFailureRetry), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());
        }

        [Test]
        public void disabled_automatic_redownloading_must_still_prevent_retry()
        {
            Mocker.GetMock<IConfigService>().SetupGet(c => c.AutoRedownloadFailed).Returns(false);

            Subject.Handle(new DownloadFailedEvent { MovieId = 78 });

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<MoviesSearchCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
        }
    }
}
