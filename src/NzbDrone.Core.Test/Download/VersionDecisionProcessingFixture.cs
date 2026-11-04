using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class VersionDecisionProcessingFixture : CoreTest<ProcessDownloadDecisions>
    {
        private QualityProfile _root;

        [SetUp]
        public void Setup()
        {
            _root = new QualityProfile { Id = 10, MaximumConcurrentDownloads = 1 };
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Get(10)).Returns(_root);
            Mocker.GetMock<IQueueService>().Setup(s => s.GetQueue()).Returns(new List<Queue.Queue>());
            Mocker.GetMock<IDownloadService>().Setup(s => s.DownloadReport(It.IsAny<RemoteMovie>(), It.IsAny<int?>())).Returns(Task.CompletedTask);
            Mocker.SetConstant<ICompositeDownloadScheduler>(Mocker.Resolve<CompositeDownloadScheduler>());
            Mocker.SetConstant<IPrioritizeDownloadDecision>(Mocker.Resolve<DownloadDecisionPriorizationService>());
        }

        private static RemoteMovie Release(int versionId, int priority, bool hasFile = false)
        {
            return new RemoteMovie
            {
                Movie = new Movie { Id = 1, QualityProfileId = 10, MovieFileId = hasFile ? 50 : 0, QualityProfile = new QualityProfile() },
                MovieVersionId = versionId,
                MovieVersionPriority = priority,
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.WEBDL1080p) },
                Release = new ReleaseInfo { Guid = versionId.ToString(), Title = versionId.ToString(), Size = versionId, PublishDate = DateTime.UtcNow, DownloadProtocol = DownloadProtocol.Usenet }
            };
        }

        [Test]
        public async Task should_dispatch_sdr_first_and_hdr_in_the_same_search()
        {
            var sdr = Release(201, 0);
            var hdr = Release(202, 1);

            var result = await Subject.ProcessDecisions(new List<DownloadDecision> { new DownloadDecision(hdr), new DownloadDecision(sdr) });

            Assert.That(result.Grabbed.Select(d => d.RemoteMovie.MovieVersionId), Is.EqualTo(new int?[] { 201, 202 }));
            Assert.That(result.Pending, Is.Empty);
        }

        [Test]
        public async Task unavailable_sdr_should_not_block_hdr()
        {
            var rejectedSdr = new DownloadDecision(Release(201, 0), new DownloadRejection(DownloadRejectionReason.QualityNotWanted, "Unavailable"));

            var result = await Subject.ProcessDecisions(new List<DownloadDecision> { rejectedSdr, new DownloadDecision(Release(202, 1)) });

            Assert.That(result.Grabbed.Single().RemoteMovie.MovieVersionId, Is.EqualTo(202));
        }

        [Test]
        public async Task delayed_sdr_should_allow_eligible_hdr()
        {
            var delayedSdr = new DownloadDecision(Release(201, 0), new DownloadRejection(DownloadRejectionReason.MinimumAgeDelay, "Waiting", RejectionType.Temporary));

            var result = await Subject.ProcessDecisions(new List<DownloadDecision> { delayedSdr, new DownloadDecision(Release(202, 1)) });

            Assert.That(result.Grabbed.Single().RemoteMovie.MovieVersionId, Is.EqualTo(202));
        }

        [Test]
        public async Task missing_hdr_should_be_acquired_before_upgrading_an_existing_sdr_file()
        {
            var result = await Subject.ProcessDecisions(new List<DownloadDecision>
            {
                new DownloadDecision(Release(201, 0, true)), new DownloadDecision(Release(202, 1))
            });

            Assert.That(result.Grabbed.Select(d => d.RemoteMovie.MovieVersionId), Is.EqualTo(new int?[] { 202, 201 }));
        }

        [Test]
        public async Task configured_limit_of_two_should_allow_both_versions()
        {
            _root.MaximumConcurrentDownloads = 2;

            var result = await Subject.ProcessDecisions(new List<DownloadDecision>
            {
                new DownloadDecision(Release(201, 0)), new DownloadDecision(Release(202, 1))
            });

            Assert.That(result.Grabbed.Select(d => d.RemoteMovie.MovieVersionId), Is.EqualTo(new int?[] { 201, 202 }));
        }

        [Test]
        public async Task race_lost_at_durable_reservation_should_leave_release_pending()
        {
            Mocker.GetMock<IDownloadService>().Setup(s => s.DownloadReport(It.IsAny<RemoteMovie>(), It.IsAny<int?>()))
                .ThrowsAsync(new DownloadReservationConflictException());

            var result = await Subject.ProcessDecisions(new List<DownloadDecision> { new DownloadDecision(Release(201, 0)) });

            Assert.That(result.Grabbed, Is.Empty);
            Assert.That(result.Pending.Count, Is.EqualTo(1));
            Mocker.GetMock<IPendingReleaseService>().Verify(s => s.AddMany(It.Is<List<Tuple<DownloadDecision, PendingReleaseReason>>>(items => items.Single().Item2 == PendingReleaseReason.VersionDownloadLimit)), Times.Once());
        }
    }
}
