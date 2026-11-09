using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.QueueTests
{
    [TestFixture]
    public class QueueServiceFixture : CoreTest<QueueService>
    {
        private List<TrackedDownload> _trackedDownloads;

        [SetUp]
        public void SetUp()
        {
            var downloadClientInfo = Builder<DownloadClientItemClientInfo>.CreateNew().Build();

            var downloadItem = Builder<NzbDrone.Core.Download.DownloadClientItem>.CreateNew()
                                        .With(v => v.RemainingTime = TimeSpan.FromSeconds(10))
                                        .With(v => v.DownloadClientInfo = downloadClientInfo)
                                        .Build();

            var series = Builder<Movie>.CreateNew()
                                        .Build();

            var remoteEpisode = Builder<RemoteMovie>.CreateNew()
                                                   .With(r => r.Movie = series)
                                                   .With(r => r.ParsedMovieInfo = new ParsedMovieInfo())
                                                   .Build();

            _trackedDownloads = Builder<TrackedDownload>.CreateListOfSize(1)
                .All()
                .With(v => v.IsTrackable = true)
                .With(v => v.DownloadItem = downloadItem)
                .With(v => v.RemoteMovie = remoteEpisode)
                .Build()
                .ToList();
        }

        [Test]
        public void queue_items_should_have_id()
        {
            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            var queue = Subject.GetQueue();

            queue.Should().HaveCount(1);

            queue.All(v => v.Id > 0).Should().BeTrue();

            var distinct = queue.Select(v => v.Id).Distinct().ToArray();

            distinct.Should().HaveCount(1);
        }

        [Test]
        public void state_change_must_update_the_cached_row_and_notify_activity_without_polling_clients()
        {
            var tracked = _trackedDownloads.Single();
            tracked.State = TrackedDownloadState.ImportPending;
            var other = new TrackedDownload
            {
                IsTrackable = true,
                DownloadClient = tracked.DownloadClient + 1,
                DownloadItem = tracked.DownloadItem,
                RemoteMovie = tracked.RemoteMovie,
                State = TrackedDownloadState.ImportPending
            };
            Subject.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { tracked, other }));
            var changedId = Subject.GetQueue().First().Id;
            Mocker.GetMock<IEventAggregator>().Invocations.Clear();

            tracked.State = TrackedDownloadState.Importing;
            Subject.Handle(new TrackedDownloadStateChangedEvent(tracked));

            Assert.That(Subject.Find(changedId).TrackedDownloadState, Is.EqualTo(TrackedDownloadState.Importing));
            Assert.That(Subject.GetQueue().Single(q => q.Id != changedId).TrackedDownloadState, Is.EqualTo(TrackedDownloadState.ImportPending));
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<QueueUpdatedEvent>()), Times.Once());
        }

        [Test]
        public void late_import_state_change_must_not_reintroduce_a_removed_queue_row()
        {
            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));
            Subject.Remove(Subject.GetQueue().Single().Id);
            Subject.Handle(new TrackedDownloadStateChangedEvent(_trackedDownloads.Single()));
            Assert.That(Subject.GetQueue(), Is.Empty);
        }

        [TestCase(false, false, "verificationPending")]
        [TestCase(false, true, "verifying")]
        [TestCase(true, true, "verificationFailed")]
        public void queue_should_expose_published_verification_stage(bool failed, bool running, string stage)
        {
            var tracked = _trackedDownloads.Single();
            tracked.RemoteMovie.MovieVersionId = 7;
            var operation = new VersionFileImport
            {
                MovieId = tracked.RemoteMovie.Movie.Id,
                MovieVersionId = 7,
                State = VersionFileImportState.Installing,
                Snapshot = new VersionFileImportSnapshot
                {
                    DownloadId = tracked.DownloadItem.DownloadId,
                    PublishBeforeVerification = true,
                    RollbackPending = failed
                }
            };
            Mocker.GetMock<IVersionFileImportRepository>().Setup(i => i.Pending()).Returns(new List<VersionFileImport> { operation });
            using var lease = running ? new VersionImportOperationLease(operation.Id) : null;
            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));
            Assert.That(Subject.GetQueue().Single().ImportStage, Is.EqualTo(stage));
            operation.State = VersionFileImportState.Completed;
            operation.Snapshot.RollbackPending = false;
            Assert.That(Subject.GetQueue().Single().ImportStage, Is.Null);
        }
    }
}
