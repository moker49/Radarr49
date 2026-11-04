using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedMovieVersionFixture : CoreTest<TrackedDownloadService>
    {
        private DownloadClientDefinition _client;
        private DownloadClientItem _item;
        private RemoteMovie _remote;
        private List<MovieHistory> _history;

        [SetUp]
        public void Setup()
        {
            _client = new DownloadClientDefinition { Id = 1 };
            _item = new DownloadClientItem { DownloadId = "download-1", Title = "Alien.1979.1080p.BluRay", DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Test" } };
            _remote = new RemoteMovie { Movie = new Movie { Id = 1, QualityProfileId = 10 }, ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Alien" }, Year = 1979 } };
            _history = new List<MovieHistory>
            {
                new MovieHistory { MovieId = 1, MovieVersionId = 201, EventType = MovieHistoryEventType.Grabbed, SourceTitle = _item.Title }
            };
            Mocker.GetMock<IHistoryService>().Setup(s => s.FindByDownloadId(_item.DownloadId)).Returns(_history);
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null)).Returns(_remote);
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), 1)).Returns(_remote);
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.ForVersion(It.Is<Movie>(m => m.Id == 1), 201)).Returns(new Movie { Id = 1, QualityProfileId = 10 });
        }

        [Test]
        public void imported_job_should_stay_imported_after_its_version_was_retired_by_conversion()
        {
            Mocker.GetMock<IDownloadHistoryService>().Setup(s => s.GetLatestDownloadHistoryItem(_item.DownloadId))
                .Returns(new DownloadHistory { MovieId = 1, MovieVersionId = 201, EventType = DownloadHistoryEventType.DownloadImported });
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.Resolve(It.IsAny<Movie>())).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { MovieId = 1, QualityProfile = new NzbDrone.Core.Profiles.Qualities.QualityProfile() }
            });

            var tracked = Subject.TrackDownload(_client, _item);

            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
            Mocker.GetMock<IMovieVersionService>().Verify(s => s.ForVersion(It.IsAny<Movie>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void restart_should_restore_version_id_from_persisted_grab()
        {
            var tracked = Subject.TrackDownload(_client, _item);

            Assert.That(tracked.MovieVersionId, Is.EqualTo(201));
            Assert.That(tracked.RemoteMovie.MovieVersionId, Is.EqualTo(201));
            Assert.That(tracked.State, Is.Not.EqualTo(TrackedDownloadState.ImportBlocked));
        }

        [Test]
        public void title_matching_another_movie_should_not_override_persisted_owner()
        {
            var incorrectlyParsed = new RemoteMovie { Movie = new Movie { Id = 999 }, ParsedMovieInfo = _remote.ParsedMovieInfo };
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), null)).Returns(incorrectlyParsed);

            var tracked = Subject.TrackDownload(_client, _item);

            Assert.That(tracked.RemoteMovie.Movie.Id, Is.EqualTo(1));
            Assert.That(tracked.RemoteMovie.MovieVersionId, Is.EqualTo(201));
        }

        [Test]
        public void missing_or_changed_assignment_should_block_import()
        {
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.ForVersion(It.IsAny<Movie>(), 201)).Throws(new InvalidOperationException("Assignment no longer exists"));

            var tracked = Subject.TrackDownload(_client, _item);

            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(tracked.Status, Is.EqualTo(TrackedDownloadStatus.Warning));
        }

        [Test]
        public void conflicting_history_sources_should_block_import()
        {
            Mocker.GetMock<IDownloadHistoryService>().Setup(s => s.GetLatestDownloadHistoryItem(_item.DownloadId))
                .Returns(new DownloadHistory { MovieId = 1, MovieVersionId = 202, EventType = DownloadHistoryEventType.DownloadGrabbed });

            Assert.That(Subject.TrackDownload(_client, _item).State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Mocker.GetMock<IMovieVersionService>().Verify(s => s.ForVersion(It.IsAny<Movie>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void composite_download_without_recorded_version_should_not_guess_destination()
        {
            _history.Clear();
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.Resolve(It.IsAny<Movie>())).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 201 }, new ResolvedMovieVersion { Id = 202 }
            });

            Assert.That(Subject.TrackDownload(_client, _item).State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
        }

        [Test]
        public void reservation_should_restore_version_when_both_histories_are_missing()
        {
            _history.Clear();
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.FindForDownload(_client.Id, _item.DownloadId))
                .Returns(new List<DownloadReservation>
                {
                    new DownloadReservation { Id = 42, MovieId = 1, MovieVersionId = 201, SourceTitle = _item.Title, DownloadId = _item.DownloadId, State = DownloadReservationState.Submitted }
                });

            var tracked = Subject.TrackDownload(_client, _item);

            Assert.That(tracked.DownloadReservationId, Is.EqualTo(42));
            Assert.That(tracked.MovieVersionId, Is.EqualTo(201));
            Assert.That(tracked.RemoteMovie.MovieVersionId, Is.EqualTo(201));
        }

        [Test]
        public void multiple_reservations_claiming_one_client_download_should_block_import()
        {
            _history.Clear();
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.FindForDownload(_client.Id, _item.DownloadId))
                .Returns(new List<DownloadReservation>
                {
                    new DownloadReservation { Id = 42, MovieId = 1, MovieVersionId = 201 },
                    new DownloadReservation { Id = 43, MovieId = 2, MovieVersionId = 202 }
                });

            Assert.That(Subject.TrackDownload(_client, _item).State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
        }

        [Test]
        public void reused_client_id_should_use_current_reservation_instead_of_old_attempt_history()
        {
            _history[0].Date = DateTime.UtcNow.AddDays(-1);
            var created = DateTime.UtcNow;
            Mocker.GetMock<IDownloadHistoryService>().Setup(s => s.GetLatestDownloadHistoryItem(_item.DownloadId))
                .Returns(new DownloadHistory { MovieId = 1, MovieVersionId = 201, Date = created.AddDays(-1), EventType = DownloadHistoryEventType.DownloadImported });
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.FindForDownload(_client.Id, _item.DownloadId))
                .Returns(new List<DownloadReservation>
                {
                    new DownloadReservation { Id = 43, MovieId = 1, MovieVersionId = 202, SourceTitle = _item.Title, DownloadId = _item.DownloadId, State = DownloadReservationState.Submitted, Created = created }
                });
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.ForVersion(It.IsAny<Movie>(), 202)).Returns(new Movie { Id = 1, QualityProfileId = 10 });

            var tracked = Subject.TrackDownload(_client, _item);

            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Downloading));
            Assert.That(tracked.MovieVersionId, Is.EqualTo(202));
        }

        [Test]
        public void blocked_cache_should_recover_when_submission_identity_becomes_available()
        {
            _history.Clear();
            var rows = new List<DownloadReservation>();
            Mocker.GetMock<IDownloadReservationRepository>().Setup(s => s.FindForDownload(_client.Id, _item.DownloadId)).Returns(rows);
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.Resolve(It.IsAny<Movie>())).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 201 }, new ResolvedMovieVersion { Id = 202 }
            });
            Assert.That(Subject.TrackDownload(_client, _item).State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            rows.Add(new DownloadReservation { Id = 42, MovieId = 1, MovieVersionId = 201, SourceTitle = _item.Title, DownloadId = _item.DownloadId, State = DownloadReservationState.Submitted });

            Assert.That(Subject.TrackDownload(_client, _item).State, Is.EqualTo(TrackedDownloadState.Downloading));
        }
    }
}
