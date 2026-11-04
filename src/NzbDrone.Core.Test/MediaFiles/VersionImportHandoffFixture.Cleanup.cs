using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        private bool _copyOnly;
        private List<string> _recycled;

        [SetUp]
        public void SetupCleanup()
        {
            _copyOnly = false;
            _recycled = new List<string>();
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((p, s) =>
            {
                _disk.Remove(p);
                _recycled.Add(p);
                return "recycled/" + Path.GetFileName(p);
            });
        }

        private VersionFileImport ReadyForCleanup(bool sidecar = true)
        {
            var op = sidecar ? WithIncomingSidecar() : Prepare();
            if (!sidecar)
            {
                Subject.Resume(_movie.Id, op.Id);
            }

            ResumeSidecars(op);
            return _operations.Get(op.Id);
        }

        private void RunCleanup(VersionFileImport op, bool retry = false, bool preserveExtras = false, bool preserveSources = false, string note = null) =>
            Mocker.Resolve<VersionImportCleanupService>().Resume(_movie.Id, op.Id, retry, preserveExtras, preserveSources, note);

        private SubtitleFile ObsoleteSubtitle(string suffix = ".de.srt")
        {
            var file = Db.Insert(new SubtitleFile { MovieId = _movie.Id, MovieFileId = _old.Id, RelativePath = "300 (2006) - SDR" + suffix, Extension = ".srt", Language = Language.German });
            _disk.Add(Full(file.RelativePath), (10, new string('E', 64)));
            return file;
        }

        private void AssertCleanupFinished(VersionFileImport op)
        {
            var row = _operations.Get(op.Id);
            Assert.That(row.State, Is.EqualTo(VersionFileImportState.EventsPending));
            Assert.That(row.IsActive, Is.True);
            Assert.That(row.Snapshot.Cleanup.All(i => i.State == VersionImportCleanupState.Finished), Is.True);
            Assert.That(_disk[Full(row.DestinationRelativePath)].Hash, Is.EqualTo(new string('A', 64)));
            Assert.That(_disk[Full(_other.RelativePath)].Hash, Is.EqualTo(new string('C', 64)));
            Assert.That(Db.All<MovieVersion>().All(v => v.Monitored), Is.True);
            foreach (var extra in row.Snapshot.IncomingExtras)
            {
                Assert.That(_disk[Full(extra.DestinationRelativePath)].Hash, Is.EqualTo(new string('F', 64)));
                Assert.That(Db.All<SubtitleFile>().Any(f => f.Id == extra.RegisteredExtraId && f.MovieFileId == row.NewMovieFileId), Is.True);
            }
        }

        [Test]
        public void empty_cleanup_phases_must_not_rehash_the_entire_video()
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE \"MovieVersions\" SET \"MovieFileId\" = NULL WHERE \"Id\" = @Id", _sdr);
            connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\" = 0 WHERE \"Id\" = @Id", _movie);
            connection.Execute("DELETE FROM \"MovieFiles\" WHERE \"Id\" = @Id", _old);
            _disk.Remove(Full(_old.RelativePath));
            var op = ReadyForCleanup(false);
            Mocker.GetMock<IVersionImportFileVerifier>().Invocations.Clear();

            RunCleanup(op);

            AssertCleanupFinished(op);
            Mocker.GetMock<IVersionImportFileVerifier>().Verify(v => v.Hash(Full(op.DestinationRelativePath)), Times.Once());
            Mocker.GetMock<IVersionImportFileVerifier>().Verify(v => v.Hash(_source), Times.Exactly(2));
        }

        [Test]
        public void no_sidecars_must_not_trigger_video_hashing_during_the_sidecar_phase()
        {
            var op = Prepare();
            Subject.Resume(_movie.Id, op.Id);
            Mocker.GetMock<IVersionImportFileVerifier>().Invocations.Clear();

            ResumeSidecars(op);

            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.CleaningOldExtras));
            Mocker.GetMock<IVersionImportFileVerifier>().Verify(v => v.Hash(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void cleanup_should_retire_obsolete_extras_and_backups_without_touching_replacements()
        {
            var obsolete = ObsoleteSubtitle();
            var op = ReadyForCleanup();
            var sourceExtra = op.Snapshot.IncomingExtras.Single().SourcePath;
            Mocker.GetMock<IEventAggregator>().Invocations.Clear();
            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(_disk.ContainsKey(Full(obsolete.RelativePath)), Is.False);
            Assert.That(_disk.ContainsKey(Full(op.BackupRelativePath)), Is.False);
            Assert.That(_disk.ContainsKey(_source), Is.False);
            Assert.That(_disk.ContainsKey(sourceExtra), Is.False);
            Assert.That(Db.All<SubtitleFile>().Count, Is.EqualTo(1));
            Assert.That(_recycled, Does.Not.Contain(Full("300 (2006) - SDR.en.forced.srt")));
            var count = _recycled.Count;
            RunCleanup(op);
            Assert.That(_recycled.Count, Is.EqualTo(count));
            Assert.That(Mocker.GetMock<IEventAggregator>().Invocations, Is.Empty);
        }

        [Test]
        public void copy_import_cleanup_should_retain_download_video_and_sidecars()
        {
            _copyOnly = true;
            var op = ReadyForCleanup();
            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(_disk.ContainsKey(op.Snapshot.IncomingExtras.Single().SourcePath), Is.True);
            Assert.That(_operations.Get(op.Id).Snapshot.Cleanup.Any(i => i.Kind == VersionImportCleanupKind.Source), Is.False);
        }

        [TestCase("extra")]
        [TestCase("video-backup")]
        [TestCase("sidecar-backup")]
        [TestCase("source")]
        public void cleanup_should_recover_after_recycling_but_before_checkpoint(string target)
        {
            var obsolete = ObsoleteSubtitle();
            var op = ReadyForCleanup();
            var path = target switch
            {
                "extra" => Full(obsolete.RelativePath),
                "video-backup" => Full(op.BackupRelativePath),
                "sidecar-backup" => Full(op.Snapshot.IncomingExtras.Single().BackupRelativePath),
                _ => _source
            };
            var failed = false;
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((p, s) =>
            {
                _disk.Remove(p);
                _recycled.Add(p);
                if (!failed && p == path)
                {
                    failed = true;
                    throw new IOException("Stopped after physical cleanup");
                }

                return "recycled/" + Path.GetFileName(p);
            });
            Assert.Throws<IOException>(() => RunCleanup(op));
            Assert.That(_operations.Get(op.Id).Snapshot.Cleanup.Single(i => i.State == VersionImportCleanupState.Recycling).Path, Is.EqualTo(target == "source" ? _source : Path.GetRelativePath(_movie.Path, path)));
            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(_recycled.Count(p => p == path), Is.EqualTo(1));
        }

        [Test]
        public void interrupted_recycling_with_existing_backup_requires_verified_retry()
        {
            var op = ReadyForCleanup();
            var backup = Full(op.BackupRelativePath);
            var failed = false;
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((p, s) =>
            {
                if (!failed && p == backup)
                {
                    failed = true;
                    throw new IOException("Stopped before removing backup");
                }

                _disk.Remove(p);
                return "recycled/" + Path.GetFileName(p);
            });
            Assert.Throws<IOException>(() => RunCleanup(op));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op));
            Assert.That(_disk.ContainsKey(backup), Is.True);
            RunCleanup(op, retry: true, note: "Inspected original backup and verified identity");
            AssertCleanupFinished(op);
        }

        [TestCase("video-backup")]
        [TestCase("sidecar-backup")]
        [TestCase("installed-video")]
        [TestCase("installed-sidecar")]
        public void changed_backup_or_replacement_should_block_cleanup(string target)
        {
            var op = ReadyForCleanup();
            var extra = op.Snapshot.IncomingExtras.Single();
            var path = Full(target switch
            {
                "video-backup" => op.BackupRelativePath,
                "sidecar-backup" => extra.BackupRelativePath,
                "installed-video" => op.DestinationRelativePath,
                _ => extra.DestinationRelativePath
            });
            _disk[path] = (_disk[path].Size, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op));
            Assert.That(_disk.ContainsKey(path), Is.True);
            Assert.That(_operations.Get(op.Id).IsActive, Is.True);
        }

        [Test]
        public void changed_obsolete_extra_can_be_explicitly_preserved_without_old_registration()
        {
            var obsolete = ObsoleteSubtitle();
            var op = ReadyForCleanup();
            _disk[Full(obsolete.RelativePath)] = (10, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op));
            RunCleanup(op, preserveExtras: true, note: "Retain edited subtitle");
            AssertCleanupFinished(op);
            Assert.That(_disk[Full(obsolete.RelativePath)].Hash, Is.EqualTo(new string('D', 64)));
            Assert.That(Db.All<SubtitleFile>().Any(f => f.Id == obsolete.Id && f.MovieFileId == _old.Id), Is.False);
            Assert.That(_operations.Get(op.Id).Snapshot.Cleanup.Single(i => i.ExtraId == obsolete.Id).Preserve, Is.True);
        }

        [Test]
        public void changed_download_source_should_require_explicit_preservation()
        {
            var op = ReadyForCleanup();
            _disk[_source] = (150, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op));
            RunCleanup(op, preserveSources: true, note: "Retain replaced download source");
            AssertCleanupFinished(op);
            Assert.That(_disk[_source].Hash, Is.EqualTo(new string('D', 64)));
        }

        [Test]
        public void cleanup_should_not_remove_obsolete_path_claimed_by_hdr()
        {
            var obsolete = ObsoleteSubtitle();
            var op = ReadyForCleanup();
            Db.Insert(new OtherExtraFile { MovieId = _movie.Id, MovieFileId = _other.Id, RelativePath = obsolete.RelativePath, Extension = ".srt" });
            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(_disk.ContainsKey(Full(obsolete.RelativePath)), Is.True);
            Assert.That(Db.Single<OtherExtraFile>().MovieFileId, Is.EqualTo(_other.Id));
        }

        [Test]
        public void cleanup_overrides_should_require_nonempty_verification_note()
        {
            var op = ReadyForCleanup();
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op, retry: true));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op, preserveExtras: true));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op, preserveSources: true));
            Assert.That(_recycled, Is.Empty);
        }

        [Test]
        public void failed_cleanup_commit_should_keep_old_registration_and_recover_missing_physical_file()
        {
            var database = Mocker.Resolve<IMainDatabase>();
            if (database.DatabaseType != DatabaseType.SQLite)
            {
                Assert.Ignore("SQLite fault injection trigger");
            }

            var obsolete = ObsoleteSubtitle();
            var op = ReadyForCleanup(false);
            using (var connection = database.OpenConnection())
            {
                connection.Execute($"CREATE TRIGGER FailCleanupCommit BEFORE UPDATE ON VersionFileImports WHEN (SELECT COUNT(*) FROM SubtitleFiles WHERE Id = {obsolete.Id} AND MovieFileId = {_old.Id}) = 0 BEGIN SELECT RAISE(ABORT, 'Simulated cleanup checkpoint failure'); END;");
            }

            Assert.Catch(() => RunCleanup(op));
            Assert.That(_disk.ContainsKey(Full(obsolete.RelativePath)), Is.False);
            Assert.That(Db.Single<SubtitleFile>().Id, Is.EqualTo(obsolete.Id));
            Assert.That(_operations.Get(op.Id).Snapshot.Cleanup.Single(i => i.ExtraId == obsolete.Id).State, Is.EqualTo(VersionImportCleanupState.Recycling));
            using (var connection = database.OpenConnection())
            {
                connection.Execute("DROP TRIGGER FailCleanupCommit;");
            }

            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(Db.All<SubtitleFile>(), Is.Empty);
            Assert.That(_recycled.Count(p => p == Full(obsolete.RelativePath)), Is.EqualTo(1));
        }

        [Test]
        public void source_reappearing_after_finished_checkpoint_should_not_be_recycled_again()
        {
            var op = ReadyForCleanup();
            RunCleanup(op);
            _disk.Add(_source, (150, new string('D', 64)));
            var count = _recycled.Count;
            RunCleanup(op);
            Assert.That(_disk[_source].Hash, Is.EqualTo(new string('D', 64)));
            Assert.That(_recycled.Count, Is.EqualTo(count));
        }

        [Test]
        public void duplicate_old_claims_should_recycle_physical_extra_once_and_clear_both_registrations()
        {
            var obsolete = ObsoleteSubtitle();
            Db.Insert(new OtherExtraFile { MovieId = _movie.Id, MovieFileId = _old.Id, RelativePath = obsolete.RelativePath, Extension = ".srt" });
            var op = ReadyForCleanup();
            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(_recycled.Count(p => p == Full(obsolete.RelativePath)), Is.EqualTo(1));
            Assert.That(Db.All<OtherExtraFile>(), Is.Empty);
            Assert.That(Db.All<SubtitleFile>().Count, Is.EqualTo(1));
        }

        [Test]
        public void old_registration_reassigned_to_hdr_should_be_preserved()
        {
            var obsolete = ObsoleteSubtitle();
            var op = ReadyForCleanup();
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE SubtitleFiles SET MovieFileId = @fileId WHERE Id = @id", new { fileId = _other.Id, id = obsolete.Id });
            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(Db.All<SubtitleFile>().Single(f => f.Id == obsolete.Id).MovieFileId, Is.EqualTo(_other.Id));
            Assert.That(_disk.ContainsKey(Full(obsolete.RelativePath)), Is.True);
        }

        [Test]
        public void snapshot_preservation_should_remain_effective_after_live_guard_changes()
        {
            var obsolete = ObsoleteSubtitle();
            Mocker.GetMock<IExtraFileDeletionGuard>().Setup(v => v.CanRecycle(It.IsAny<Movie>(), It.IsAny<int>(), It.IsAny<string>())).Returns(false);
            var op = ReadyForCleanup(false);
            Mocker.GetMock<IExtraFileDeletionGuard>().Setup(v => v.CanRecycle(It.IsAny<Movie>(), It.IsAny<int>(), It.IsAny<string>())).Returns(true);
            RunCleanup(op);
            AssertCleanupFinished(op);
            Assert.That(_disk.ContainsKey(Full(obsolete.RelativePath)), Is.True);
            Assert.That(Db.All<SubtitleFile>(), Is.Empty);
        }

        [Test]
        public void newly_appeared_extra_should_not_be_recycled_as_previously_missing_file()
        {
            var obsolete = ObsoleteSubtitle();
            _disk.Remove(Full(obsolete.RelativePath));
            var op = ReadyForCleanup();
            _disk.Add(Full(obsolete.RelativePath), (10, new string('E', 64)));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op));
            Assert.That(_disk.ContainsKey(Full(obsolete.RelativePath)), Is.True);
            RunCleanup(op, preserveExtras: true, note: "Retain subtitle that appeared after capture");
            AssertCleanupFinished(op);
        }

        [Test]
        public void changed_registered_version_should_block_cleanup_before_first_recycle()
        {
            var op = ReadyForCleanup();
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE MovieVersions SET MovieFileId = NULL WHERE Id = @Id", _sdr);
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op));
            Assert.That(_recycled, Is.Empty);
            Assert.That(_disk.ContainsKey(Full(op.BackupRelativePath)), Is.True);
        }

        [Test]
        public void cleanup_manifest_cannot_retire_installed_video_as_backup()
        {
            var op = ReadyForCleanup(false);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE VersionFileImports SET BackupRelativePath = @path WHERE Id = @Id", new { path = op.DestinationRelativePath, op.Id });
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op));
            Assert.That(_recycled, Is.Empty);
            Assert.That(_disk[Full(op.DestinationRelativePath)].Hash, Is.EqualTo(new string('A', 64)));
        }

        [Test]
        public void cleanup_phase_cannot_advance_with_unfinished_manifest_entries()
        {
            ObsoleteSubtitle();
            var op = ReadyForCleanup();
            _operations.BeginCleanup(op.Id);
            Assert.Throws<InvalidOperationException>(() => _operations.AdvanceCleanup(op.Id, VersionFileImportState.CleaningOldExtras, VersionFileImportState.RecyclingBackup));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.CleaningOldExtras));
        }

        [Test]
        public void retry_permission_should_never_override_changed_backup_identity()
        {
            var op = ReadyForCleanup();
            var backup = Full(op.BackupRelativePath);
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(backup, It.IsAny<string>())).Throws(new IOException("Stopped before recycling backup"));
            Assert.Throws<IOException>(() => RunCleanup(op));
            _disk[backup] = (100, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => RunCleanup(op, retry: true, note: "Retry requested after inspection"));
            Assert.That(_disk[backup].Hash, Is.EqualTo(new string('D', 64)));
        }
    }
}
