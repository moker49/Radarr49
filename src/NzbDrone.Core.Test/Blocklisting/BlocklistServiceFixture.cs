using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Blocklisting
{
    [TestFixture]
    public class BlocklistServiceFixture : CoreTest<BlocklistService>
    {
        private DownloadFailedEvent _event;

        [SetUp]
        public void Setup()
        {
            _event = new DownloadFailedEvent
            {
                MovieId = 69,
                Quality = new QualityModel(),
                SourceTitle = "series.title.s01e01",
                DownloadClient = "SabnzbdClient",
                DownloadId = "Sabnzbd_nzo_2dfh73k"
            };

            _event.Data.Add("publishedDate", DateTime.UtcNow.ToString("s") + "Z");
            _event.Data.Add("size", "1000");
            _event.Data.Add("indexer", "nzbs.org");
            _event.Data.Add("protocol", "1");
            _event.Data.Add("message", "Marked as failed");
        }

        [Test]
        public void version_specific_nzb_failure_must_not_block_another_version()
        {
            var date = DateTime.UtcNow;
            Mocker.GetMock<IBlocklistRepository>().Setup(b => b.BlocklistedByTitle(1, "release")).Returns(new List<Blocklist>
            {
                new Blocklist { MovieVersionId = 7, SourceTitle = "release", Protocol = DownloadProtocol.Usenet, Size = 1000, PublishedDate = date }
            });
            var release = new ReleaseInfo { Title = "release", DownloadProtocol = DownloadProtocol.Usenet, Size = 1000, PublishDate = date };
            Assert.That(Subject.Blocklisted(1, release, 7), Is.True);
            Assert.That(Subject.Blocklisted(1, release, 8), Is.False);
            Assert.That(Subject.Blocklisted(1, release), Is.False);
        }

        [TestCase(null, true, true)]
        [TestCase(7, true, false)]
        [TestCase(8, false, true)]
        public void version_specific_hash_blocklist_must_not_reject_other_versions(int? blockedVersion, bool sdr, bool hdr)
        {
            Mocker.GetMock<IBlocklistRepository>().Setup(b => b.BlocklistedByTorrentInfoHash(1, "hash")).Returns(new List<Blocklist>
            {
                new Blocklist { MovieId = 1, MovieVersionId = blockedVersion, TorrentInfoHash = "hash" }
            });
            Assert.That(Subject.BlocklistedTorrentHash(1, "hash", 7), Is.EqualTo(sdr));
            Assert.That(Subject.BlocklistedTorrentHash(1, "hash", 8), Is.EqualTo(hdr));
        }

        [Test]
        public void should_add_to_repository()
        {
            Subject.Handle(_event);

            Mocker.GetMock<IBlocklistRepository>()
                .Verify(v => v.Insert(It.Is<Blocklist>(b => b.MovieId == _event.MovieId)), Times.Once());
        }

        [Test]
        public void should_add_to_repository_missing_size_and_protocol()
        {
            Subject.Handle(_event);

            _event.Data.Remove("size");
            _event.Data.Remove("protocol");

            Mocker.GetMock<IBlocklistRepository>()
                .Verify(v => v.Insert(It.Is<Blocklist>(b => b.MovieId == _event.MovieId)), Times.Once());
        }
    }
}
