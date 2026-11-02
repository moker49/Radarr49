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
    }
}
