using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class CompositeDownloadSchedulerFixture : CoreTest<CompositeDownloadScheduler>
    {
        private List<Queue.Queue> _queue;

        [SetUp]
        public void Setup()
        {
            _queue = new List<Queue.Queue>();
            Mocker.GetMock<IQueueService>().Setup(s => s.GetQueue()).Returns(_queue);
        }

        private static RemoteMovie Release(int versionId)
        {
            return new RemoteMovie
            {
                Movie = new Movie { Id = 1, QualityProfileId = 10 }, MovieVersionId = versionId,
                Release = new ReleaseInfo { Guid = versionId.ToString(), Title = versionId.ToString(), Size = versionId }
            };
        }

        [Test]
        public void active_other_version_should_not_consume_this_versions_slot()
        {
            _queue.Add(new Queue.Queue { RemoteMovie = Release(201), TrackedDownloadState = TrackedDownloadState.Downloading });

            Assert.That(Subject.CanGrab(Release(202), new List<DownloadDecision>()), Is.True);
        }

        [Test]
        public void same_batch_should_allow_one_download_for_each_version()
        {
            var grabbed = new List<DownloadDecision> { new DownloadDecision(Release(201)) };

            Assert.That(Subject.CanGrab(Release(202), grabbed), Is.True);
        }

        [Test]
        public void should_not_start_another_download_for_same_version()
        {
            _queue.Add(new Queue.Queue { RemoteMovie = Release(201), TrackedDownloadState = TrackedDownloadState.Downloading });

            Assert.That(Subject.CanGrab(Release(201), new List<DownloadDecision>()), Is.False);
        }

        [Test]
        public void same_torrent_from_another_indexer_should_not_fill_second_version()
        {
            var sdr = Release(201);
            var hdr = Release(202);
            sdr.Release = new TorrentInfo { InfoHash = "ABC", IndexerId = 1 };
            hdr.Release = new TorrentInfo { InfoHash = "abc", IndexerId = 2 };

            Assert.That(Subject.CanGrab(hdr, new List<DownloadDecision> { new DownloadDecision(sdr) }), Is.False);
        }

        [Test]
        public void unknown_queued_version_must_still_block_ambiguous_dispatch()
        {
            var unknown = Release(201);
            unknown.MovieVersionId = null;
            _queue.Add(new Queue.Queue { RemoteMovie = unknown, TrackedDownloadState = TrackedDownloadState.Downloading });
            Assert.That(Subject.CanGrab(Release(202), new List<DownloadDecision>()), Is.False);
        }

        [Test]
        public void completed_and_failed_downloads_should_not_consume_slots()
        {
            _queue.Add(new Queue.Queue { RemoteMovie = Release(201), TrackedDownloadState = TrackedDownloadState.Imported });
            _queue.Add(new Queue.Queue { RemoteMovie = Release(201), TrackedDownloadState = TrackedDownloadState.Failed });

            Assert.That(Subject.CanGrab(Release(202), new List<DownloadDecision>()), Is.True);
        }

        [Test]
        public void just_grabbed_release_already_visible_in_queue_should_be_counted_once()
        {
            var sdr = Release(201);
            _queue.Add(new Queue.Queue { RemoteMovie = sdr, TrackedDownloadState = TrackedDownloadState.Downloading });

            Assert.That(Subject.CanGrab(Release(202), new List<DownloadDecision> { new DownloadDecision(sdr) }), Is.True);
        }
    }
}
