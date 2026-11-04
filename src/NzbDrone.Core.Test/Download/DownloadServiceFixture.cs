using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadServiceFixture : CoreTest<DownloadService>
    {
        private RemoteMovie _parseResult;
        private List<IDownloadClient> _downloadClients;
        [SetUp]
        public void Setup()
        {
            _downloadClients = new List<IDownloadClient>();

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(v => v.GetDownloadClients(It.IsAny<bool>()))
                .Returns(_downloadClients);

            Mocker.GetMock<IProvideDownloadClient>()
                .Setup(v => v.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()))
                .Returns<DownloadProtocol, int, bool, HashSet<int>>((v, i, f, t) => _downloadClients.FirstOrDefault(d => d.Protocol == v));

            var releaseInfo = Builder<ReleaseInfo>.CreateNew()
                .With(v => v.DownloadProtocol = DownloadProtocol.Usenet)
                .With(v => v.DownloadUrl = "http://test.site/download1.ext")
                .Build();

            _parseResult = Builder<RemoteMovie>.CreateNew()
                   .With(c => c.MovieVersionId = null)
                   .With(c => c.Movie = Builder<Movie>.CreateNew().Build())
                   .With(c => c.Release = releaseInfo)
                   .Build();
        }

        private Mock<IDownloadClient> WithUsenetClient()
        {
            var mock = new Mock<IDownloadClient>(MockBehavior.Default);
            mock.SetupGet(s => s.Definition).Returns(Builder<IndexerDefinition>.CreateNew().Build());

            _downloadClients.Add(mock.Object);

            mock.SetupGet(v => v.Protocol).Returns(DownloadProtocol.Usenet);

            return mock;
        }

        [Test]
        public void draft_composite_should_be_rejected_before_selecting_download_client()
        {
            Mocker.GetMock<ICompositeProfilePolicy>().Setup(s => s.EnsureDownloadAllowed(_parseResult.Movie, _parseResult.MovieVersionId))
                .Throws(new NzbDroneClientException(HttpStatusCode.Conflict, "Draft composite"));

            Assert.ThrowsAsync<NzbDroneClientException>(() => Subject.DownloadReport(_parseResult, null));
            Mocker.GetMock<IProvideDownloadClient>().Verify(s => s.GetDownloadClient(It.IsAny<DownloadProtocol>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<HashSet<int>>()), Times.Never());
        }

        private Mock<IDownloadClient> WithTorrentClient()
        {
            var mock = new Mock<IDownloadClient>(MockBehavior.Default);
            mock.SetupGet(s => s.Definition).Returns(Builder<IndexerDefinition>.CreateNew().Build());

            _downloadClients.Add(mock.Object);

            mock.SetupGet(v => v.Protocol).Returns(DownloadProtocol.Torrent);

            return mock;
        }

        [Test]
        public async Task version_dispatch_should_persist_intent_and_client_identity_in_order()
        {
            _parseResult.MovieVersionId = 201;
            _parseResult.Movie.Path = "C:/CompositeTest/Movie";
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.ForProfile(_parseResult.Movie.QualityProfileId))
                .Returns(new List<CompositeProfileVersion> { new CompositeProfileVersion { Id = 101 } });
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.ForMovie(_parseResult.Movie.Id))
                .Returns(new List<MovieVersion> { new MovieVersion { Id = 201, CompositeProfileVersionId = 101 } });
            Mocker.GetMock<IQualityProfileRepository>().Setup(s => s.Get(_parseResult.Movie.QualityProfileId))
                .Returns(new QualityProfile { CompositeTestRoot = "C:/CompositeTest" });
            Mocker.SetConstant<ICompositeProfilePolicy>(Mocker.Resolve<CompositeProfilePolicy>());
            var order = new List<string>();
            var client = WithUsenetClient();
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.Reserve(_parseResult, It.IsAny<int>()))
                .Callback(() => order.Add("intent"))
                .Returns(new DownloadReservation { Id = 42 });
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.BeginDispatch(42)).Callback(() => order.Add("dispatch-marker"));
            client.Setup(s => s.Download(_parseResult, It.IsAny<IIndexer>())).Callback(() => order.Add("client-request")).ReturnsAsync("job-1");
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.Submitted(42, "job-1")).Callback(() => order.Add("bound-id"));
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<MovieGrabbedEvent>())).Callback<MovieGrabbedEvent>(_ => order.Add("history-event"));

            await Subject.DownloadReport(_parseResult, null);

            Assert.That(order, Is.EqualTo(new[] { "intent", "dispatch-marker", "client-request", "bound-id", "history-event" }));
            VerifyEventPublished<MovieGrabbedEvent>();
        }

        [Test]
        public void conflicting_reservation_should_prevent_client_submission()
        {
            _parseResult.MovieVersionId = 201;
            var client = WithUsenetClient();
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.Reserve(_parseResult, It.IsAny<int>())).Throws(new DownloadReservationConflictException());

            Assert.ThrowsAsync<DownloadReservationConflictException>(() => Subject.DownloadReport(_parseResult, null));

            client.Verify(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [Test]
        public void client_release_rejection_should_release_managed_slot_without_grab_event()
        {
            _parseResult.MovieVersionId = 201;
            var client = WithUsenetClient();
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.Reserve(_parseResult, It.IsAny<int>())).Returns(new DownloadReservation { Id = 42 });
            client.Setup(s => s.Download(_parseResult, It.IsAny<IIndexer>())).ThrowsAsync(new DownloadClientRejectedReleaseException(_parseResult.Release, "Possible duplicate"));

            Assert.ThrowsAsync<DownloadClientRejectedReleaseException>(() => Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.DispatchFailed(42, true, "Possible duplicate"), Times.Once());
            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.Submitted(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        [Test]
        public void ambiguous_client_failure_should_not_be_classified_as_definitive_rejection()
        {
            _parseResult.MovieVersionId = 201;
            var client = WithUsenetClient();
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.Reserve(_parseResult, It.IsAny<int>())).Returns(new DownloadReservation { Id = 42 });
            client.Setup(s => s.Download(_parseResult, It.IsAny<IIndexer>())).ThrowsAsync(new TimeoutException("Response lost"));

            Assert.ThrowsAsync<TimeoutException>(() => Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.DispatchFailed(42, false, "Response lost"), Times.Once());
            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.Submitted(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        [Test]
        public void definitive_release_rejection_should_be_recorded_for_slot_release()
        {
            _parseResult.MovieVersionId = 201;
            var client = WithUsenetClient();
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.Reserve(_parseResult, It.IsAny<int>())).Returns(new DownloadReservation { Id = 42 });
            client.Setup(s => s.Download(_parseResult, It.IsAny<IIndexer>())).ThrowsAsync(new ReleaseUnavailableException(_parseResult.Release, "Gone"));

            Assert.ThrowsAsync<ReleaseUnavailableException>(() => Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.DispatchFailed(42, true, It.IsAny<string>()), Times.Once());
        }

        [Test]
        public void preparation_failure_must_not_reach_dispatch_marker_or_client()
        {
            _parseResult.MovieVersionId = 201;
            var client = WithUsenetClient();
            Mocker.GetMock<IDownloadReservationService>().Setup(s => s.Reserve(_parseResult, It.IsAny<int>())).Returns(new DownloadReservation { Id = 42 });
            Mocker.GetMock<ISeedConfigProvider>().Setup(s => s.GetSeedConfiguration(_parseResult)).Throws(new InvalidOperationException("Preparation failed"));

            Assert.ThrowsAsync<InvalidOperationException>(() => Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.BeginDispatch(It.IsAny<int>()), Times.Never());
            Mocker.GetMock<IDownloadReservationService>().Verify(s => s.DispatchFailed(42, false, "Preparation failed"), Times.Once());
            client.Verify(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }

        [Test]
        public async Task Download_report_should_publish_on_grab_event()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()));

            await Subject.DownloadReport(_parseResult, null);

            VerifyEventPublished<MovieGrabbedEvent>();
        }

        [Test]
        public async Task Download_report_should_grab_using_client()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()));

            await Subject.DownloadReport(_parseResult, null);

            mock.Verify(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public void Download_report_should_not_publish_on_failed_grab_event()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Throws(new WebException());

            Assert.ThrowsAsync<WebException>(async () => await Subject.DownloadReport(_parseResult, null));

            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_indexer_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new WebException());
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Once());
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_http429_with_long_time()
        {
            var request = new HttpRequest("http://my.indexer.com");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)429);
            response.Headers["Retry-After"] = "300";

            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new TooManyRequestsException(request, response));
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), TimeSpan.FromMinutes(5.0)), Times.Once());
        }

        [Test]
        public void Download_report_should_trigger_indexer_backoff_on_http429_based_on_date()
        {
            var request = new HttpRequest("http://my.indexer.com");
            var response = new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)429);
            response.Headers["Retry-After"] = DateTime.UtcNow.AddSeconds(300).ToString("r");

            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseDownloadException(v.Release, "Error", new TooManyRequestsException(request, response));
                });

            Assert.ThrowsAsync<ReleaseDownloadException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(),
                    It.IsInRange<TimeSpan>(TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5.1), Moq.Range.Inclusive)),
                    Times.Once());
        }

        [Test]
        public void Download_report_should_not_trigger_indexer_backoff_on_downloadclient_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Throws(new DownloadClientException("Some Error"));

            Assert.ThrowsAsync<DownloadClientException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never());
        }

        [Test]
        public void Download_report_should_not_trigger_indexer_backoff_on_indexer_404_error()
        {
            var mock = WithUsenetClient();
            mock.Setup(s => s.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((v, indexer) =>
                {
                    throw new ReleaseUnavailableException(v.Release, "Error", new WebException());
                });

            Assert.ThrowsAsync<ReleaseUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IIndexerStatusService>()
                .Verify(v => v.RecordFailure(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never());
        }

        [Test]
        public void should_not_attempt_download_if_client_isnt_configured()
        {
            Assert.ThrowsAsync<DownloadClientUnavailableException>(async () => await Subject.DownloadReport(_parseResult, null));

            Mocker.GetMock<IDownloadClient>().Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
            VerifyEventNotPublished<MovieGrabbedEvent>();
        }

        [Test]
        public async Task should_attempt_download_even_if_client_is_disabled()
        {
            var mockUsenet = WithUsenetClient();

            Mocker.GetMock<IDownloadClientStatusService>()
                  .Setup(v => v.GetBlockedProviders())
                  .Returns(new List<DownloadClientStatus>
                  {
                      new DownloadClientStatus
                      {
                          ProviderId = _downloadClients.First().Definition.Id,
                          DisabledTill = DateTime.UtcNow.AddHours(3)
                      }
                  });

            await Subject.DownloadReport(_parseResult, null);

            Mocker.GetMock<IDownloadClientStatusService>().Verify(c => c.GetBlockedProviders(), Times.Never());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
            VerifyEventPublished<MovieGrabbedEvent>();
        }

        [Test]
        public async Task should_send_download_to_correct_usenet_client()
        {
            var mockTorrent = WithTorrentClient();
            var mockUsenet = WithUsenetClient();

            await Subject.DownloadReport(_parseResult, null);

            mockTorrent.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
        }

        [Test]
        public async Task should_send_download_to_correct_torrent_client()
        {
            var mockTorrent = WithTorrentClient();
            var mockUsenet = WithUsenetClient();

            _parseResult.Release.DownloadProtocol = DownloadProtocol.Torrent;

            await Subject.DownloadReport(_parseResult, null);

            mockTorrent.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Once());
            mockUsenet.Verify(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()), Times.Never());
        }
    }
}
