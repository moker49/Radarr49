using System.Collections.Generic;
using System.Linq;
using System.Net;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class VersionFileImportRecoveryApiFixture : TestBase<VersionFileImportsController>
    {
        private VersionFileImport _operation;

        [SetUp]
        public void Setup()
        {
            _operation = new VersionFileImport
            {
                Id = 42, MovieId = 1, MovieVersionId = 7, OldMovieFileId = 11, NewMovieFileId = 12,
                State = VersionFileImportState.RecyclingBackup, SourcePath = "download.mkv",
                Snapshot = new VersionFileImportSnapshot
                {
                    Incoming = new MovieFile { Size = 100 }, SourceHash = new string('A', 64), LastRecoveryError = "Inspect current backup",
                    IncomingExtras = new List<VersionImportSourceExtra> { new VersionImportSourceExtra { SourcePath = "download.en.srt", State = VersionImportExtraState.Registered, RegisteredExtraId = 8 } },
                    Cleanup = new List<VersionImportCleanupItem>
                    {
                        new VersionImportCleanupItem { Kind = VersionImportCleanupKind.OldExtra, State = VersionImportCleanupState.Finished },
                        new VersionImportCleanupItem { Kind = VersionImportCleanupKind.Backup, Path = "private/previous.mkv", Existed = true, Size = 90, State = VersionImportCleanupState.Recycling }
                    },
                    Events = new VersionImportEventProgress { ImportHistoryId = 10, Deliveries = new List<VersionImportDelivery> { new VersionImportDelivery { Kind = VersionImportEventKind.FileImported } } }
                }
            };
            Mocker.GetMock<IVersionFileImportRepository>().Setup(v => v.Get(42)).Returns(_operation);
            Mocker.GetMock<IVersionFileImportRepository>().Setup(v => v.ForMovie(1)).Returns(new List<VersionFileImport> { _operation });
            Mocker.GetMock<IVersionImportRecoveryService>().Setup(v => v.Get(1, 42)).Returns(_operation);
        }

        [Test]
        public void inspection_should_expose_exact_recovery_progress_and_checkpoint()
        {
            var resource = Subject.Get(1).Single();
            Assert.That(resource.Checkpoint, Is.EqualTo(VersionImportRecoveryCheckpoint.For(_operation)));
            Assert.That(resource.CurrentCleanupIndex, Is.EqualTo(1));
            Assert.That(resource.Cleanup[1].Path, Is.EqualTo("private/previous.mkv"));
            Assert.That(resource.Cleanup[1].State, Is.EqualTo(VersionImportCleanupState.Recycling));
            Assert.That(resource.Sidecars.Single().RegisteredExtraId, Is.EqualTo(8));
            Assert.That(resource.ImportHistoryId, Is.EqualTo(10));
            Assert.That(resource.LastRecoveryError, Is.EqualTo("Inspect current backup"));
            Assert.That(resource.IsActive, Is.True);
            Assert.That(resource.CanCancel, Is.False);
        }

        [Test]
        public void individual_inspection_should_use_route_owned_service_lookup()
        {
            Assert.That(Subject.GetOperation(1, 42).Id, Is.EqualTo(42));
            Mocker.GetMock<IVersionImportRecoveryService>().Verify(v => v.Get(1, 42), Times.Once());
        }

        [Test]
        public void empty_resume_should_not_authorize_physical_retry_or_preservation()
        {
            Subject.Resume(1, 42, new VersionFileImportResumeResource());
            Mocker.GetMock<IVersionImportRecoveryService>().Verify(v => v.Resume(1, 42, null, false, false, false, null), Times.Once());
        }

        [Test]
        public void resume_should_forward_checkpoint_and_override_choices_exactly()
        {
            Subject.Resume(1, 42, new VersionFileImportResumeResource { Checkpoint = "checkpoint", RetryPhysical = true, PreserveChangedSources = true, Note = "Inspected file" });
            Mocker.GetMock<IVersionImportRecoveryService>().Verify(v => v.Resume(1, 42, "checkpoint", true, false, true, "Inspected file"), Times.Once());
        }

        [Test]
        public void cancel_should_forward_checkpoint_and_note()
        {
            Subject.Cancel(1, 42, new VersionFileImportCancelResource { Checkpoint = "checkpoint", Note = "Cancel partial copy" });
            Mocker.GetMock<IVersionImportRecoveryService>().Verify(v => v.Cancel(1, 42, "checkpoint", "Cancel partial copy"), Times.Once());
        }

        [Test]
        public void real_recovery_should_reject_wrong_movie_before_phase_dispatch()
        {
            Mocker.SetConstant<IVersionImportRecoveryService>(Mocker.Resolve<VersionImportRecoveryService>());
            var error = Assert.Throws<NzbDroneClientException>(() => Subject.GetOperation(2, 42));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(2, 42, new VersionFileImportResumeResource()));
            Assert.That(Mocker.GetMock<IVersionImportCleanupService>().Invocations, Is.Empty);
        }

        [Test]
        public void real_recovery_should_reject_stale_api_checkpoint_without_authorizing_cleanup()
        {
            Mocker.SetConstant<IVersionImportRecoveryService>(Mocker.Resolve<VersionImportRecoveryService>());
            var checkpoint = Subject.Get(1).Single().Checkpoint;
            _operation.Snapshot.Cleanup[1].State = VersionImportCleanupState.Finished;
            var error = Assert.Throws<NzbDroneClientException>(() => Subject.Resume(1, 42, new VersionFileImportResumeResource { Checkpoint = checkpoint, RetryPhysical = true, Note = "Inspected backup" }));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(Mocker.GetMock<IVersionImportCleanupService>().Invocations, Is.Empty);
        }
    }
}
