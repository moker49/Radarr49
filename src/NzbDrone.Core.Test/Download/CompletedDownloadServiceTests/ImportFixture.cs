using System;
using System.Collections.Generic;
using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class ImportFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                    .With(h => h.Status = DownloadItemStatus.Completed)
                                                    .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                    .With(h => h.Title = "Drone.1998")
                                                    .Build();

            var remoteMovie = BuildRemoteMovie();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                    .With(c => c.MovieVersionId = null)
                    .With(c => c.DownloadReservationId = null)
                    .With(c => c.State = TrackedDownloadState.Downloading)
                    .With(c => c.DownloadItem = completed)
                    .With(c => c.RemoteMovie = remoteMovie)
                    .Build();

            Mocker.GetMock<IDownloadClient>()
              .SetupGet(c => c.Definition)
              .Returns(new DownloadClientDefinition { Id = 1, Name = "testClient" });

            Mocker.GetMock<IProvideDownloadClient>()
                  .Setup(c => c.Get(It.IsAny<int>()))
                  .Returns(Mocker.GetMock<IDownloadClient>().Object);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.MostRecentForDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new MovieHistory());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie("Drone.1998"))
                  .Returns(remoteMovie.Movie);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>());

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);
        }

        private RemoteMovie BuildRemoteMovie()
        {
            return new RemoteMovie
            {
                Movie = new Movie()
            };
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
               .Setup(s => s.MostRecentForDownloadId(It.Is<string>(i => i == "1234")))
               .Returns(new MovieHistory() { SourceTitle = "Droned 1998" });

            Mocker.GetMock<IParsingService>()
               .Setup(s => s.GetMovie(It.IsAny<string>()))
               .Returns((Movie)null);

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.GetMovie("Droned 1998"))
                .Returns(BuildRemoteMovie().Movie);
        }

        private void GivenSeriesMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteMovie.Movie);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void published_import_must_stay_importing_until_separate_verification_finishes(bool fails)
        {
            var pending = new PendingVersionImport { MovieId = _trackedDownload.RemoteMovie.Movie.Id, OperationId = 42 };
            _trackedDownload.MovieVersionId = 7;
            var result = new ImportResult(new ImportDecision(new LocalMovie { Movie = _trackedDownload.RemoteMovie.Movie, MovieVersionId = 7 })) { PendingImport = pending };
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                .Returns(new List<ImportResult> { result });
            Action verify = null;
            Mocker.GetMock<IDownloadImportPipeline>().Setup(p => p.QueueVerification(It.IsAny<string>(), It.IsAny<Action>()))
                .Callback<string, Action>((_, work) => verify = work).Returns(true);
            if (fails)
            {
                Mocker.GetMock<IVersionImportRecoveryService>().Setup(r => r.CompletePublished(pending)).Throws(new IOException("Verification failed"));
            }

            Subject.Import(_trackedDownload);
            Assert.That(_trackedDownload.State, Is.EqualTo(TrackedDownloadState.Importing));
            Assert.That(result.Result, Is.EqualTo(ImportResultType.PendingVerification));
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
            Assert.That(verify, Is.Not.Null);
            verify();
            Assert.That(_trackedDownload.State, Is.EqualTo(fails ? TrackedDownloadState.ImportBlocked : TrackedDownloadState.Imported));
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<DownloadCompletedEvent>()), fails ? Times.Never() : Times.Once());
        }

        [Test]
        public void should_notify_activity_before_file_processing_and_after_pending_status_is_updated()
        {
            var states = new List<TrackedDownloadState>();
            Mocker.GetMock<IEventAggregator>()
                .Setup(e => e.PublishEvent(It.IsAny<TrackedDownloadStateChangedEvent>()))
                .Callback<TrackedDownloadStateChangedEvent>(e => states.Add(e.Download.State));
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                .Returns(() =>
                {
                    Assert.That(states, Is.EqualTo(new[] { TrackedDownloadState.Importing }));
                    return new List<ImportResult>();
                });

            Subject.Import(_trackedDownload);

            Assert.That(states, Is.EqualTo(new[] { TrackedDownloadState.Importing, TrackedDownloadState.ImportPending }));
            Assert.That(_trackedDownload.StatusMessages, Is.Not.Empty);
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_rejected()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                .Returns(new List<ImportResult>
                {
                    new ImportResult(
                        new ImportDecision(
                            new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" },
                            new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")),
                        "Test Failure"),

                    new ImportResult(
                        new ImportDecision(
                            new LocalMovie { Path = @"C:\TestPath\Droned.1999.mkv" },
                            new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")),
                        "Test Failure")
                });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent<DownloadCompletedEvent>(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_no_movies_were_parsed()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                .Returns(new List<ImportResult>
                {
                    new ImportResult(
                        new ImportDecision(
                            new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" },
                            new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")),
                        "Test Failure"),

                    new ImportResult(
                        new ImportDecision(
                            new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" },
                            new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")),
                        "Test Failure")
                });

            _trackedDownload.RemoteMovie.Movie = new Movie();

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_skipped()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure"),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_mark_as_imported_if_all_movies_were_imported_but_extra_files_were_not()
        {
            GivenSeriesMatch();

            _trackedDownload.RemoteMovie.Movie = new Movie();

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie })),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure")
               });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_the_download_can_be_tracked_using_the_source_movieid()
        {
            GivenABadlyNamedDownload();

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie }))
               });

            Mocker.GetMock<IMovieService>()
                  .Setup(v => v.GetMovie(It.IsAny<int>()))
                  .Returns(BuildRemoteMovie().Movie);

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        private void AssertNotImported()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        [TestCase(DownloadReservationState.Submitted, 7, false)]
        [TestCase(DownloadReservationState.Imported, 8, false)]
        [TestCase(DownloadReservationState.Imported, 7, true)]
        public void reserved_download_should_only_complete_after_its_own_verified_import(DownloadReservationState state, int version, bool expected)
        {
            _trackedDownload.MovieVersionId = 7;
            _trackedDownload.DownloadReservationId = 3;
            Mocker.GetMock<IDownloadReservationRepository>()
                .Setup(v => v.FindForDownload(_trackedDownload.DownloadClient, _trackedDownload.DownloadItem.DownloadId))
                .Returns(new List<DownloadReservation>
                {
                    new DownloadReservation { Id = 3, MovieId = _trackedDownload.RemoteMovie.Movie.Id, MovieVersionId = version, State = state }
                });

            Subject.VerifyImport(_trackedDownload, new List<ImportResult>()).Should().Be(expected);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), expected ? Times.Once() : Times.Never());
        }

        [TestCase(VersionFileImportState.Registered, 7, false)]
        [TestCase(VersionFileImportState.EventsPending, 7, false)]
        [TestCase(VersionFileImportState.Completed, 7, true)]
        [TestCase(VersionFileImportState.Registered, 8, true)]
        public void reserved_download_should_wait_for_its_import_finalization(VersionFileImportState state, int version, bool expected)
        {
            _trackedDownload.MovieVersionId = 7;
            _trackedDownload.DownloadReservationId = 3;
            Mocker.GetMock<IDownloadReservationRepository>()
                .Setup(v => v.FindForDownload(_trackedDownload.DownloadClient, _trackedDownload.DownloadItem.DownloadId))
                .Returns(new List<DownloadReservation> { new DownloadReservation { Id = 3, MovieId = _trackedDownload.RemoteMovie.Movie.Id, MovieVersionId = 7, State = DownloadReservationState.Imported } });
            Mocker.GetMock<IVersionFileImportRepository>()
                .Setup(v => v.ForMovie(_trackedDownload.RemoteMovie.Movie.Id))
                .Returns(new List<VersionFileImport> { new VersionFileImport { MovieVersionId = version, State = state } });

            Subject.VerifyImport(_trackedDownload, new List<ImportResult>()).Should().Be(expected);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), expected ? Times.Once() : Times.Never());
        }

        private void AssertImported()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Verify(v => v.ProcessPath(_trackedDownload.DownloadItem.OutputPath.FullPath, ImportMode.Auto, _trackedDownload.RemoteMovie.Movie, _trackedDownload.DownloadItem), Times.Once());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }
    }
}
