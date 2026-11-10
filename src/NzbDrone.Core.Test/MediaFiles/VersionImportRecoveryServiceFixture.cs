using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class VersionImportRecoveryServiceFixture : CoreTest<VersionImportRecoveryService>
    {
        private VersionFileImport _operation;
        private List<VersionFileImport> _operations;
        private List<string> _calls;

        [SetUp]
        public void Setup()
        {
            _operation = Operation(42, 1);
            _operations = new List<VersionFileImport> { _operation };
            _calls = new List<string>();
            Mocker.GetMock<IVersionFileImportRepository>().Setup(v => v.Get(It.IsAny<int>())).Returns<int>(id => _operations.Single(o => o.Id == id));
            Mocker.GetMock<IVersionFileImportRepository>().Setup(v => v.Pending()).Returns(() => _operations.Where(o => o.IsActive).ToList());
            Mocker.GetMock<IVersionImportHandoffService>().Setup(v => v.Resume(It.IsAny<int>(), It.IsAny<int>())).Returns<int, int>((movie, id) =>
            {
                Advance(id, VersionFileImportState.Registered, "handoff");
                return new MovieFile();
            });
            Mocker.GetMock<IVersionImportSidecarService>().Setup(v => v.Resume(It.IsAny<int>(), It.IsAny<int>())).Callback<int, int>((movie, id) => Advance(id, VersionFileImportState.CleaningOldExtras, "sidecars"));
            Mocker.GetMock<IVersionImportCleanupService>().Setup(v => v.Resume(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<int?>()))
                .Callback<int, int, bool, bool, bool, string, int?>((movie, id, retry, extras, sources, note, index) => Advance(id, VersionFileImportState.EventsPending, "cleanup"));
            Mocker.GetMock<IVersionImportCompletionService>().Setup(v => v.Resume(It.IsAny<int>(), It.IsAny<int>())).Callback<int, int>((movie, id) => Advance(id, VersionFileImportState.Completed, "completion"));
        }

        private static VersionFileImport Operation(int id, int movie) => new VersionFileImport
        {
            Id = id, MovieId = movie, MovieVersionId = id, State = VersionFileImportState.Prepared,
            Updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Snapshot = new VersionFileImportSnapshot { Incoming = new MovieFile(), SourceHash = new string('A', 64) }
        };

        private void Advance(int id, VersionFileImportState state, string phase)
        {
            _calls.Add(phase);
            _operations.Single(o => o.Id == id).State = state;
        }

        [TestCase(VersionFileImportState.Prepared, "handoff,sidecars,cleanup,completion")]
        [TestCase(VersionFileImportState.Staging, "handoff,sidecars,cleanup,completion")]
        [TestCase(VersionFileImportState.Staged, "handoff,sidecars,cleanup,completion")]
        [TestCase(VersionFileImportState.BackingUp, "handoff,sidecars,cleanup,completion")]
        [TestCase(VersionFileImportState.Installing, "handoff,sidecars,cleanup,completion")]
        [TestCase(VersionFileImportState.Registered, "sidecars,cleanup,completion")]
        [TestCase(VersionFileImportState.Finalizing, "sidecars,cleanup,completion")]
        [TestCase(VersionFileImportState.CleaningOldExtras, "cleanup,completion")]
        [TestCase(VersionFileImportState.RecyclingBackup, "cleanup,completion")]
        [TestCase(VersionFileImportState.RemovingSource, "cleanup,completion")]
        [TestCase(VersionFileImportState.EventsPending, "completion")]
        [TestCase(VersionFileImportState.Completed, "")]
        public void should_resume_only_remaining_phases(VersionFileImportState state, string expected)
        {
            _operation.State = state;
            Subject.Resume(1, 42);
            Assert.That(string.Join(",", _calls), Is.EqualTo(expected));
            Assert.That(_operation.State, Is.EqualTo(VersionFileImportState.Completed));
        }

        [Test]
        public void wrong_movie_should_return_not_found_before_any_phase_runs()
        {
            var error = Assert.Throws<NzbDroneClientException>(() => Subject.Resume(2, 42));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public void stale_checkpoint_should_reject_request_without_mutating_diagnostics()
        {
            var checkpoint = VersionImportRecoveryCheckpoint.For(_operation);
            _operation.State = VersionFileImportState.Staging;
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42, checkpoint));
            Assert.That(_calls, Is.Empty);
            Mocker.GetMock<IVersionFileImportRepository>().Verify(v => v.RecordRecoveryResult(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void physical_retry_should_be_bound_to_current_cleanup_entry()
        {
            _operation.State = VersionFileImportState.RecyclingBackup;
            _operation.Snapshot.Cleanup = new List<VersionImportCleanupItem>
            {
                new VersionImportCleanupItem { Kind = VersionImportCleanupKind.OldExtra, State = VersionImportCleanupState.Finished },
                new VersionImportCleanupItem { Kind = VersionImportCleanupKind.Backup, State = VersionImportCleanupState.Recycling }
            };
            Subject.Resume(1, 42, VersionImportRecoveryCheckpoint.For(_operation), true, false, false, "Verified current backup");
            Mocker.GetMock<IVersionImportCleanupService>().Verify(v => v.Resume(1, 42, true, false, false, "Verified current backup", 1), Times.Once());
        }

        [Test]
        public void override_cannot_authorize_future_cleanup_from_prepared_state()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42, VersionImportRecoveryCheckpoint.For(_operation), true, false, false, "Retry requested"));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public void override_requires_checkpoint_and_nonempty_note()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42, null, true, false, false, "Verified"));
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42, VersionImportRecoveryCheckpoint.For(_operation), true));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public void preservation_cannot_apply_to_other_file_categories()
        {
            _operation.State = VersionFileImportState.RecyclingBackup;
            _operation.Snapshot.Cleanup = new List<VersionImportCleanupItem> { new VersionImportCleanupItem { Kind = VersionImportCleanupKind.Backup } };
            var checkpoint = VersionImportRecoveryCheckpoint.For(_operation);
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42, checkpoint, false, true, false, "Preserve extra"));
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42, checkpoint, false, false, true, "Preserve source"));
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public void phase_without_progress_should_stop_instead_of_looping()
        {
            _operation.State = VersionFileImportState.Registered;
            Mocker.GetMock<IVersionImportSidecarService>().Setup(v => v.Resume(1, 42));
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42));
            Mocker.GetMock<IVersionFileImportRepository>().Verify(v => v.RecordRecoveryResult(42, It.Is<string>(s => s.Contains("expected checkpoint"))), Times.Once());
        }

        [Test]
        public void cancellation_requires_fresh_checkpoint_and_pre_backup_state()
        {
            var checkpoint = VersionImportRecoveryCheckpoint.For(_operation);
            Subject.Cancel(1, 42, checkpoint, "Cancel unstarted import");
            Mocker.GetMock<IVersionImportHandoffService>().Verify(v => v.CancelBeforeBackup(1, 42, "Cancel unstarted import"), Times.Once());
            _operation.State = VersionFileImportState.BackingUp;
            _operation.OldMovieFileId = 12;
            _operation.Snapshot.PreviousExisted = true;
            Assert.Throws<NzbDroneClientException>(() => Subject.Cancel(1, 42, VersionImportRecoveryCheckpoint.For(_operation), "Too late"));
        }

        [Test]
        public void abandoned_handoff_without_previous_or_registered_file_can_be_cancelled()
        {
            _operation.State = VersionFileImportState.BackingUp;
            _operation.OldMovieFileId = null;
            _operation.NewMovieFileId = null;
            _operation.Snapshot.PreviousExisted = false;
            Assert.DoesNotThrow(() => Subject.Cancel(1, 42, VersionImportRecoveryCheckpoint.For(_operation), "Abandoned unpublished import"));
        }

        [Test]
        public void startup_should_continue_other_operations_after_blocked_import()
        {
            _operations.Add(Operation(43, 2));
            Mocker.GetMock<IVersionImportHandoffService>().Setup(v => v.Resume(1, 42)).Throws(new InvalidOperationException("Blocked source"));
            Subject.HandleAsync(new ApplicationStartedEvent());
            Assert.That(_operation.State, Is.EqualTo(VersionFileImportState.Prepared));
            Assert.That(_operations.Single(o => o.Id == 43).State, Is.EqualTo(VersionFileImportState.Completed));
            Mocker.GetMock<IVersionFileImportRepository>().Verify(v => v.RecordRecoveryResult(42, "Blocked source"), Times.Once());
        }
    }
}
