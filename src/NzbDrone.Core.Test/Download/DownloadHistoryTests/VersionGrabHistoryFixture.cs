using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.DownloadHistoryTests
{
    [TestFixture]
    public class VersionGrabHistoryFixture : CoreTest
    {
        [TestCase(null)]
        [TestCase(201)]
        public void both_grab_histories_should_preserve_intended_version(int? versionId)
        {
            var remote = new RemoteMovie
            {
                Movie = new Movie { Id = 1 },
                MovieVersionId = versionId,
                Release = new ReleaseInfo { Title = "Alien.1979", Indexer = "Test" },
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(), ReleaseGroup = "Test" }
            };
            var grabbed = new MovieGrabbedEvent(remote) { DownloadId = "download-1" };

            Mocker.Resolve<HistoryService>().Handle(grabbed);
            Mocker.Resolve<DownloadHistoryService>().Handle(grabbed);

            Mocker.GetMock<IHistoryRepository>().Verify(s => s.Insert(It.Is<MovieHistory>(h => h.MovieVersionId == versionId)), Times.Once());
            Mocker.GetMock<IDownloadHistoryRepository>().Verify(s => s.Insert(It.Is<DownloadHistory>(h => h.MovieVersionId == versionId)), Times.Once());
        }
    }
}
