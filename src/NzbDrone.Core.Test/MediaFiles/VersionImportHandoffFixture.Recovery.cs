using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        private VersionImportRecoveryService Recovery()
        {
            Mocker.SetConstant<IVersionImportHandoffService>(Subject);
            Mocker.SetConstant<IVersionImportSidecarService>(Mocker.Resolve<VersionImportSidecarService>());
            Mocker.SetConstant<IVersionImportCleanupService>(Mocker.Resolve<VersionImportCleanupService>());
            Mocker.SetConstant<IVersionImportCompletionService>(Mocker.Resolve<VersionImportCompletionService>());
            return Mocker.Resolve<VersionImportRecoveryService>();
        }

        [Test]
        public void coordinator_should_run_prepared_intent_through_all_real_database_phases()
        {
            var op = Prepare();
            Recovery().Resume(_movie.Id, op.Id);
            AssertCompletedImport(op);
            var row = _operations.Get(op.Id);
            Assert.That(row.Snapshot.LastRecoveryAttempt, Is.Not.Null);
            Assert.That(row.Snapshot.LastRecoveryError, Is.Null);
            Assert.That(_operations.Pending(), Is.Empty);
        }

        [Test]
        public void coordinator_should_continue_registered_sidecars_through_completion()
        {
            var op = WithIncomingSidecar();
            Recovery().Resume(_movie.Id, op.Id);
            AssertCompletedImport(op);
            Assert.That(_disk[Full("300 (2006) - SDR.en.forced.srt")].Hash, Is.EqualTo(new string('F', 64)));
        }

        [Test]
        public void startup_should_recover_completed_backup_move_from_persisted_checkpoint()
        {
            var op = BeforeBackup();
            Move(Full(_old.RelativePath), Full(op.BackupRelativePath));
            Recovery().HandleAsync(new ApplicationStartedEvent());
            AssertCompletedImport(op);
        }

        [Test]
        public void startup_should_leave_ambiguous_recycling_pending_with_inspection_error()
        {
            var op = ReadyForCleanup();
            var backup = Full(op.BackupRelativePath);
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(backup, It.IsAny<string>())).Throws(new IOException("Interrupted recycle"));
            Assert.Throws<IOException>(() => RunCleanup(op));
            Recovery().HandleAsync(new ApplicationStartedEvent());
            var row = _operations.Get(op.Id);
            Assert.That(row.State, Is.EqualTo(VersionFileImportState.RecyclingBackup));
            Assert.That(row.Snapshot.LastRecoveryError, Does.Contain("explicitly retry"));
            Assert.That(row.Snapshot.LastRecoveryAttempt, Is.Not.Null);
            Assert.That(_disk.ContainsKey(backup), Is.True);
        }

        [Test]
        public void verified_retry_should_complete_current_entry_and_clear_recovery_error()
        {
            var op = ReadyForCleanup();
            var backup = Full(op.BackupRelativePath);
            var first = true;
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(backup, It.IsAny<string>())).Returns<string, string>((path, subfolder) =>
            {
                if (first)
                {
                    first = false;
                    throw new IOException("Interrupted recycle");
                }

                _disk.Remove(path);
                return "recycled/previous.mkv";
            });
            var recovery = Recovery();
            Assert.Throws<NzbDroneClientException>(() => recovery.Resume(_movie.Id, op.Id));
            var row = _operations.Get(op.Id);
            recovery.Resume(_movie.Id, op.Id, VersionImportRecoveryCheckpoint.For(row), true, false, false, "Inspected interrupted video backup");
            AssertCompletedImport(op);
            Assert.That(_operations.Get(op.Id).Snapshot.LastRecoveryError, Is.Null);
        }

        [Test]
        public void preservation_override_should_not_apply_to_next_changed_source()
        {
            var op = ReadyForCleanup();
            var extraSource = op.Snapshot.IncomingExtras.Single().SourcePath;
            _disk[_source] = (150, new string('D', 64));
            _disk[extraSource] = (20, new string('D', 64));
            var recovery = Recovery();
            Assert.Throws<NzbDroneClientException>(() => recovery.Resume(_movie.Id, op.Id));
            var row = _operations.Get(op.Id);
            var token = VersionImportRecoveryCheckpoint.For(row);
            Assert.Throws<NzbDroneClientException>(() => recovery.Resume(_movie.Id, op.Id, token, false, false, true, "Retain changed video source only"));
            row = _operations.Get(op.Id);
            var sources = row.Snapshot.Cleanup.Where(i => i.Kind == VersionImportCleanupKind.Source).ToList();
            Assert.That(sources[0].State, Is.EqualTo(VersionImportCleanupState.Finished));
            Assert.That(sources[0].Preserve, Is.True);
            Assert.That(sources[1].State, Is.EqualTo(VersionImportCleanupState.Prepared));
            Assert.That(sources[1].Preserve, Is.False);
            Assert.Throws<NzbDroneClientException>(() => recovery.Resume(_movie.Id, op.Id, token, false, false, true, "Stale video verification"));
            recovery.Resume(_movie.Id, op.Id, VersionImportRecoveryCheckpoint.For(row), false, false, true, "Retain changed subtitle source");
            AssertCompletedImport(op);
            Assert.That(_disk.ContainsKey(_source) && _disk.ContainsKey(extraSource), Is.True);
        }

        [Test]
        public void cancellation_should_require_fresh_checkpoint_and_retain_partial_stage_for_inspection()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileImportState.Prepared, VersionFileImportState.Staging);
            _disk.Add(Full(op.StagingRelativePath), (10, new string('D', 64)));
            var recovery = Recovery();
            var token = VersionImportRecoveryCheckpoint.For(_operations.Get(op.Id));
            recovery.Cancel(_movie.Id, op.Id, token, "Cancel interrupted staging copy");
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Cancelled));
            Assert.That(_disk.ContainsKey(Full(op.StagingRelativePath)), Is.True);
            Assert.That(_disk.ContainsKey(Full(_old.RelativePath)), Is.True);
            Assert.That(_operations.Pending(), Is.Empty);
            Assert.Throws<NzbDroneClientException>(() => recovery.Resume(_movie.Id, op.Id));
        }

        [Test]
        public void checkpoint_should_roundtrip_without_changing_and_track_durable_progress()
        {
            var op = Prepare();
            var before = VersionImportRecoveryCheckpoint.For(_operations.Get(op.Id));
            Assert.That(VersionImportRecoveryCheckpoint.For(_operations.Get(op.Id)), Is.EqualTo(before));
            _operations.Advance(op.Id, VersionFileImportState.Prepared, VersionFileImportState.Staging);
            Assert.That(VersionImportRecoveryCheckpoint.For(_operations.Get(op.Id)), Is.Not.EqualTo(before));
        }

        [Test]
        public void startup_replay_should_complete_interrupted_handoff_on_real_filesystem()
        {
            var root = TempFolder;
            _movie.Path = Path.Combine(root, "300 (2006)");
            var downloads = Path.Combine(root, "Downloads");
            var recycle = Path.Combine(root, "Recycle");
            Directory.CreateDirectory(_movie.Path);
            Directory.CreateDirectory(downloads);
            Directory.CreateDirectory(recycle);
            using (var connection = Mocker.Resolve<IMainDatabase>().OpenConnection())
            {
                connection.Execute("UPDATE Movies SET Path = @path WHERE Id = @Id", new { path = _movie.Path, _movie.Id });
            }

            _source = Path.Combine(downloads, "300.2006.SDR.mkv");
            var subtitle = Path.Combine(downloads, "300.2006.SDR.en.forced.srt");
            var incoming = Enumerable.Repeat((byte)1, 150).ToArray();
            var hdr = Enumerable.Repeat((byte)3, 100).ToArray();
            System.IO.File.WriteAllBytes(_source, incoming);
            System.IO.File.WriteAllBytes(Full(_old.RelativePath), Enumerable.Repeat((byte)2, 100).ToArray());
            System.IO.File.WriteAllBytes(Full(_other.RelativePath), hdr);
            System.IO.File.WriteAllText(subtitle, "captured subtitle");
            Mocker.GetMock<IMovieService>().Setup(v => v.AllMoviePaths()).Returns(new Dictionary<int, string> { { _movie.Id, _movie.Path } });
            Mocker.GetMock<IRootFolderService>().Setup(v => v.GetBestRootFolderPath(_movie.Path, null)).Returns(root);
            Mocker.GetMock<IConfigService>().SetupGet(v => v.ImportExtraFiles).Returns(true);
            Mocker.GetMock<IConfigService>().SetupGet(v => v.ExtraFileExtensions).Returns("srt");
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileExists(It.IsAny<string>())).Returns<string>(System.IO.File.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns<string>(Directory.Exists);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFileSize(It.IsAny<string>())).Returns<string>(p => new FileInfo(p).Length);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileGetLastWrite(It.IsAny<string>())).Returns<string>(System.IO.File.GetLastWriteTimeUtc);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.OpenReadStream(It.IsAny<string>())).Returns<string>(System.IO.File.OpenRead);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.CreateFolder(It.IsAny<string>())).Callback<string>(p => Directory.CreateDirectory(p));
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFiles(It.IsAny<string>(), false)).Returns<string, bool>((p, recurse) => Directory.GetFiles(p));
            Mocker.GetMock<IDiskTransferService>().Setup(v => v.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false)).Returns<string, string, TransferMode, bool>((s, d, mode, overwrite) =>
            {
                System.IO.File.Copy(s, d, false);
                return TransferMode.Copy;
            });
            var interrupted = false;
            Mocker.GetMock<IDiskProvider>().Setup(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Callback<string, string, bool>((s, d, overwrite) =>
            {
                System.IO.File.Move(s, d, false);
                if (!interrupted && d.EndsWith("previous.mkv", StringComparison.Ordinal))
                {
                    interrupted = true;
                    throw new IOException("Simulated stop after backup move");
                }
            });
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((p, subfolder) =>
            {
                var destination = Path.Combine(recycle, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(p));
                System.IO.File.Move(p, destination, false);
                return destination;
            });
            Mocker.SetConstant<IVersionImportFileVerifier>(Mocker.Resolve<VersionImportFileVerifier>());
            var op = Prepare();
            Assert.Throws<IOException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.BackingUp));
            Recovery().HandleAsync(new ApplicationStartedEvent());
            AssertCompletedImport(op);
            Assert.That(System.IO.File.ReadAllBytes(Full(op.DestinationRelativePath)), Is.EqualTo(incoming));
            Assert.That(System.IO.File.ReadAllBytes(Full(_other.RelativePath)), Is.EqualTo(hdr));
            Assert.That(System.IO.File.ReadAllText(Full("300 (2006) - SDR.en.forced.srt")), Is.EqualTo("captured subtitle"));
            Assert.That(System.IO.File.Exists(_source) || System.IO.File.Exists(subtitle), Is.False);
            Assert.That(Directory.GetFiles(recycle).Length, Is.EqualTo(3));
        }
    }
}
