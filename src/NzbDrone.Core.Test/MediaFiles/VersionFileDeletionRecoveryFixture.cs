using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class VersionFileDeletionRecoveryFixture : DbTest<VersionFileDeletionRecoveryService, VersionFileDeletion>
    {
        private readonly DateTime _modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        private Movie _movie;
        private MovieVersion _sdr;
        private MovieVersion _hdr;
        private MovieFile _sdrFile;
        private MovieFile _hdrFile;
        private VersionFileDeletionRepository _operations;
        private MediaFileRepository _files;
        private SubtitleFile _subtitle;
        private Dictionary<string, long> _diskFiles;

        [SetUp]
        public void Setup()
        {
            _movie = Db.Insert(new Movie { Path = @"C:\Movies\300 (2006)".AsOsAgnostic(), QualityProfileId = 10, Monitored = true });
            var sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            var hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            _sdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = sdr.Id, Monitored = true });
            _hdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = hdr.Id, Monitored = true });
            _files = Mocker.Resolve<MediaFileRepository>();
            Mocker.SetConstant<IMediaFileRepository>(_files);
            _sdrFile = _files.InsertForVersion(File("300 (2006) - SDR.mkv"), _sdr.Id);
            _hdrFile = _files.InsertForVersion(File("300 (2006) - HDR.mkv"), _hdr.Id);
            _subtitle = Db.Insert(new SubtitleFile { MovieId = _movie.Id, MovieFileId = _sdrFile.Id, RelativePath = "300 (2006) - SDR.en.srt", Extension = ".srt", Language = Language.English, LanguageTags = new List<string>() });
            Db.Insert(new SubtitleFile { MovieId = _movie.Id, MovieFileId = _hdrFile.Id, RelativePath = "300 (2006) - HDR.en.srt", Extension = ".srt", Language = Language.English, LanguageTags = new List<string>() });
            Db.Insert(new MetadataFile { MovieId = _movie.Id, RelativePath = "movie.nfo", Extension = ".nfo", Consumer = "XbmcMetadata" });
            _operations = Mocker.Resolve<VersionFileDeletionRepository>();
            Mocker.SetConstant<IVersionFileDeletionRepository>(_operations);
            Mocker.SetConstant<IExtraFileRepository<SubtitleFile>>(Mocker.Resolve<SubtitleFileRepository>());
            Mocker.SetConstant<IExtraFileRepository<MetadataFile>>(Mocker.Resolve<MetadataFileRepository>());
            Mocker.SetConstant<IExtraFileRepository<OtherExtraFile>>(Mocker.Resolve<OtherExtraFileRepository>());
            Mocker.SetConstant<IExtraFileDeletionGuard>(Mocker.Resolve<ExtraFileDeletionGuard>());
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(_movie.Id)).Returns(() => Db.Single<Movie>());
            Mocker.GetMock<IMovieService>().Setup(v => v.AllMoviePaths()).Returns(new Dictionary<int, string> { { _movie.Id, _movie.Path } });
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesByMovie(_movie.Id)).Returns(() => Db.All<MovieFile>());
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Get(It.IsAny<Movie>(), It.IsAny<int>())).Returns<Movie, int>((movie, id) => Resolve(id));
            _diskFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                { Full(_sdrFile.RelativePath), 100 }, { Full(_hdrFile.RelativePath), 200 },
                { Full(_subtitle.RelativePath), 10 }, { Full("300 (2006) - HDR.en.srt"), 20 }, { Full("movie.nfo"), 30 }
            };
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileExists(It.IsAny<string>())).Returns<string>(p => _diskFiles.ContainsKey(Path.GetFullPath(p)));
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFileSize(It.IsAny<string>())).Returns<string>(p => _diskFiles[Path.GetFullPath(p)]);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileGetLastWrite(It.IsAny<string>())).Returns(_modified);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetDirectories(It.IsAny<string>())).Returns(new[] { _movie.Path });
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((path, subfolder) =>
            {
                _diskFiles.Remove(Path.GetFullPath(path));
                return "recycled/" + Path.GetFileName(path);
            });
        }

        private string Full(string relative) => Path.GetFullPath(Path.Combine(_movie.Path, relative));
        private MovieFile File(string relative) => new MovieFile { MovieId = _movie.Id, RelativePath = relative, Size = 100, Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English } };
        private ResolvedMovieVersion Resolve(int id)
        {
            var version = Db.All<MovieVersion>().Single(v => v.Id == id);
            return new ResolvedMovieVersion { Id = id, MovieId = _movie.Id, MovieFileId = version.MovieFileId, IsPrimary = id == _sdr.Id, Monitored = version.Monitored, QualityProfile = new QualityProfile() };
        }

        private VersionFileDeletion Prepare()
        {
            return _operations.Begin(new VersionFileDeletion
            {
                MovieId = _movie.Id, MovieVersionId = _sdr.Id, MovieFileId = _sdrFile.Id, MoviePath = _movie.Path, Unmonitor = true,
                Snapshot = new VersionFileDeletionSnapshot
                {
                    File = _sdrFile, SourceExisted = true, SourceSize = 100, SourceLastWrite = _modified, SourceLastWriteUtcTicks = _modified.Ticks,
                    Extras = new List<VersionFileDeletionExtra>
                    {
                        new VersionFileDeletionExtra { Id = _subtitle.Id, Table = "SubtitleFiles", RelativePath = _subtitle.RelativePath, SourceExisted = true, Size = 10, LastWrite = _modified, LastWriteUtcTicks = _modified.Ticks }
                    }
                }
            });
        }

        private VersionFileDeletion DetachAfterCrash()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);
            _diskFiles.Remove(Full(_sdrFile.RelativePath));
            _operations.Detach(op.Id);
            return _operations.Get(op.Id);
        }

        private void AssertCompleted(VersionFileDeletion op)
        {
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Completed));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).MovieFileId, Is.Null);
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).Monitored, Is.False);
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _hdr.Id).MovieFileId, Is.EqualTo(_hdrFile.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.Zero);
            Assert.That(Db.Single<Movie>().Monitored, Is.True);
            Assert.That(_diskFiles.ContainsKey(Full(_hdrFile.RelativePath)), Is.True);
            Assert.That(_diskFiles.ContainsKey(Full("300 (2006) - HDR.en.srt")), Is.True);
            Assert.That(_diskFiles.ContainsKey(Full("movie.nfo")), Is.True);
        }

        [Test]
        public void normal_delete_should_persist_snapshot_and_finish_owned_extras_before_events()
        {
            _movie.ManagedVersion = Resolve(_sdr.Id);
            Subject.Start(_movie, _sdrFile, true);

            var op = AllStoredModels.Single();
            AssertCompleted(op);
            Assert.That(op.Snapshot.File.Quality.Quality, Is.EqualTo(Quality.Bluray1080p));
            Assert.That(op.Snapshot.File.Languages, Is.EqualTo(new[] { Language.English }));
            Assert.That(op.Snapshot.File.Movie, Is.Null);
            Assert.That(op.Snapshot.SourceLastWriteUtcTicks, Is.EqualTo(_modified.Ticks));
            Assert.That(Db.All<SubtitleFile>().Count, Is.EqualTo(1));
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.Is<MovieFileDeletedEvent>(e => e.DeletionOperationId == op.Id && e.MovieFile.MovieVersionId == _sdr.Id)), Times.Once());
        }

        [Test]
        public void restart_after_recycling_but_before_detach_should_not_recycle_main_file_again()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);
            _diskFiles.Remove(Full(_sdrFile.RelativePath));

            Subject.Resume(_movie.Id, op.Id, false, false, null);

            AssertCompleted(op);
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(Full(_sdrFile.RelativePath), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void ambiguous_recycle_with_existing_source_requires_verified_retry()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);

            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id, op.Id, false, false, null));
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Subject.Resume(_movie.Id, op.Id, true, false, "Verified original source on disk");
            AssertCompleted(op);
            Assert.That(_operations.Get(op.Id).Note, Does.Contain("Verified original"));
        }

        [Test]
        public void changed_source_before_recycling_can_be_cancelled_without_file_changes()
        {
            var op = Prepare();
            _diskFiles[Full(_sdrFile.RelativePath)] = 101;

            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id, op.Id, false, false, null));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Prepared));
            Subject.Cancel(_movie.Id, op.Id, "Source changed; inspect it first");

            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Cancelled));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_sdrFile.Id));
            Assert.That(_diskFiles[Full(_sdrFile.RelativePath)], Is.EqualTo(101));
        }

        [Test]
        public void orphan_housekeeping_should_keep_extras_until_recovery_finishes()
        {
            var op = DetachAfterCrash();
            Mocker.Resolve<CleanupOrphanedSubtitleFiles>().Clean();
            Assert.That(Db.All<SubtitleFile>().Count, Is.EqualTo(2));

            Subject.Resume(_movie.Id, op.Id, false, false, null);

            AssertCompleted(op);
            Assert.That(Db.All<SubtitleFile>().Count, Is.EqualTo(1));
        }

        [Test]
        public void crash_after_extra_recycle_should_finish_registration_without_recycling_again()
        {
            var op = DetachAfterCrash();
            _operations.Advance(op.Id, VersionFileDeletionState.Detached, VersionFileDeletionState.RecyclingExtra);
            _diskFiles.Remove(Full(_subtitle.RelativePath));

            Subject.Resume(_movie.Id, op.Id, false, false, null);

            AssertCompleted(op);
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void changed_extra_can_be_preserved_explicitly_without_recycling_it()
        {
            var op = DetachAfterCrash();
            _diskFiles[Full(_subtitle.RelativePath)] = 99;
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id, op.Id, false, false, null));

            Subject.Resume(_movie.Id, op.Id, false, true, "Preserve edited subtitle");

            AssertCompleted(op);
            Assert.That(_diskFiles[Full(_subtitle.RelativePath)], Is.EqualTo(99));
            Assert.That(_operations.Get(op.Id).Note, Does.Contain("Preserve edited subtitle"));
        }

        [Test]
        public void new_cross_category_claim_should_preserve_a_sidecar_path_during_recovery()
        {
            var op = DetachAfterCrash();
            Db.Insert(new MetadataFile { MovieId = _movie.Id, MovieFileId = _hdrFile.Id, RelativePath = _subtitle.RelativePath, Extension = ".srt", Consumer = "changed-provider" });

            Subject.Resume(_movie.Id, op.Id, false, false, null);

            AssertCompleted(op);
            Assert.That(_diskFiles.ContainsKey(Full(_subtitle.RelativePath)), Is.True);
            Assert.That(Db.All<MetadataFile>().Count, Is.EqualTo(2));
        }

        [Test]
        public void completed_operation_replay_should_not_touch_a_new_file_at_the_old_path()
        {
            var op = DetachAfterCrash();
            Subject.Resume(_movie.Id, op.Id, false, false, null);
            _diskFiles[Full(_sdrFile.RelativePath)] = 999;

            Subject.Resume(_movie.Id, op.Id, false, false, null);

            Assert.That(_diskFiles[Full(_sdrFile.RelativePath)], Is.EqualTo(999));
        }

        [Test]
        public void pending_operation_should_block_registration_and_direct_deletion_until_recovered()
        {
            var op = Prepare();
            Assert.Throws<InvalidOperationException>(() => _files.DeleteManagedVersionFile(_sdrFile, _sdr.Id, true));
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);
            _diskFiles.Remove(Full(_sdrFile.RelativePath));
            _operations.Detach(op.Id);

            Assert.Throws<InvalidOperationException>(() => _files.InsertForVersion(File("replacement.mkv"), _sdr.Id));
            Assert.Throws<VersionFileDeletionPendingException>(() => _operations.CheckReady(_movie.Id, null));
            Assert.DoesNotThrow(() => _operations.CheckReady(_movie.Id, _hdr.Id));
        }

        [Test]
        public void startup_should_resume_persisted_prepared_deletion()
        {
            var op = Prepare();

            Subject.HandleAsync(new ApplicationStartedEvent());

            AssertCompleted(op);
        }

        [Test]
        public void startup_should_leave_ambiguous_existing_source_untouched()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);

            Subject.HandleAsync(new ApplicationStartedEvent());

            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Recycling));
            Assert.That(_diskFiles.ContainsKey(Full(_sdrFile.RelativePath)), Is.True);
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void verified_retry_should_still_refuse_a_changed_source()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);
            _diskFiles[Full(_sdrFile.RelativePath)] = 999;

            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id, op.Id, true, false, "Inspected source"));

            Assert.That(_diskFiles[Full(_sdrFile.RelativePath)], Is.EqualTo(999));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_sdrFile.Id));
        }

        [Test]
        public void missing_source_under_unavailable_root_should_not_be_assumed_deleted()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);
            _diskFiles.Remove(Full(_sdrFile.RelativePath));
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns(false);

            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id, op.Id, false, false, null));

            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Recycling));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_sdrFile.Id));
        }

        [Test]
        public void operation_must_belong_to_route_movie()
        {
            var op = Prepare();
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id + 1, op.Id, false, false, null));
            Assert.Throws<NzbDroneClientException>(() => Subject.Cancel(_movie.Id + 1, op.Id, "Verified"));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Prepared));
        }

        [Test]
        public void recycling_started_deletion_cannot_be_cancelled_or_retried_without_note()
        {
            var op = Prepare();
            _operations.Advance(op.Id, VersionFileDeletionState.Prepared, VersionFileDeletionState.Recycling);

            Assert.Throws<NzbDroneClientException>(() => Subject.Cancel(_movie.Id, op.Id, "Cancel"));
            Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id, op.Id, true, false, null));
            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Recycling));
        }

        [Test]
        public async Task concurrent_prepare_should_create_only_one_active_operation()
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                try
                {
                    Prepare();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })));

            Assert.That(attempts.Count(x => x), Is.EqualTo(1));
            Assert.That(AllStoredModels.Count, Is.EqualTo(1));
        }

        [Test]
        public void cancelled_unstarted_operation_should_allow_a_new_exact_file_request()
        {
            var op = Prepare();
            Subject.Cancel(_movie.Id, op.Id, "Cancel before recycling");

            var next = Prepare();

            Assert.That(next.Id, Is.Not.EqualTo(op.Id));
            Assert.That(_operations.FindRequest(_movie.Id, _sdr.Id, _sdrFile.Id).Id, Is.EqualTo(next.Id));
        }

        [Test]
        public void checkpoint_cannot_skip_pending_extras()
        {
            var op = DetachAfterCrash();

            Assert.Throws<InvalidOperationException>(() => _operations.Advance(op.Id, VersionFileDeletionState.Detached, VersionFileDeletionState.EventsPending));

            Assert.That(_operations.Get(op.Id).State, Is.EqualTo(VersionFileDeletionState.Detached));
            Assert.That(_operations.Get(op.Id).ExtraIndex, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void recycle_failure_should_leave_a_recoverable_checkpoint_for_either_physical_outcome(bool removedBeforeFailure)
        {
            _movie.ManagedVersion = Resolve(_sdr.Id);
            var path = Full(_sdrFile.RelativePath);
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(path, It.IsAny<string>())).Returns<string, string>((source, subfolder) =>
            {
                if (removedBeforeFailure)
                {
                    _diskFiles.Remove(source);
                }

                throw new IOException("Interrupted recycler");
            });

            Assert.Throws<IOException>(() => Subject.Start(_movie, _sdrFile, true));

            var op = AllStoredModels.Single();
            Assert.That(op.State, Is.EqualTo(VersionFileDeletionState.Recycling));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_sdrFile.Id));
            if (!removedBeforeFailure)
            {
                Assert.Throws<NzbDroneClientException>(() => Subject.Resume(_movie.Id, op.Id, false, false, null));
            }

            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(path, It.IsAny<string>())).Returns<string, string>((source, subfolder) =>
            {
                _diskFiles.Remove(source);
                return "recycled/source";
            });
            Subject.Resume(_movie.Id, op.Id, !removedBeforeFailure, false, removedBeforeFailure ? null : "Verified original source after recycler failure");

            AssertCompleted(op);
        }
    }
}
