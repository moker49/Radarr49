using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Indexers;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class VersionReleaseCacheFixture : TestBase<ReleaseController>
    {
        [SetUp]
        public void Setup()
        {
            var profile = new QualityProfile
            {
                Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.WEBDL1080p, Allowed = true } }
            };
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.GetDefaultProfile(It.IsAny<string>(), It.IsAny<Quality>(), It.IsAny<Quality[]>())).Returns(profile);
            var decisions = new List<DownloadDecision> { Decision(201), Decision(202) };
            Mocker.GetMock<ISearchForReleases>().Setup(s => s.MovieSearch(1, true, true)).ReturnsAsync(decisions);
            Mocker.GetMock<IPrioritizeDownloadDecision>().Setup(s => s.PrioritizeDecisionsForMovies(It.IsAny<List<DownloadDecision>>())).Returns((List<DownloadDecision> d) => d);
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByMovieId(1)).Returns(new List<MovieHistory>());
            Mocker.GetMock<IDownloadService>().Setup(s => s.DownloadReport(It.IsAny<RemoteMovie>(), It.IsAny<int?>())).Returns(Task.CompletedTask);
        }

        private static DownloadDecision Decision(int versionId)
        {
            return new DownloadDecision(new RemoteMovie
            {
                Movie = new Movie { Id = 1 }, MovieVersionId = versionId,
                Release = new ReleaseInfo { Guid = "same-release", IndexerId = 1, Title = "Alien.1979" },
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.WEBDL1080p) }
            });
        }

        [Test]
        public async Task same_release_should_keep_both_version_cache_entries()
        {
            var releases = await Subject.GetReleases(1);

            await Subject.DownloadRelease(releases[0]);
            await Subject.DownloadRelease(releases[1]);

            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.Is<RemoteMovie>(r => r.MovieVersionId == 201), It.IsAny<int?>()), Times.Once());
            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.Is<RemoteMovie>(r => r.MovieVersionId == 202), It.IsAny<int?>()), Times.Once());
        }

        [Test]
        public async Task legacy_grab_without_version_must_not_choose_either_version_cache_entry()
        {
            await Subject.GetReleases(1);

            var exception = Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(new ReleaseResource { Guid = "same-release", IndexerId = 1 }));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Mocker.GetMock<IDownloadService>().Verify(s => s.DownloadReport(It.IsAny<RemoteMovie>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public async Task legacy_override_must_not_strip_version_assignment()
        {
            var releases = await Subject.GetReleases(1);
            releases[0].ShouldOverride = true;

            var exception = Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadRelease(releases[0]));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        }
    }
}
