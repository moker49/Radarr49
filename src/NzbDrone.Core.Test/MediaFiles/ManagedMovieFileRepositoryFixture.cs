using System;
using System.Collections.Generic;
using Dapper;
using FizzWare.NBuilder;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class ManagedMovieFileRepositoryFixture : DbTest<MediaFileRepository, MovieFile>
    {
        private Movie _movie;
        private MovieVersion _sdr;
        private MovieVersion _hdr;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = 10).With(m => m.MovieFileId = 0).Build();
            Db.Insert(_movie);
            var sdr = new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true };
            var hdr = new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 };
            Db.Insert(sdr);
            Db.Insert(hdr);
            _sdr = new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = sdr.Id, Monitored = true };
            _hdr = new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = hdr.Id, Monitored = true };
            Db.Insert(_sdr);
            Db.Insert(_hdr);
        }

        private MovieFile File(string path)
        {
            return new MovieFile { MovieId = _movie.Id, RelativePath = path, Quality = new QualityModel(), Languages = new List<Language>() };
        }

        private int PrimaryFileId()
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            return connection.ExecuteScalar<int>("SELECT \"MovieFileId\" FROM \"Movies\" WHERE \"Id\" = @Id", _movie);
        }

        private MovieVersion Version(int id)
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            return connection.QuerySingle<MovieVersion>("SELECT * FROM \"MovieVersions\" WHERE \"Id\" = @id", new { id });
        }

        [Test]
        public void adding_secondary_should_preserve_primary_summary()
        {
            var sdr = Subject.InsertForVersion(File("300 (2006) - SDR.mkv"), _sdr.Id);
            var hdr = Subject.InsertForVersion(File("300 (2006) - HDR.mkv"), _hdr.Id);

            Assert.That(PrimaryFileId(), Is.EqualTo(sdr.Id));
            Assert.That(Version(_sdr.Id).MovieFileId, Is.EqualTo(sdr.Id));
            Assert.That(Version(_hdr.Id).MovieFileId, Is.EqualTo(hdr.Id));
        }

        [Test]
        public void replacing_primary_should_leave_secondary_owned_and_monitored()
        {
            var old = Subject.InsertForVersion(File("old-sdr.mkv"), _sdr.Id);
            var hdr = Subject.InsertForVersion(File("hdr.mkv"), _hdr.Id);
            Subject.DeleteForVersion(old, false);
            var replacement = Subject.InsertForVersion(File("new-sdr.mkv"), _sdr.Id);

            Assert.That(PrimaryFileId(), Is.EqualTo(replacement.Id));
            Assert.That(Version(_hdr.Id).MovieFileId, Is.EqualTo(hdr.Id));
            Assert.That(Version(_sdr.Id).Monitored, Is.True);
            Assert.That(Subject.Get(hdr.Id).RelativePath, Is.EqualTo("hdr.mkv"));
        }

        [Test]
        public void deleting_secondary_should_only_unmonitor_secondary()
        {
            var sdr = Subject.InsertForVersion(File("sdr.mkv"), _sdr.Id);
            var hdr = Subject.InsertForVersion(File("hdr.mkv"), _hdr.Id);
            Assert.That(Subject.DeleteForVersion(hdr, true), Is.EqualTo(_hdr.Id));

            Assert.That(PrimaryFileId(), Is.EqualTo(sdr.Id));
            Assert.That(Version(_hdr.Id).MovieFileId, Is.Null);
            Assert.That(Version(_hdr.Id).Monitored, Is.False);
            Assert.That(Version(_sdr.Id).Monitored, Is.True);
        }

        [Test]
        public void path_collision_should_leave_target_empty_and_other_file_untouched()
        {
            var sdr = Subject.InsertForVersion(File("SDR.mkv"), _sdr.Id);
            var collision = File("sdr.MKV");
            Assert.Throws<InvalidOperationException>(() => Subject.InsertForVersion(collision, _hdr.Id));

            Assert.That(collision.Id, Is.Zero);
            Assert.That(Version(_hdr.Id).MovieFileId, Is.Null);
            Assert.That(PrimaryFileId(), Is.EqualTo(sdr.Id));
            Assert.That(Subject.GetFilesByMovie(_movie.Id).Count, Is.EqualTo(1));
        }

        [Test]
        public void occupied_version_should_not_be_overwritten()
        {
            var existing = Subject.InsertForVersion(File("sdr.mkv"), _sdr.Id);
            Assert.Throws<InvalidOperationException>(() => Subject.InsertForVersion(File("replacement.mkv"), _sdr.Id));
            Assert.That(Version(_sdr.Id).MovieFileId, Is.EqualTo(existing.Id));
        }

        [Test]
        public void foreign_movie_version_should_be_rejected()
        {
            var file = File("foreign.mkv");
            file.MovieId = _movie.Id + 100;
            Assert.Throws<InvalidOperationException>(() => Subject.InsertForVersion(file, _hdr.Id));
            Assert.That(Version(_hdr.Id).MovieFileId, Is.Null);
            Assert.That(Subject.GetFilesByMovie(_movie.Id), Is.Empty);
        }

        [Test]
        public void explicit_delete_should_preserve_other_version_file_monitoring_and_root_monitoring()
        {
            var sdr = Subject.InsertForVersion(File("SDR.mkv"), _sdr.Id);
            var hdr = Subject.InsertForVersion(File("HDR.mkv"), _hdr.Id);

            Subject.DeleteManagedVersionFile(hdr, _hdr.Id, true);

            Assert.That(Version(_hdr.Id).MovieFileId, Is.Null);
            Assert.That(Version(_hdr.Id).Monitored, Is.False);
            Assert.That(Version(_sdr.Id).MovieFileId, Is.EqualTo(sdr.Id));
            Assert.That(Version(_sdr.Id).Monitored, Is.True);
            Assert.That(PrimaryFileId(), Is.EqualTo(sdr.Id));
            Assert.That(Db.Single<Movie>().Monitored, Is.EqualTo(_movie.Monitored));
        }

        [Test]
        public void explicit_delete_should_reject_stale_or_wrong_version_without_detaching_any_file()
        {
            var sdr = Subject.InsertForVersion(File("SDR.mkv"), _sdr.Id);
            var hdr = Subject.InsertForVersion(File("HDR.mkv"), _hdr.Id);

            Assert.Throws<InvalidOperationException>(() => Subject.DeleteManagedVersionFile(hdr, _sdr.Id, true));

            Assert.That(Version(_hdr.Id).MovieFileId, Is.EqualTo(hdr.Id));
            Assert.That(Version(_sdr.Id).MovieFileId, Is.EqualTo(sdr.Id));
            Assert.That(Subject.GetFilesByMovie(_movie.Id).Count, Is.EqualTo(2));
        }

        [Test]
        public void explicit_delete_should_recheck_active_reservation_in_transaction()
        {
            var sdr = Subject.InsertForVersion(File("SDR.mkv"), _sdr.Id);
            Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 2, DownloadId = "job", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });

            Assert.Throws<InvalidOperationException>(() => Subject.DeleteManagedVersionFile(sdr, _sdr.Id, true));

            Assert.That(Version(_sdr.Id).MovieFileId, Is.EqualTo(sdr.Id));
            Assert.That(Version(_sdr.Id).Monitored, Is.True);
            Assert.That(PrimaryFileId(), Is.EqualTo(sdr.Id));
        }

        [Test]
        public void deletion_should_expire_only_target_imported_release_ownership_and_keep_reservation_audit()
        {
            var sdr = Subject.InsertForVersion(File("SDR.mkv"), _sdr.Id);
            var hdr = Subject.InsertForVersion(File("HDR.mkv"), _hdr.Id);
            Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 2, DownloadId = "sdr", SourceTitle = "300 SDR", ImportedMovieFileId = sdr.Id, State = DownloadReservationState.Imported });
            Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _hdr.Id, DownloadClientId = 2, DownloadId = "hdr", SourceTitle = "300 HDR", ImportedMovieFileId = hdr.Id, State = DownloadReservationState.Imported });
            var reservations = Mocker.Resolve<DownloadReservationRepository>();

            Subject.DeleteManagedVersionFile(hdr, _hdr.Id, true);

            Assert.That(reservations.FindForDownload(2, "hdr"), Is.Empty);
            Assert.That(reservations.FindForDownload(2, "sdr").Count, Is.EqualTo(1));
            Assert.That(Db.All<DownloadReservation>().Count, Is.EqualTo(2));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void explicit_primary_delete_should_clear_summary_without_promoting_secondary(bool unmonitor)
        {
            var sdr = Subject.InsertForVersion(File("SDR.mkv"), _sdr.Id);
            var hdr = Subject.InsertForVersion(File("HDR.mkv"), _hdr.Id);

            Subject.DeleteManagedVersionFile(sdr, _sdr.Id, unmonitor);

            Assert.That(PrimaryFileId(), Is.Zero);
            Assert.That(Version(_sdr.Id).MovieFileId, Is.Null);
            Assert.That(Version(_sdr.Id).Monitored, Is.EqualTo(!unmonitor));
            Assert.That(Version(_hdr.Id).MovieFileId, Is.EqualTo(hdr.Id));
            Assert.That(Version(_hdr.Id).Monitored, Is.True);
        }
    }
}
