using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        private VersionFileImport ReadyForCompletion(bool sidecar = true)
        {
            var op = ReadyForCleanup(sidecar);
            RunCleanup(op);
            Mocker.GetMock<IEventAggregator>().Invocations.Clear();
            return _operations.Get(op.Id);
        }

        private void Complete(VersionFileImport op) => Mocker.Resolve<VersionImportCompletionService>().Resume(_movie.Id, op.Id);

        private void AssertCompletedImport(VersionFileImport op)
        {
            var row = _operations.Get(op.Id);
            Assert.That(row.State, Is.EqualTo(VersionFileImportState.Completed));
            Assert.That(row.IsActive, Is.False);
            Assert.That(row.Snapshot.Events.Deliveries.All(d => d.State == VersionImportDeliveryState.Dispatched), Is.True);
            Assert.That(Db.All<MovieHistory>().Count, Is.EqualTo(op.OldMovieFileId.HasValue ? 2 : 1));
            Assert.That(Db.All<MovieHistory>().All(h => h.MovieId == _movie.Id && h.MovieVersionId == _sdr.Id && h.Data["versionFileImportId"] == op.Id.ToString()), Is.True);
            Assert.DoesNotThrow(() => Mocker.Resolve<VersionFileDeletionRepository>().CheckReady(_movie.Id, _sdr.Id));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _hdr.Id).MovieFileId, Is.EqualTo(_other.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(row.NewMovieFileId));
        }

        [Test]
        public void completion_should_commit_audit_before_ordered_dispatch_and_release_operation_gate()
        {
            var op = ReadyForCompletion();
            MovieFileImportedEvent imported = null;
            Mocker.GetMock<IEventAggregator>().Setup(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>())).Callback<MovieFileImportedEvent>(e =>
            {
                imported = e;
                Assert.That(Db.All<MovieHistory>().Count, Is.EqualTo(2));
                Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.EventsPending));
                Assert.Throws<VersionFileDeletionPendingException>(() => Mocker.Resolve<VersionFileDeletionRepository>().CheckReady(_movie.Id, _sdr.Id));
            });
            Complete(op);
            AssertCompletedImport(op);
            Assert.That(imported.ImportOperationId, Is.EqualTo(op.Id));
            Assert.That(imported.MovieInfo.MovieVersionId, Is.EqualTo(_sdr.Id));
            Assert.That(imported.ImportedMovie.Movie.ManagedVersion.Id, Is.EqualTo(_sdr.Id));
            Assert.That(imported.OldFiles.Single().MovieFile.Id, Is.EqualTo(_old.Id));
            Assert.That(imported.OldFiles.Single().RecycleBinPath, Does.StartWith("recycled/"));
            Assert.That(imported.DownloadId, Is.Null);
            Assert.That(Db.All<DownloadHistory>(), Is.Empty);
            var order = Mocker.GetMock<IEventAggregator>().Invocations.Select(i => i.Method.GetGenericArguments().Single().Name).ToArray();
            Assert.That(order, Is.EqualTo(new[] { nameof(MovieFileDeletedEvent), nameof(DeleteCompletedEvent), nameof(MovieFileAddedEvent), nameof(MovieFileImportedEvent) }));
            var count = order.Length;
            Complete(op);
            Assert.That(Mocker.GetMock<IEventAggregator>().Invocations.Count, Is.EqualTo(count));
            AssertCompletedImport(op);
        }

        [Test]
        public void first_acquisition_should_only_publish_added_and_imported_events()
        {
            Mocker.Resolve<MediaFileRepository>().DeleteForVersion(_old, false);
            _disk.Remove(Full(_old.RelativePath));
            var op = ReadyForCompletion(false);
            Complete(op);
            AssertCompletedImport(op);
            Assert.That(_operations.Get(op.Id).Snapshot.Events.DeletedHistoryId, Is.Null);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileDeletedEvent>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Once());
        }

        [Test]
        public void completion_should_record_exact_bound_download_once()
        {
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "download-1", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            var op = ReadyForCompletion();
            Complete(op);
            AssertCompletedImport(op);
            var history = Db.Single<DownloadHistory>();
            Assert.That(history.EventType, Is.EqualTo(DownloadHistoryEventType.FileImported));
            Assert.That(history.MovieVersionId, Is.EqualTo(_sdr.Id));
            Assert.That(history.DownloadClientId, Is.EqualTo(7));
            Assert.That(history.DownloadId, Is.EqualTo("download-1"));
            Assert.That(history.Data["versionFileImportId"], Is.EqualTo(op.Id.ToString()));
            Complete(op);
            Assert.That(Db.All<DownloadHistory>().Count, Is.EqualTo(1));
        }

        [Test]
        public void failed_atomic_audit_commit_should_not_publish_or_leave_partial_rows()
        {
            var database = Mocker.Resolve<IMainDatabase>();
            if (database.DatabaseType != DatabaseType.SQLite)
            {
                Assert.Ignore("SQLite fault injection trigger");
            }

            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "download-1", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            var op = ReadyForCompletion();
            using (var connection = database.OpenConnection())
            {
                connection.Execute("CREATE TRIGGER FailImportAudit BEFORE INSERT ON DownloadHistory BEGIN SELECT RAISE(ABORT, 'Simulated audit failure'); END;");
            }

            Assert.Catch(() => Complete(op));
            Assert.That(Db.All<MovieHistory>(), Is.Empty);
            Assert.That(Db.All<DownloadHistory>(), Is.Empty);
            Assert.That(_operations.Get(op.Id).Snapshot.Events, Is.Null);
            Assert.That(Mocker.GetMock<IEventAggregator>().Invocations, Is.Empty);
            using (var connection = database.OpenConnection())
            {
                connection.Execute("DROP TRIGGER FailImportAudit;");
            }

            Complete(op);
            AssertCompletedImport(op);
            Assert.That(Db.All<DownloadHistory>().Count, Is.EqualTo(1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void interrupted_import_event_should_retry_without_duplicate_audit_or_prior_events(bool afterDelivery)
        {
            var op = ReadyForCompletion();
            var attempts = 0;
            var accepted = 0;
            Mocker.GetMock<IEventAggregator>().Setup(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>())).Callback<MovieFileImportedEvent>(e =>
            {
                attempts++;
                if (attempts == 1)
                {
                    if (afterDelivery)
                    {
                        accepted++;
                    }

                    throw new IOException("Simulated process stop around event dispatch");
                }

                accepted++;
            });
            Assert.Throws<IOException>(() => Complete(op));
            Assert.That(_operations.Get(op.Id).Snapshot.Events.Deliveries.Last().State, Is.EqualTo(VersionImportDeliveryState.Dispatching));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.EventsPending));
            Complete(op);
            AssertCompletedImport(op);
            Assert.That(accepted, Is.EqualTo(afterDelivery ? 2 : 1));
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileDeletedEvent>()), Times.Once());
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileAddedEvent>()), Times.Once());
        }

        [Test]
        public void completion_cannot_release_gate_before_every_delivery_is_checkpointed()
        {
            var op = ReadyForCompletion();
            _operations.RecordEventHistory(op.Id);
            Assert.Throws<InvalidOperationException>(() => _operations.CompleteEvents(op.Id));
            Assert.Throws<InvalidOperationException>(() => _operations.BeginEventDelivery(op.Id, 3));
            Assert.That(_operations.Get(op.Id).IsActive, Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void changed_or_missing_audit_should_block_delivery_and_completion(bool remove)
        {
            var op = ReadyForCompletion();
            _operations.RecordEventHistory(op.Id);
            var id = _operations.Get(op.Id).Snapshot.Events.ImportHistoryId;
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute(remove ? "DELETE FROM History WHERE Id = @id" : "UPDATE History SET MovieVersionId = 999 WHERE Id = @id", new { id });
            Assert.Throws<InvalidOperationException>(() => Complete(op));
            Assert.That(Mocker.GetMock<IEventAggregator>().Invocations, Is.Empty);
            Assert.That(_operations.Get(op.Id).IsActive, Is.True);
        }

        [Test]
        public void completed_operation_replay_should_not_validate_or_reclaim_a_later_assignment()
        {
            var op = ReadyForCompletion();
            Complete(op);
            var count = Mocker.GetMock<IEventAggregator>().Invocations.Count;
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE MovieVersions SET MovieFileId = NULL WHERE Id = @Id", _sdr);
            Assert.DoesNotThrow(() => Complete(op));
            Assert.That(Mocker.GetMock<IEventAggregator>().Invocations.Count, Is.EqualTo(count));
        }

        [Test]
        public void import_event_should_restore_captured_score_formats_and_release_after_roundtrip()
        {
            Mocker.GetMock<IMovieVersionImportService>().Setup(v => v.ValidateForWrite(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>(), true)).Callback<LocalMovie, DownloadClientItem, bool>((local, client, download) =>
            {
                local.CustomFormatScore = 123;
                local.CustomFormats = new List<CustomFormat> { new CustomFormat { Id = 5, Name = "Original format" } };
                local.Release = new GrabbedReleaseInfo { Title = "Original release", Indexer = "Original indexer", Size = 150, MovieIds = new List<int> { _movie.Id } };
            });
            var op = ReadyForCompletion();
            MovieFileImportedEvent imported = null;
            Mocker.GetMock<IEventAggregator>().Setup(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>())).Callback<MovieFileImportedEvent>(e => imported = e);
            Complete(op);
            Assert.That(imported.MovieInfo.CustomFormatScore, Is.EqualTo(123));
            Assert.That(imported.MovieInfo.CustomFormats.Single().Name, Is.EqualTo("Original format"));
            Assert.That(imported.MovieInfo.Release.Title, Is.EqualTo("Original release"));
            Assert.That(imported.MovieInfo.Release.MovieIds, Is.EqualTo(new[] { _movie.Id }));
            Assert.That(Db.All<MovieHistory>().Single(h => h.EventType == MovieHistoryEventType.DownloadFolderImported).Data["customFormatScore"], Is.EqualTo("123"));
        }

        [Test]
        public void journal_history_subscribers_should_not_duplicate_precommitted_audit()
        {
            Mocker.SetConstant<IHistoryRepository>(Mocker.Resolve<HistoryRepository>());
            Mocker.SetConstant<IDownloadHistoryRepository>(Mocker.Resolve<DownloadHistoryRepository>());
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "download-1", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            var op = ReadyForCompletion();
            Mocker.GetMock<IEventAggregator>().Setup(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>())).Callback<MovieFileImportedEvent>(e =>
            {
                Mocker.Resolve<HistoryService>().Handle(e);
                Mocker.Resolve<DownloadHistoryService>().Handle(e);
                Mocker.Resolve<HistoryService>().Handle(e);
                Mocker.Resolve<DownloadHistoryService>().Handle(e);
            });
            Mocker.GetMock<IEventAggregator>().Setup(v => v.PublishEvent(It.IsAny<MovieFileDeletedEvent>())).Callback<MovieFileDeletedEvent>(e => Mocker.Resolve<HistoryService>().Handle(e));
            Complete(op);
            AssertCompletedImport(op);
            Assert.That(Db.All<DownloadHistory>().Count, Is.EqualTo(1));
        }

        [Test]
        public void file_changed_by_subscriber_should_block_completion_without_replaying_acknowledged_events()
        {
            var op = ReadyForCompletion();
            var path = Full(op.DestinationRelativePath);
            Mocker.GetMock<IEventAggregator>().Setup(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>())).Callback<MovieFileImportedEvent>(e => _disk[path] = (150, new string('D', 64)));
            Assert.Throws<InvalidOperationException>(() => Complete(op));
            Assert.That(_operations.Get(op.Id).Snapshot.Events.Deliveries.All(d => d.State == VersionImportDeliveryState.Dispatched), Is.True);
            Assert.That(_operations.Get(op.Id).IsActive, Is.True);
            _disk[path] = (150, new string('A', 64));
            Complete(op);
            AssertCompletedImport(op);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Once());
        }

        [Test]
        public void download_completion_should_wait_until_import_events_are_checkpointed()
        {
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "download-1", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            Mocker.SetConstant<IDownloadReservationRepository>(Mocker.Resolve<DownloadReservationRepository>());
            var op = ReadyForCompletion();
            var tracked = new TrackedDownload
            {
                MovieVersionId = _sdr.Id, DownloadReservationId = _reservation.Id, DownloadClient = 7,
                DownloadItem = new DownloadClientItem { DownloadId = "download-1" }, RemoteMovie = new RemoteMovie { Movie = Root() }
            };
            var completedDownloads = Mocker.Resolve<CompletedDownloadService>();
            Assert.That(completedDownloads.VerifyImport(tracked, new List<ImportResult>()), Is.False);
            _operations.RecordEventHistory(op.Id);
            Assert.That(completedDownloads.VerifyImport(tracked, new List<ImportResult>()), Is.False);
            Complete(op);
            Assert.That(completedDownloads.VerifyImport(tracked, new List<ImportResult>()), Is.True);
            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
        }

        [Test]
        public void changed_sidecar_owner_should_block_audit_and_events_before_dispatch()
        {
            var op = ReadyForCompletion();
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE SubtitleFiles SET MovieFileId = @Id", _other);
            Assert.Throws<InvalidOperationException>(() => Complete(op));
            Assert.That(Db.All<MovieHistory>(), Is.Empty);
            Assert.That(_operations.Get(op.Id).Snapshot.Events, Is.Null);
            Assert.That(Mocker.GetMock<IEventAggregator>().Invocations, Is.Empty);
        }

        [Test]
        public void completion_before_physical_cleanup_should_be_rejected()
        {
            var op = ReadyForCleanup();
            Assert.Throws<InvalidOperationException>(() => Complete(op));
            Assert.That(Db.All<MovieHistory>(), Is.Empty);
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.CleaningOldExtras));
        }

        [Test]
        public void failed_final_checkpoint_should_retry_without_republishing_events()
        {
            var database = Mocker.Resolve<IMainDatabase>();
            if (database.DatabaseType != DatabaseType.SQLite)
            {
                Assert.Ignore("SQLite fault injection trigger");
            }

            var op = ReadyForCompletion();
            using (var connection = database.OpenConnection())
            {
                connection.Execute("CREATE TRIGGER FailImportComplete BEFORE UPDATE ON VersionFileImports WHEN NEW.State = 11 BEGIN SELECT RAISE(ABORT, 'Simulated completion commit failure'); END;");
            }

            Assert.Catch(() => Complete(op));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.EventsPending));
            Assert.That(_operations.Get(op.Id).Snapshot.Events.Deliveries.All(d => d.State == VersionImportDeliveryState.Dispatched), Is.True);
            var count = Mocker.GetMock<IEventAggregator>().Invocations.Count;
            using (var connection = database.OpenConnection())
            {
                connection.Execute("DROP TRIGGER FailImportComplete;");
            }

            Complete(op);
            AssertCompletedImport(op);
            Assert.That(Mocker.GetMock<IEventAggregator>().Invocations.Count, Is.EqualTo(count));
        }

        [Test]
        public async Task concurrent_audit_preparation_should_commit_one_set_of_records()
        {
            var op = ReadyForCompletion();
            var repository = _operations;
            await Task.WhenAll(Task.Run(() => repository.RecordEventHistory(op.Id)), Task.Run(() => repository.RecordEventHistory(op.Id)));
            Assert.That(Db.All<MovieHistory>().Count, Is.EqualTo(2));
            Assert.That(_operations.Get(op.Id).Snapshot.Events.ImportHistoryId, Is.GreaterThan(0));
            Complete(op);
            AssertCompletedImport(op);
        }
    }
}
