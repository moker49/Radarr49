using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        private void CorruptPublication()
        {
            Mocker.GetMock<IVersionImportFileVerifier>().Setup(v => v.CopyAndHash(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((source, stage) =>
            {
                _disk.Add(stage, (_disk[source].Size, new string('D', 64)));
                return _disk[source].Hash;
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cancelling_abandoned_handoff_must_preserve_files_and_refuse_a_published_destination(bool destinationExists)
        {
            Db.Update(_sdr);
            Db.Delete(_old);
            _disk.Remove(Full(_old.RelativePath));
            var local = RuntimeLocal();
            local.Movie = Root().WithVersion(Resolve(_sdr.Id), null);
            var operation = Subject.Prepare(local, File(null), false);
            _operations.Advance(operation.Id, VersionFileImportState.Prepared, VersionFileImportState.Staging);
            _operations.Advance(operation.Id, VersionFileImportState.Staging, VersionFileImportState.Staged);
            _operations.Advance(operation.Id, VersionFileImportState.Staged, VersionFileImportState.BackingUp);
            if (destinationExists)
            {
                _disk.Add(Full(operation.DestinationRelativePath), (100, new string('E', 64)));
                Assert.Throws<System.InvalidOperationException>(() => Subject.CancelBeforeBackup(_movie.Id, operation.Id, "Abandoned download"));
                Assert.That(_operations.Get(operation.Id).State, Is.EqualTo(VersionFileImportState.BackingUp));
                Assert.That(_disk.ContainsKey(Full(operation.DestinationRelativePath)), Is.True);
            }
            else
            {
                Subject.CancelBeforeBackup(_movie.Id, operation.Id, "Abandoned download");
                Assert.That(_operations.Get(operation.Id).State, Is.EqualTo(VersionFileImportState.Cancelled));
            }

            Assert.That(_disk.ContainsKey(_source), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void first_version_publication_must_complete_or_quarantine_without_a_previous_backup(bool corrupt)
        {
            Db.Update(_sdr);
            Db.Delete(_old);
            _disk.Remove(Full(_old.RelativePath));
            var root = Root();
            root.MovieFileId = 0;
            Db.Update(root);
            var local = RuntimeLocal();
            local.Movie = Root().WithVersion(Resolve(_sdr.Id), null);
            if (corrupt)
            {
                CorruptPublication();
            }

            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(local) }, true).Single();
            var operation = _operations.All().Single();
            Assert.That(operation.OldMovieFileId, Is.Null);
            if (corrupt)
            {
                Assert.That(result.Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(_operations.Get(operation.Id).Snapshot.RollbackCompleted, Is.True);
                Assert.That(_disk.ContainsKey(Full(operation.DestinationRelativePath)), Is.False);
                Assert.That(_disk.ContainsKey(_source), Is.True);
                Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).MovieFileId, Is.Null);
            }
            else
            {
                Assert.That(result.Result, Is.EqualTo(ImportResultType.Imported));
                AssertCompletedImport(operation);
            }
        }

        [Test]
        public void published_checksum_failure_must_quarantine_and_restore_without_consuming_source()
        {
            CorruptPublication();
            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(RuntimeLocal()) }, true).Single();

            Assert.That(result.Result, Is.EqualTo(ImportResultType.Skipped));
            var operation = _operations.All().Single();
            Assert.That(operation.Snapshot.RollbackCompleted, Is.True);
            Assert.That(operation.NewMovieFileId, Is.Null);
            Assert.That(_disk[_source].Hash, Is.EqualTo(new string('A', 64)));
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Assert.That(_disk[Full(Path.Combine(Path.GetDirectoryName(operation.StagingRelativePath), "failed.mkv.radarr-failed"))].Hash, Is.EqualTo(new string('D', 64)));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(_operations.Pending().Select(v => v.Id), Does.Contain(operation.Id));
            Assert.That(_recycled, Is.Empty);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Never());

            Recovery().Cancel(_movie.Id, operation.Id, VersionImportRecoveryCheckpoint.For(operation), "Reviewed failed publication");
            Assert.That(_operations.Get(operation.Id).State, Is.EqualTo(VersionFileImportState.Cancelled));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void recovery_after_publication_must_verify_before_completing_or_backtracking(bool corrupt)
        {
            if (corrupt)
            {
                CorruptPublication();
            }

            var interrupted = false;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Callback<string, string, bool>((source, destination, _) =>
            {
                Move(source, destination);
                if (!interrupted && Path.GetFileName(source) == "incoming.mkv")
                {
                    interrupted = true;
                    throw new IOException("Simulated restart after publication");
                }
            });
            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(RuntimeLocal()) }, true).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Skipped));
            var operation = _operations.All().Single();
            Assert.That(operation.State, Is.EqualTo(VersionFileImportState.Installing));
            Assert.That(operation.NewMovieFileId, Is.Null);
            Assert.That(_disk.ContainsKey(_source), Is.True);
            if (corrupt)
            {
                Assert.Throws<NzbDroneClientException>(() => Recovery().Resume(_movie.Id, operation.Id));
                Assert.That(_operations.Get(operation.Id).Snapshot.RollbackCompleted, Is.True);
                Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
                Assert.That(_disk.ContainsKey(_source), Is.True);
            }
            else
            {
                Recovery().Resume(_movie.Id, operation.Id);
                AssertCompletedImport(operation);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void interrupted_rollback_must_resume_before_or_after_backup_restoration(bool renameSucceeded)
        {
            CorruptPublication();
            var failed = false;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Callback<string, string, bool>((source, destination, _) =>
            {
                if (!failed && Path.GetFileName(source) == "previous.mkv")
                {
                    failed = true;
                    if (renameSucceeded)
                    {
                        Move(source, destination);
                    }

                    throw new IOException("Simulated interruption restoring backup");
                }

                Move(source, destination);
            });
            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(RuntimeLocal()) }, true).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Skipped));
            var operation = _operations.All().Single();
            Assert.That(operation.Snapshot.RollbackPending, Is.True);
            Assert.That(operation.Snapshot.RollbackCompleted, Is.False);

            Assert.Throws<NzbDroneClientException>(() => Recovery().Resume(_movie.Id, operation.Id));
            Assert.That(_operations.Get(operation.Id).Snapshot.RollbackCompleted, Is.True);
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(_recycled, Is.Empty);
        }
    }
}
