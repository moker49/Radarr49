using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FizzWare.NBuilder;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadReservationRepositoryFixture : DbTest<DownloadReservationRepository, DownloadReservation>
    {
        private Movie _movie;
        private QualityProfile _profile;
        private MovieVersion _sdr;
        private MovieVersion _hdr;

        [SetUp]
        public void Setup()
        {
            _profile = new QualityProfile { Name = "Composite", Items = new List<QualityProfileQualityItem>(), MaximumConcurrentDownloads = 1 };
            var child = new QualityProfile { Name = "Child", Items = new List<QualityProfileQualityItem>() };
            Db.Insert(_profile);
            Db.Insert(child);
            _movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = _profile.Id).With(m => m.MovieFileId = 0).Build();
            Db.Insert(_movie);
            var sdr = new CompositeProfileVersion { CompositeProfileId = _profile.Id, QualityProfileId = child.Id, Label = "SDR", Priority = 0, IsPrimary = true };
            var hdr = new CompositeProfileVersion { CompositeProfileId = _profile.Id, QualityProfileId = child.Id, Label = "HDR", Priority = 1 };
            Db.Insert(sdr);
            Db.Insert(hdr);
            _sdr = new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = sdr.Id, Monitored = true };
            _hdr = new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = hdr.Id, Monitored = true };
            Db.Insert(_sdr);
            Db.Insert(_hdr);
        }

        private DownloadReservation Intent(MovieVersion version, ReleaseInfo release = null)
        {
            release ??= new ReleaseInfo { Guid = "release-" + version.Id, IndexerId = 1, Title = "Release " + version.Id, Size = version.Id * 100 };
            return new DownloadReservation
            {
                MovieId = _movie.Id, MovieVersionId = version.Id, DownloadClientId = 7,
                SourceTitle = release.Title, ReleaseKeys = DownloadReleaseIdentity.GetKeys(release)
            };
        }

        private DownloadReservation Reserve(MovieVersion version)
        {
            return Subject.TryReserve(Intent(version), new List<ActiveVersionDownload>());
        }

        private void AllowTwo()
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE \"QualityProfiles\" SET \"MaximumConcurrentDownloads\" = 2 WHERE \"Id\" = @Id", _profile);
        }

        [Test]
        public void should_persist_intent_before_dispatch()
        {
            var row = Reserve(_sdr);

            Assert.That(Subject.Get(row.Id).State, Is.EqualTo(DownloadReservationState.Prepared));
            Assert.That(Subject.Get(row.Id).ReleaseKeys, Is.EqualTo(row.ReleaseKeys));
            Assert.That(Subject.Get(row.Id).DownloadId, Is.Null);
        }

        [Test]
        public void same_version_cannot_reserve_twice()
        {
            AllowTwo();
            Reserve(_sdr);

            Assert.That(Reserve(_sdr), Is.Null);
        }

        [Test]
        public void unfinished_deletion_should_block_only_its_versions_reservation()
        {
            Db.Insert(new VersionFileDeletion
            {
                MovieId = _movie.Id, MovieVersionId = _sdr.Id, MovieFileId = 99, MoviePath = _movie.Path,
                State = VersionFileDeletionState.Detached, Snapshot = new VersionFileDeletionSnapshot { File = new MovieFile { Id = 99 } }
            });

            Assert.That(Reserve(_sdr), Is.Null);
            Assert.That(Reserve(_hdr), Is.Not.Null);
        }

        [Test]
        public void default_limit_should_block_other_version()
        {
            Reserve(_sdr);

            Assert.That(Reserve(_hdr), Is.Null);
        }

        [Test]
        public void configured_limit_should_allow_both_versions()
        {
            AllowTwo();
            Reserve(_sdr);

            Assert.That(Reserve(_hdr), Is.Not.Null);
        }

        [Test]
        public void same_torrent_cannot_be_claimed_by_other_version()
        {
            AllowTwo();
            Subject.TryReserve(Intent(_sdr, new TorrentInfo { InfoHash = "ABC", Title = "SDR", IndexerId = 1 }), new List<ActiveVersionDownload>());

            Assert.That(Subject.TryReserve(Intent(_hdr, new TorrentInfo { InfoHash = "abc", Title = "HDR", IndexerId = 2 }), new List<ActiveVersionDownload>()), Is.Null);
        }

        [Test]
        public void unknown_version_in_existing_queue_should_block_new_dispatch()
        {
            AllowTwo();
            var queue = new List<ActiveVersionDownload> { new ActiveVersionDownload { DownloadClientId = 7, DownloadId = "unknown" } };

            Assert.That(Subject.TryReserve(Intent(_sdr), queue), Is.Null);
        }

        [Test]
        public void submitted_download_visible_in_queue_should_count_once()
        {
            AllowTwo();
            var sdr = Reserve(_sdr);
            Subject.BeginDispatch(sdr.Id);
            Subject.BindDownload(sdr.Id, "job-1", null);
            var queue = new List<ActiveVersionDownload>
            {
                new ActiveVersionDownload { MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "JOB-1" }
            };

            Assert.That(Subject.TryReserve(Intent(_hdr), queue), Is.Not.Null);
        }

        [Test]
        public void failure_before_dispatch_should_release_capacity()
        {
            var row = Reserve(_sdr);
            Subject.RecordFailure(row.Id, false, "Preparation failed");

            Assert.That(Subject.Get(row.Id).State, Is.EqualTo(DownloadReservationState.Failed));
            Assert.That(Reserve(_hdr), Is.Not.Null);
        }

        [Test]
        public void ambiguous_dispatch_failure_should_remain_blocked_after_restart()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.RecordFailure(row.Id, false, "Timeout after request");
            var restarted = new DownloadReservationRepository(Mocker.Resolve<IMainDatabase>(), Mocker.GetMock<IEventAggregator>().Object);

            Assert.That(restarted.Get(row.Id).State, Is.EqualTo(DownloadReservationState.Uncertain));
            Assert.That(restarted.TryReserve(Intent(_hdr), new List<ActiveVersionDownload>()), Is.Null);
        }

        [Test]
        public void definitive_rejection_should_release_capacity()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.RecordFailure(row.Id, true, "Client explicitly rejected release");

            Assert.That(Reserve(_hdr), Is.Not.Null);
        }

        [Test]
        public void submitted_identity_should_survive_missing_history_and_later_failures()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.BindDownload(row.Id, "job-1", null);
            Subject.RecordFailure(row.Id, false, "History write failed");

            var restored = Subject.FindForDownload(7, "JOB-1").Single();
            Assert.That(restored.MovieVersionId, Is.EqualTo(_sdr.Id));
            Assert.That(restored.State, Is.EqualTo(DownloadReservationState.Submitted));
            Assert.That(Subject.FindForDownload(8, "JOB-1"), Is.Empty);
        }

        [Test]
        public void accepted_submission_without_client_id_should_require_reconciliation()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.BindDownload(row.Id, null, null);

            Assert.That(Subject.Get(row.Id).State, Is.EqualTo(DownloadReservationState.Uncertain));
            Assert.That(Reserve(_hdr), Is.Null);
        }

        [Test]
        public void explicit_confirmation_can_release_unbound_uncertainty()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.RecordFailure(row.Id, false, "Timeout");
            Subject.ConfirmNotSubmitted(row.Id, "Verified client has no such job");

            Assert.That(Reserve(_hdr), Is.Not.Null);
        }

        [Test]
        public void known_submission_cannot_be_confirmed_not_submitted()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.BindDownload(row.Id, "job-1", null);

            Assert.Throws<InvalidOperationException>(() => Subject.ConfirmNotSubmitted(row.Id, "Wrong assumption"));
            Assert.That(Subject.Get(row.Id).State, Is.EqualTo(DownloadReservationState.Submitted));
        }

        [Test]
        public void same_client_download_id_cannot_be_bound_to_both_versions()
        {
            AllowTwo();
            var a = Reserve(_sdr);
            var b = Reserve(_hdr);
            Subject.BeginDispatch(a.Id);
            Subject.BeginDispatch(b.Id);
            Subject.BindDownload(a.Id, "job-1", null);

            Assert.Throws<InvalidOperationException>(() => Subject.BindDownload(b.Id, "JOB-1", null));
            Assert.That(Subject.Get(b.Id).State, Is.EqualTo(DownloadReservationState.Dispatching));
        }

        [Test]
        public void imported_file_should_keep_release_ownership_without_consuming_download_slot()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.BindDownload(row.Id, "job-1", null);
            var file = Builder<MovieFile>.CreateNew().With(f => f.Id = 0).With(f => f.MovieId = _movie.Id)
                .With(f => f.Quality = new QualityModel()).With(f => f.Languages = new List<Language>()).Build();
            Db.Insert(file);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE \"MovieVersions\" SET \"MovieFileId\" = @fileId WHERE \"Id\" = @versionId", new { fileId = file.Id, versionId = _sdr.Id });
            Subject.MarkImported(row.Id, _sdr.Id, file.Id);

            var duplicate = Intent(_hdr);
            duplicate.ReleaseKeys = row.ReleaseKeys;
            Assert.That(Subject.TryReserve(duplicate, new List<ActiveVersionDownload>()), Is.Null);
            Assert.That(Reserve(_hdr), Is.Not.Null);
            Subject.MarkFailed(row.Id, "Late failure event");
            Assert.That(Subject.Get(row.Id).State, Is.EqualTo(DownloadReservationState.Imported));
        }

        [Test]
        public void wrong_import_file_should_not_release_reservation()
        {
            var row = Reserve(_sdr);
            Subject.BeginDispatch(row.Id);
            Subject.BindDownload(row.Id, "job-1", null);

            Assert.Throws<InvalidOperationException>(() => Subject.MarkImported(row.Id, _sdr.Id, 999));
            Assert.That(Subject.Get(row.Id).State, Is.EqualTo(DownloadReservationState.Submitted));
        }

        [Test]
        public async Task concurrent_searches_must_not_overbook_one_download_slot()
        {
            var repository = Subject;
            var first = Intent(_sdr);
            var second = Intent(_hdr);

            var results = await Task.WhenAll(
                Task.Run(() => repository.TryReserve(first, new List<ActiveVersionDownload>())),
                Task.Run(() => repository.TryReserve(second, new List<ActiveVersionDownload>())));

            Assert.That(results.Count(r => r != null), Is.EqualTo(1));
            Assert.That(Subject.ForMovie(_movie.Id).Count(r => r.IsActive), Is.EqualTo(1));
        }
    }
}
