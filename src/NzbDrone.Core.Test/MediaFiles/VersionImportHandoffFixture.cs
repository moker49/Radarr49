using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class VersionImportHandoffFixture : DbTest<VersionImportHandoffService, VersionFileImport>
    {
        private Movie _movie;
        private MovieVersion _sdr;
        private MovieVersion _hdr;
        private MovieFile _old;
        private MovieFile _other;
        private VersionFileImportRepository _operations;
        private Dictionary<string, (long Size, string Hash)> _disk;
        private string _source;
        private bool _allowed;
        private DownloadReservation _reservation;

        [SetUp]
        public void Setup()
        {
            _movie = Db.Insert(new Movie { Path = @"C:\Movies\300 (2006)".AsOsAgnostic(), QualityProfileId = 10, Monitored = true });
            var sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", IsPrimary = true });
            var hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            _sdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = sdr.Id, Monitored = true });
            _hdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = hdr.Id, Monitored = true });
            var files = Mocker.Resolve<MediaFileRepository>();
            _old = files.InsertForVersion(File("300 (2006) - SDR.mkv"), _sdr.Id);
            _other = files.InsertForVersion(File("300 (2006) - HDR.mkv"), _hdr.Id);
            _operations = Mocker.Resolve<VersionFileImportRepository>();
            Mocker.SetConstant<IVersionFileImportRepository>(_operations);
            Mocker.SetConstant<IExtraFileRepository<SubtitleFile>>(Mocker.Resolve<SubtitleFileRepository>());
            Mocker.SetConstant<IExtraFileRepository<MetadataFile>>(Mocker.Resolve<MetadataFileRepository>());
            Mocker.SetConstant<IExtraFileRepository<OtherExtraFile>>(Mocker.Resolve<OtherExtraFileRepository>());
            Mocker.GetMock<IExtraFileDeletionGuard>().Setup(v => v.CanRecycle(It.IsAny<Movie>(), It.IsAny<int>(), It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(_movie.Id)).Returns(() => Root());
            Mocker.GetMock<IMovieService>().Setup(v => v.AllMoviePaths()).Returns(new Dictionary<int, string> { { _movie.Id, _movie.Path } });
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesByMovie(_movie.Id)).Returns(() => Db.All<MovieFile>());
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Get(It.IsAny<Movie>(), It.IsAny<int>())).Returns<Movie, int>((m, id) => Resolve(id));
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(v => v.ParseCustomFormat(It.IsAny<LocalMovie>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<IQualityDefinitionService>().Setup(v => v.Get(It.IsAny<Quality>())).Returns(new QualityDefinition());
            Mocker.GetMock<IRootFolderService>().Setup(v => v.GetBestRootFolderPath(_movie.Path, null)).Returns(@"C:\Movies".AsOsAgnostic());
            _allowed = true;
            _reservation = null;
            _source = @"C:\Downloads\300.2006.SDR.mkv".AsOsAgnostic();
            _disk = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase)
            {
                { _source, (150, new string('A', 64)) }, { Full(_old.RelativePath), (100, new string('B', 64)) }, { Full(_other.RelativePath), (100, new string('C', 64)) }
            };
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileExists(It.IsAny<string>())).Returns<string>(p => _disk.ContainsKey(Path.GetFullPath(p)));
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFileSize(It.IsAny<string>())).Returns<string>(p => _disk[Path.GetFullPath(p)].Size);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileGetLastWrite(It.IsAny<string>())).Returns(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IVersionImportFileVerifier>().Setup(v => v.Hash(It.IsAny<string>())).Returns<string>(p => _disk[Path.GetFullPath(p)].Hash);
            Mocker.GetMock<IDiskTransferService>().Setup(v => v.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false)).Returns<string, string, TransferMode, bool>((s, d, m, o) =>
            {
                _disk.Add(d, _disk[s]);
                return TransferMode.Copy;
            });
            Mocker.GetMock<IDiskProvider>().Setup(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Callback<string, string, bool>((s, d, o) => Move(s, d));
        }

        private string Full(string relative) => Path.GetFullPath(Path.Combine(_movie.Path, relative));
        private MovieFile File(string relative) => new MovieFile { MovieId = _movie.Id, RelativePath = relative, Size = 100, Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English } };
        private Movie Root()
        {
            var movie = Db.Single<Movie>();
            movie.MovieMetadata = new MovieMetadata { Title = "300", Year = 2006, Runtime = 100 };
            return movie;
        }

        private ResolvedMovieVersion Resolve(int id) => new ResolvedMovieVersion
        {
            Id = id, MovieId = _movie.Id, Label = id == _sdr.Id ? "SDR" : "HDR", IsPrimary = id == _sdr.Id,
            MovieFileId = Db.All<MovieVersion>().Single(v => v.Id == id).MovieFileId, Monitored = true,
            QualityProfile = new QualityProfile { Language = Language.Any, Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.Bluray1080p, Allowed = _allowed } } }
        };

        private VersionFileImport Prepare()
        {
            var root = Root().WithVersion(Resolve(_sdr.Id), Db.All<MovieFile>().SingleOrDefault(f => f.Id == _old.Id));
            var local = new LocalMovie
            {
                Movie = root, MovieVersionId = _sdr.Id, Path = _source, Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English },
                DownloadReservationId = _reservation?.Id,
                DownloadItem = _reservation == null ? null : new DownloadClientItem { DownloadId = _reservation.DownloadId, DownloadClientInfo = new DownloadClientItemClientInfo { Id = _reservation.DownloadClientId } }
            };
            return Subject.Prepare(local, File(null), false);
        }

        private void Move(string source, string destination)
        {
            _disk.Add(destination, _disk[source]);
            _disk.Remove(source);
        }

        private VersionFileImport BeforeBackup()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileImportState.Prepared, VersionFileImportState.Staging);
            _disk.Add(Full(op.StagingRelativePath), _disk[_source]);
            _operations.Advance(op.Id, VersionFileImportState.Staging, VersionFileImportState.Staged);
            _operations.Advance(op.Id, VersionFileImportState.Staged, VersionFileImportState.BackingUp);
            return op;
        }

        private void AssertRegistered(VersionFileImport op, MovieFile result)
        {
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Registered));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).MovieFileId, Is.EqualTo(result.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(result.Id));
            Assert.That(Db.All<MovieFile>().Any(f => f.Id == _old.Id), Is.False);
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _hdr.Id).MovieFileId, Is.EqualTo(_other.Id));
            Assert.That(_disk[Full(_other.RelativePath)].Hash, Is.EqualTo(new string('C', 64)));
            Assert.That(_disk[_source].Hash, Is.EqualTo(new string('A', 64)));
            Assert.That(_disk[Full(op.BackupRelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Assert.That(_disk[Full(result.RelativePath)].Hash, Is.EqualTo(new string('A', 64)));
            Assert.That(result.Quality.Quality, Is.EqualTo(Quality.Bluray1080p));
            Assert.That(result.Languages, Is.EqualTo(new[] { Language.English }));
            Assert.That(Db.All<MovieVersion>().All(v => v.Monitored), Is.True);
        }

        [Test]
        public void should_copy_verify_and_register_without_removing_source_or_backup()
        {
            var op = Prepare();
            var file = Subject.Resume(_movie.Id, op.Id);
            AssertRegistered(op, file);
            Assert.That(Subject.Resume(_movie.Id, op.Id).Id, Is.EqualTo(file.Id));
            Assert.That(Db.All<MovieFile>().Count, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_resume_after_backup_or_install_move_before_checkpoint(bool installed)
        {
            var op = BeforeBackup();
            Move(Full(_old.RelativePath), Full(op.BackupRelativePath));
            if (installed)
            {
                _operations.Advance(op.Id, VersionFileImportState.BackingUp, VersionFileImportState.Installing);
                Move(Full(op.StagingRelativePath), Full(op.DestinationRelativePath));
            }

            AssertRegistered(op, Subject.Resume(_movie.Id, op.Id));
        }

        [Test]
        public void partial_copy_should_keep_original_and_allow_cancellation()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileImportState.Prepared, VersionFileImportState.Staging);
            _disk.Add(Full(op.StagingRelativePath), (10, new string('D', 64)));
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Subject.CancelBeforeBackup(_movie.Id, op.Id, "Interrupted partial copy inspected");
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Cancelled));
            Assert.That(_disk.ContainsKey(Full(op.StagingRelativePath)), Is.True);
        }

        [Test]
        public void changed_source_should_block_before_any_copy()
        {
            var op = Prepare();
            _disk[_source] = (150, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Prepared));
            Assert.That(_disk.ContainsKey(Full(_old.RelativePath)), Is.True);
        }

        [Test]
        public void changed_profile_should_block_without_moving_old_file()
        {
            var op = Prepare();
            _allowed = false;
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(_disk.ContainsKey(Full(_old.RelativePath)), Is.True);
        }

        [Test]
        public void should_not_adopt_foreign_destination_after_backup()
        {
            var op = BeforeBackup();
            Move(Full(_old.RelativePath), Full(op.BackupRelativePath));
            _disk.Add(Full(op.DestinationRelativePath), (150, new string('D', 64)));
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(_disk[Full(op.BackupRelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Assert.Throws<InvalidOperationException>(() => Subject.CancelBeforeBackup(_movie.Id, op.Id, "Cannot cancel after backup"));
        }

        [Test]
        public void should_block_other_writes_to_target_until_finalization()
        {
            var op = Prepare();
            Subject.Resume(_movie.Id, op.Id);
            var guard = Mocker.Resolve<VersionFileDeletionRepository>();
            Assert.Throws<VersionFileDeletionPendingException>(() => guard.CheckReady(_movie.Id, _sdr.Id));
            Assert.DoesNotThrow(() => guard.CheckReady(_movie.Id, _hdr.Id));
            Assert.Throws<InvalidOperationException>(() => Prepare());
        }

        [Test]
        public void first_import_should_register_an_empty_version_without_backup()
        {
            Mocker.Resolve<MediaFileRepository>().DeleteForVersion(_old, false);
            _disk.Remove(Full(_old.RelativePath));
            var op = Prepare();
            var result = Subject.Resume(_movie.Id, op.Id);
            Assert.That(op.OldMovieFileId, Is.Null);
            Assert.That(op.BackupRelativePath, Is.Null);
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(result.Id));
            Assert.That(_disk.ContainsKey(_source), Is.True);
        }

        [Test]
        public void failed_assignment_commit_should_rollback_registration_and_allow_retry()
        {
            var database = Mocker.Resolve<IMainDatabase>();
            if (database.DatabaseType != DatabaseType.SQLite)
            {
                Assert.Ignore("SQLite fault injection trigger");
            }

            var op = Prepare();
            using (var connection = database.OpenConnection())
            {
                connection.Execute("CREATE TRIGGER FailVersionAssignment BEFORE UPDATE OF MovieFileId ON MovieVersions BEGIN SELECT RAISE(ABORT, 'Simulated registration failure'); END;");
            }

            Assert.Catch(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Installing));
            Assert.That(Db.All<MovieFile>().Count, Is.EqualTo(2));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).MovieFileId, Is.EqualTo(_old.Id));
            using (var connection = database.OpenConnection())
            {
                connection.Execute("DROP TRIGGER FailVersionAssignment;");
            }

            AssertRegistered(op, Subject.Resume(_movie.Id, op.Id));
        }

        [Test]
        public void changed_backup_should_block_registration_after_install_move()
        {
            var op = BeforeBackup();
            Move(Full(_old.RelativePath), Full(op.BackupRelativePath));
            _operations.Advance(op.Id, VersionFileImportState.BackingUp, VersionFileImportState.Installing);
            Move(Full(op.StagingRelativePath), Full(op.DestinationRelativePath));
            _disk[Full(op.BackupRelativePath)] = (100, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_old.Id));
        }

        [Test]
        public void should_commit_reservation_ownership_with_new_file()
        {
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "download-1", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            var op = Prepare();
            var result = Subject.Resume(_movie.Id, op.Id);
            AssertRegistered(op, result);
            var reservation = Db.Single<DownloadReservation>();
            Assert.That(reservation.State, Is.EqualTo(DownloadReservationState.Imported));
            Assert.That(reservation.ImportedMovieFileId, Is.EqualTo(result.Id));
            Assert.That(_operations.Get(op.Id).IsActive, Is.True);
        }

        [Test]
        public void changed_reservation_should_block_before_copy()
        {
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "download-1", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            var op = Prepare();
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE DownloadReservations SET DownloadId = 'different' WHERE Id = @Id", _reservation);
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Prepared));
            Assert.That(_disk.ContainsKey(Full(_old.RelativePath)), Is.True);
        }

        [Test]
        public void should_snapshot_sidecars_and_keep_old_registrations_during_pending_finalization()
        {
            var subtitle = Db.Insert(new SubtitleFile { MovieId = _movie.Id, MovieFileId = _old.Id, RelativePath = "300 (2006) - SDR.en.srt", Extension = ".srt", Language = Language.English, LanguageTags = new List<string>() });
            var metadata = Db.Insert(new MetadataFile { MovieId = _movie.Id, MovieFileId = _old.Id, RelativePath = "300 (2006) - SDR.nfo", Extension = ".nfo", Consumer = "XbmcMetadata" });
            var other = Db.Insert(new OtherExtraFile { MovieId = _movie.Id, MovieFileId = _old.Id, RelativePath = "300 (2006) - SDR.txt", Extension = ".txt" });
            foreach (var relative in new[] { subtitle.RelativePath, metadata.RelativePath, other.RelativePath })
            {
                _disk.Add(Full(relative), (10, new string('E', 64)));
            }

            var sourceSubtitle = Path.ChangeExtension(_source, ".en.srt");
            _disk.Add(sourceSubtitle, (20, new string('F', 64)));
            Mocker.GetMock<IConfigService>().SetupGet(v => v.ImportExtraFiles).Returns(true);
            Mocker.GetMock<IConfigService>().SetupGet(v => v.ExtraFileExtensions).Returns("srt");
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFiles(Path.GetDirectoryName(_source), false)).Returns(new[] { sourceSubtitle, _source });
            var op = Prepare();
            Subject.Resume(_movie.Id, op.Id);
            var snapshot = _operations.Get(op.Id).Snapshot;
            Assert.That(snapshot.PreviousExtras.Count, Is.EqualTo(3));
            Assert.That(snapshot.PreviousExtras.All(e => e.ContentHash == new string('E', 64) && e.LastWriteUtcTicks > 0 && !e.Preserve), Is.True);
            Assert.That(snapshot.IncomingExtras.Single().ContentHash, Is.EqualTo(new string('F', 64)));
            Assert.That(_disk.ContainsKey(sourceSubtitle), Is.True);
            Mocker.Resolve<CleanupOrphanedSubtitleFiles>().Clean();
            Mocker.Resolve<CleanupOrphanedMetadataFiles>().Clean();
            Mocker.Resolve<CleanupOrphanedExtraFiles>().Clean();
            Assert.That(Db.All<SubtitleFile>().Count, Is.EqualTo(1));
            Assert.That(Db.All<MetadataFile>().Count, Is.EqualTo(1));
            Assert.That(Db.All<OtherExtraFile>().Count, Is.EqualTo(1));
        }

        [TestCase("../outside.mkv")]
        [TestCase("ordinary.mkv")]
        public void should_reject_staging_paths_outside_private_area(string relative)
        {
            var op = Prepare();
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE VersionFileImports SET StagingRelativePath = @relative WHERE Id = @Id", new { relative, op.Id });
            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, op.Id));
            Assert.That(_disk.ContainsKey(Full(_old.RelativePath)), Is.True);
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Prepared));
        }

        [Test]
        public void pending_import_should_block_targeted_deletion_before_recycling()
        {
            Prepare();
            Assert.Throws<InvalidOperationException>(() => Mocker.Resolve<VersionFileDeletionRepository>().Begin(new VersionFileDeletion
            {
                MovieId = _movie.Id, MovieVersionId = _sdr.Id, MovieFileId = _old.Id, MoviePath = _movie.Path,
                Snapshot = new VersionFileDeletionSnapshot { File = _old }
            }));
            Assert.That(Db.All<VersionFileDeletion>(), Is.Empty);
            Assert.That(_disk.ContainsKey(Full(_old.RelativePath)), Is.True);
        }

        private VersionFileImport WithIncomingSidecar(bool previous = true, string suffix = ".en.forced.srt")
        {
            var source = Path.Combine(Path.GetDirectoryName(_source), Path.GetFileNameWithoutExtension(_source) + suffix);
            _disk.Add(source, (20, new string('F', 64)));
            if (previous)
            {
                var relative = "300 (2006) - SDR" + suffix;
                Db.Insert(new SubtitleFile { MovieId = _movie.Id, MovieFileId = _old.Id, RelativePath = relative, Extension = Path.GetExtension(suffix), Language = Language.English });
                _disk.Add(Full(relative), (10, new string('E', 64)));
            }

            Mocker.GetMock<IConfigService>().SetupGet(v => v.ImportExtraFiles).Returns(true);
            Mocker.GetMock<IConfigService>().SetupGet(v => v.ExtraFileExtensions).Returns("srt,txt,nfo");
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFiles(Path.GetDirectoryName(_source), false)).Returns(new[] { source, _source });
            var op = Prepare();
            Subject.Resume(_movie.Id, op.Id);
            return op;
        }

        private void ResumeSidecars(VersionFileImport op) => Mocker.Resolve<VersionImportSidecarService>().Resume(_movie.Id, op.Id);

        private void AssertSidecarInstalled(VersionFileImport op, bool previous = true)
        {
            var row = _operations.Get(op.Id);
            var extra = row.Snapshot.IncomingExtras.Single();
            Assert.That(row.State, Is.EqualTo(VersionFileImportState.CleaningOldExtras));
            Assert.That(extra.State, Is.EqualTo(VersionImportExtraState.Registered));
            Assert.That(_disk[Full(extra.DestinationRelativePath)].Hash, Is.EqualTo(new string('F', 64)));
            Assert.That(_disk[extra.SourcePath].Hash, Is.EqualTo(new string('F', 64)));
            if (previous)
            {
                Assert.That(_disk[Full(extra.BackupRelativePath)].Hash, Is.EqualTo(new string('E', 64)));
            }

            var file = Db.Single<SubtitleFile>();
            Assert.That(file.Id, Is.EqualTo(extra.RegisteredExtraId));
            Assert.That(file.MovieFileId, Is.EqualTo(row.NewMovieFileId));
            Assert.That(file.RelativePath, Is.EqualTo("300 (2006) - SDR.en.forced.srt"));
            Assert.That(file.Language, Is.EqualTo(Language.English));
            Assert.That(file.LanguageTags, Does.Contain("forced"));
            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(_disk.ContainsKey(Full(op.BackupRelativePath)), Is.True);
            Assert.That(_disk[Full(_other.RelativePath)].Hash, Is.EqualTo(new string('C', 64)));
            Assert.That(row.IsActive, Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_install_and_register_sidecar_while_retaining_sources_and_backups(bool previous)
        {
            var op = WithIncomingSidecar(previous);
            ResumeSidecars(op);
            AssertSidecarInstalled(op, previous);
            ResumeSidecars(op);
            AssertSidecarInstalled(op, previous);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_recover_sidecar_move_completed_before_checkpoint(bool backupMove)
        {
            var op = WithIncomingSidecar();
            var failed = false;
            Mocker.GetMock<IDiskProvider>().Setup(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Callback<string, string, bool>((s, d, o) =>
            {
                Move(s, d);
                if (!failed && (backupMove ? d.Contains(".previous", StringComparison.Ordinal) : d.EndsWith("SDR.en.forced.srt", StringComparison.Ordinal)))
                {
                    failed = true;
                    throw new IOException("Simulated stop after sidecar move");
                }
            });
            Assert.Throws<IOException>(() => ResumeSidecars(op));
            var extra = _operations.Get(op.Id).Snapshot.IncomingExtras.Single();
            Assert.That(extra.State, Is.EqualTo(backupMove ? VersionImportExtraState.BackingUp : VersionImportExtraState.Installing));
            ResumeSidecars(op);
            AssertSidecarInstalled(op);
        }

        [Test]
        public void incomplete_sidecar_copy_should_keep_previous_physical_file_and_registration()
        {
            var op = WithIncomingSidecar();
            Mocker.GetMock<IDiskTransferService>().Setup(v => v.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false)).Returns<string, string, TransferMode, bool>((s, d, m, o) =>
            {
                _disk.Add(d, (5, new string('D', 64)));
                return TransferMode.Copy;
            });
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(_operations.Get(op.Id).Snapshot.IncomingExtras.Single().State, Is.EqualTo(VersionImportExtraState.Copying));
            Assert.That(_disk[Full("300 (2006) - SDR.en.forced.srt")].Hash, Is.EqualTo(new string('E', 64)));
            Assert.That(Db.Single<SubtitleFile>().MovieFileId, Is.EqualTo(_old.Id));
        }

        [TestCase("source")]
        [TestCase("old")]
        [TestCase("video")]
        public void changed_files_should_stop_before_sidecar_replacement(string changed)
        {
            var op = WithIncomingSidecar();
            var path = changed == "source" ? op.Snapshot.IncomingExtras.Single().SourcePath : Full(changed == "old" ? "300 (2006) - SDR.en.forced.srt" : op.DestinationRelativePath);
            _disk[path] = (_disk[path].Size, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(Db.Single<SubtitleFile>().MovieFileId, Is.EqualTo(_old.Id));
        }

        [Test]
        public void unregistered_sidecar_destination_should_never_be_overwritten()
        {
            var op = WithIncomingSidecar(false);
            _disk.Add(Full("300 (2006) - SDR.en.forced.srt"), (20, new string('F', 64)));
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(Db.All<SubtitleFile>(), Is.Empty);
        }

        [Test]
        public void sidecar_claimed_by_hdr_should_block_sdr_replacement()
        {
            var op = WithIncomingSidecar();
            Db.Insert(new OtherExtraFile { MovieId = _movie.Id, MovieFileId = _other.Id, RelativePath = "300 (2006) - SDR.en.forced.srt", Extension = ".srt" });
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(_disk[Full("300 (2006) - SDR.en.forced.srt")].Hash, Is.EqualTo(new string('E', 64)));
        }

        [Test]
        public void should_install_non_subtitle_as_other_extra_preserving_its_suffix()
        {
            var op = WithIncomingSidecar(false, ".notes.nfo");
            ResumeSidecars(op);
            var file = Db.Single<OtherExtraFile>();
            Assert.That(file.RelativePath, Is.EqualTo("300 (2006) - SDR.notes.nfo"));
            Assert.That(file.MovieFileId, Is.EqualTo(_operations.Get(op.Id).NewMovieFileId));
            Assert.That(Db.All<SubtitleFile>(), Is.Empty);
        }

        [Test]
        public void no_incoming_sidecars_should_advance_without_removing_old_extras()
        {
            var op = Prepare();
            Subject.Resume(_movie.Id, op.Id);
            ResumeSidecars(op);
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.CleaningOldExtras));
            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(_disk.ContainsKey(Full(op.BackupRelativePath)), Is.True);
        }

        [Test]
        public void failed_sidecar_registration_should_rollback_old_claim_removal_and_allow_retry()
        {
            var database = Mocker.Resolve<IMainDatabase>();
            if (database.DatabaseType != DatabaseType.SQLite)
            {
                Assert.Ignore("SQLite fault injection trigger");
            }

            var op = WithIncomingSidecar();
            var oldId = Db.Single<SubtitleFile>().Id;
            using (var connection = database.OpenConnection())
            {
                connection.Execute("CREATE TRIGGER FailSidecarInsert BEFORE INSERT ON SubtitleFiles BEGIN SELECT RAISE(ABORT, 'Simulated sidecar registration failure'); END;");
            }

            Assert.Catch(() => ResumeSidecars(op));
            var extra = _operations.Get(op.Id).Snapshot.IncomingExtras.Single();
            Assert.That(extra.State, Is.EqualTo(VersionImportExtraState.Installing));
            Assert.That(extra.RegisteredExtraId, Is.Null);
            Assert.That(Db.Single<SubtitleFile>().Id, Is.EqualTo(oldId));
            Assert.That(Db.Single<SubtitleFile>().MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(_disk[Full(extra.DestinationRelativePath)].Hash, Is.EqualTo(new string('F', 64)));
            Assert.That(_disk[Full(extra.BackupRelativePath)].Hash, Is.EqualTo(new string('E', 64)));
            using (var connection = database.OpenConnection())
            {
                connection.Execute("DROP TRIGGER FailSidecarInsert;");
            }

            ResumeSidecars(op);
            AssertSidecarInstalled(op);
        }

        [Test]
        public void changed_sidecar_backup_should_stop_before_registration()
        {
            var op = WithIncomingSidecar();
            Mocker.GetMock<IDiskProvider>().Setup(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Callback<string, string, bool>((s, d, o) =>
            {
                Move(s, d);
                if (d.EndsWith("SDR.en.forced.srt", StringComparison.Ordinal))
                {
                    throw new IOException("Stop after install move");
                }
            });
            Assert.Throws<IOException>(() => ResumeSidecars(op));
            var extra = _operations.Get(op.Id).Snapshot.IncomingExtras.Single();
            _disk[Full(extra.BackupRelativePath)] = (10, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(Db.Single<SubtitleFile>().MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(_operations.Get(op.Id).Snapshot.IncomingExtras.Single().State, Is.EqualTo(VersionImportExtraState.Installing));
        }

        [Test]
        public void shared_old_sidecar_should_stay_protected_even_if_guard_later_allows_it()
        {
            Mocker.GetMock<IExtraFileDeletionGuard>().Setup(v => v.CanRecycle(It.IsAny<Movie>(), It.IsAny<int>(), It.IsAny<string>())).Returns(false);
            var op = WithIncomingSidecar();
            Mocker.GetMock<IExtraFileDeletionGuard>().Setup(v => v.CanRecycle(It.IsAny<Movie>(), It.IsAny<int>(), It.IsAny<string>())).Returns(true);
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(_disk[Full("300 (2006) - SDR.en.forced.srt")].Hash, Is.EqualTo(new string('E', 64)));
            Assert.That(Db.Single<SubtitleFile>().MovieFileId, Is.EqualTo(_old.Id));
        }

        [Test]
        public void sidecar_plan_should_not_change_with_live_import_settings()
        {
            var op = WithIncomingSidecar();
            Mocker.GetMock<IConfigService>().SetupGet(v => v.ImportExtraFiles).Returns(false);
            Mocker.GetMock<IConfigService>().SetupGet(v => v.ExtraFileExtensions).Returns(string.Empty);
            ResumeSidecars(op);
            AssertSidecarInstalled(op);
        }

        [Test]
        public void changed_installed_sidecar_should_block_replay()
        {
            var op = WithIncomingSidecar();
            ResumeSidecars(op);
            var extra = _operations.Get(op.Id).Snapshot.IncomingExtras.Single();
            _disk[Full(extra.DestinationRelativePath)] = (20, new string('D', 64));
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(_operations.Get(op.Id).IsActive, Is.True);
        }

        [Test]
        public void changed_registered_sidecar_owner_should_block_replay()
        {
            var op = WithIncomingSidecar();
            ResumeSidecars(op);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE SubtitleFiles SET MovieFileId = @Id", _other);
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(_operations.Get(op.Id).IsActive, Is.True);
        }

        [Test]
        public void should_revalidate_imported_reservation_before_modifying_sidecars()
        {
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "download-1", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            var op = WithIncomingSidecar();
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE DownloadReservations SET DownloadId = 'different' WHERE Id = @Id", _reservation);
            Assert.Throws<InvalidOperationException>(() => ResumeSidecars(op));
            Assert.That(_disk[Full("300 (2006) - SDR.en.forced.srt")].Hash, Is.EqualTo(new string('E', 64)));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Registered));
        }

        [Test]
        public void should_not_finish_sidecars_before_all_checkpoints_register()
        {
            var op = WithIncomingSidecar();
            _operations.BeginExtras(op.Id);
            Assert.Throws<InvalidOperationException>(() => _operations.FinishExtras(op.Id));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileImportState.Finalizing));
        }
    }
}
