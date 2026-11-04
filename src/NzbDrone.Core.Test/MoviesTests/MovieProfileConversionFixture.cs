using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests
{
    [TestFixture]
    public class MovieProfileConversionFixture : DbTest<MovieProfileConversionRepository, Movie>
    {
        private QualityProfile _sdr;
        private QualityProfile _hdr;
        private QualityProfile _other;
        private QualityProfile _composite;
        private Movie _movie;
        private MovieFile _sdrFile;
        private MovieFile _hdrFile;
        private List<CompositeProfileVersion> _definitions;

        [SetUp]
        public void Setup()
        {
            _sdr = Db.Insert(Profile("SDR"));
            _hdr = Db.Insert(Profile("HDR"));
            _other = Db.Insert(Profile("Other"));
            _composite = Profile("SDR+HDR");
            _composite.CompositeTestRoot = Path.GetFullPath("C:/ConversionMovies");
            Db.Insert(_composite);
            var metadata = Db.Insert(new MovieMetadata { TmdbId = 101, Title = "Movie", CleanTitle = "movie", SortTitle = "movie", Year = 2000 });
            _movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = _composite.Id)
                .With(m => m.MovieMetadataId = metadata.Id)
                .With(m => m.MovieFileId = 0).With(m => m.Path = Path.Combine(_composite.CompositeTestRoot, "Movie")).Build();
            Db.Insert(_movie);
            _sdrFile = Db.Insert(File("Movie - SDR.mkv"));
            _hdrFile = Db.Insert(File("Movie - HDR.mkv"));
            _definitions = new List<CompositeProfileVersion>
            {
                Db.Insert(new CompositeProfileVersion { CompositeProfileId = _composite.Id, QualityProfileId = _sdr.Id, Label = "SDR", Priority = 0, IsPrimary = true }),
                Db.Insert(new CompositeProfileVersion { CompositeProfileId = _composite.Id, QualityProfileId = _hdr.Id, Label = "HDR", Priority = 1 })
            };
            Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = _definitions[0].Id, MovieFileId = _sdrFile.Id, Monitored = true });
            Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = _definitions[1].Id, MovieFileId = _hdrFile.Id, Monitored = true });
            _movie.MovieFileId = _sdrFile.Id;
            Db.Update(_movie);
            Mocker.GetMock<IMovieRepository>().Setup(r => r.Get(It.IsAny<int>())).Returns<int>(id => Db.All<Movie>().Single(m => m.Id == id));
        }

        private static QualityProfile Profile(string name)
        {
            return new QualityProfile { Name = name, Items = new List<QualityProfileQualityItem>() };
        }

        private MovieFile File(string path)
        {
            return new MovieFile { MovieId = _movie.Id, RelativePath = path, Size = 10, Quality = new QualityModel(), Languages = new List<Language>(), DateAdded = DateTime.UtcNow };
        }

        private Movie Change(int profileId, int? retain = null, int? assign = null)
        {
            var movie = Db.All<Movie>().Single(m => m.Id == _movie.Id);
            movie.QualityProfileId = profileId;
            return Subject.UpdateProfiles(new List<Movie> { movie }, retain, assign).Single();
        }

        [Test]
        public void converting_should_clear_obsolete_pending_candidates_but_metadata_edits_should_not()
        {
            var version = Db.All<MovieVersion>().First();
            Db.Insert(new PendingRelease
            {
                MovieId = _movie.Id, MovieVersionId = version.Id, Title = "Candidate", Added = DateTime.UtcNow,
                ParsedMovieInfo = new ParsedMovieInfo { MovieTitles = new List<string> { "Movie" } },
                Release = new ReleaseInfo { Title = "Candidate", PublishDate = DateTime.UtcNow }, Reason = PendingReleaseReason.VersionDownloadLimit
            });

            Change(_composite.Id);
            Assert.That(Db.All<PendingRelease>().Count, Is.EqualTo(1));
            Change(_sdr.Id);
            Assert.That(Db.All<PendingRelease>(), Is.Empty);
        }

        [Test]
        public void exact_child_match_should_preserve_both_files_and_mark_only_the_other_unmanaged()
        {
            var converted = Change(_sdr.Id);

            Assert.That(converted.MovieFileId, Is.EqualTo(_sdrFile.Id));
            Assert.That(Db.All<MovieFile>().Single(f => f.Id == _hdrFile.Id).IsUnmanaged, Is.True);
            Assert.That(Db.All<MovieFile>().Single(f => f.Id == _sdrFile.Id).IsUnmanaged, Is.False);
            Assert.That(Db.All<MovieFile>().Select(f => f.RelativePath), Is.EquivalentTo(new[] { "Movie - SDR.mkv", "Movie - HDR.mkv" }));
            Assert.That(Db.All<MovieVersion>().All(v => v.Retired && v.MovieFileId == null), Is.True);
            Assert.That(Mocker.Resolve<MovieVersionRepository>().ForMovie(_movie.Id), Is.Empty);
        }

        [Test]
        public void ambiguous_api_conversion_should_reject_without_any_changes()
        {
            Assert.That(() => Change(_other.Id), Throws.TypeOf<NzbDrone.Core.Exceptions.NzbDroneClientException>());

            Assert.That(Db.All<Movie>().Single().QualityProfileId, Is.EqualTo(_composite.Id));
            Assert.That(Db.All<MovieFile>().All(f => !f.IsUnmanaged), Is.True);
            Assert.That(Db.All<MovieVersion>().All(v => !v.Retired && v.MovieFileId.HasValue), Is.True);
        }

        [Test]
        public void explicit_ui_choice_should_keep_the_selected_version_for_an_unrelated_simple_profile()
        {
            var movie = Change(_other.Id, _hdr.Id);

            Assert.That(movie.MovieFileId, Is.EqualTo(_hdrFile.Id));
            Assert.That(Db.All<MovieFile>().Single(f => f.Id == _sdrFile.Id).IsUnmanaged, Is.True);
        }

        [Test]
        public void explicit_conversion_back_should_restore_matching_copies_and_original_version_ids()
        {
            var ids = Db.All<MovieVersion>().Select(v => v.Id).ToArray();
            Change(_sdr.Id);

            var restored = Change(_composite.Id);

            Assert.That(restored.MovieFileId, Is.EqualTo(_sdrFile.Id));
            Assert.That(Db.All<MovieFile>().All(f => !f.IsUnmanaged), Is.True);
            Assert.That(Db.All<MovieVersion>().Where(v => !v.Retired).Select(v => v.Id), Is.EquivalentTo(ids));
        }

        [Test]
        public void simple_to_composite_without_a_matching_child_should_require_an_explicit_choice()
        {
            Change(_other.Id, _sdr.Id);
            Assert.That(() => Change(_composite.Id), Throws.TypeOf<NzbDrone.Core.Exceptions.NzbDroneClientException>());
            Assert.That(Db.All<Movie>().Single().QualityProfileId, Is.EqualTo(_other.Id));

            Assert.That(Change(_composite.Id, assign: _sdr.Id).MovieFileId, Is.EqualTo(_sdrFile.Id));
        }

        [Test]
        public void completed_import_history_should_remain_bound_to_retired_versions()
        {
            var version = Db.All<MovieVersion>().First();
            var reservation = new DownloadReservation { MovieId = _movie.Id, MovieVersionId = version.Id, DownloadClientId = 1, SourceTitle = "Imported", State = DownloadReservationState.Imported, ImportedMovieFileId = _sdrFile.Id, ReleaseKeys = new List<string> { "test" }, Created = DateTime.UtcNow, Updated = DateTime.UtcNow };
            Db.Insert(reservation);

            Change(_sdr.Id);

            Assert.That(Db.All<DownloadReservation>().Single().MovieVersionId, Is.EqualTo(version.Id));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == version.Id).Retired, Is.True);
        }

        [Test]
        public void unmanaged_files_should_remain_excluded_from_rescan_import_candidates()
        {
            Change(_sdr.Id);
            var files = Db.All<MovieFile>();
            var paths = files.Select(f => Path.Combine(_movie.Path, f.RelativePath)).ToList();

            Assert.That(MediaFileService.FilterExistingFiles(paths, files, _movie), Is.Empty);
        }

        [Test]
        public void multi_movie_conversion_should_roll_back_when_any_movie_is_ambiguous()
        {
            var metadata = Db.Insert(new MovieMetadata { TmdbId = 102, Title = "Second", CleanTitle = "second", SortTitle = "second", Year = 2000 });
            var second = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.MovieMetadataId = metadata.Id).With(m => m.QualityProfileId = _composite.Id).With(m => m.MovieFileId = 0).With(m => m.Path = Path.Combine(_composite.CompositeTestRoot, "Second")).Build();
            Db.Insert(second);
            foreach (var definition in _definitions)
            {
                Db.Insert(new MovieVersion { MovieId = second.Id, CompositeProfileVersionId = definition.Id, Monitored = true });
            }

            var first = Db.All<Movie>().Single(m => m.Id == _movie.Id);
            first.QualityProfileId = _sdr.Id;
            second.QualityProfileId = _other.Id;

            Assert.That(() => Subject.UpdateProfiles(new List<Movie> { first, second }), Throws.TypeOf<NzbDrone.Core.Exceptions.NzbDroneClientException>());
            Assert.That(Db.All<Movie>().All(m => m.QualityProfileId == _composite.Id), Is.True);
            Assert.That(Db.All<MovieFile>().All(f => !f.IsUnmanaged), Is.True);
        }

        [Test]
        public void active_download_should_block_conversion()
        {
            var version = Db.All<MovieVersion>().First();
            Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = version.Id, DownloadClientId = 1, SourceTitle = "Active", State = DownloadReservationState.Submitted, ReleaseKeys = new List<string> { "test" }, Created = DateTime.UtcNow, Updated = DateTime.UtcNow });

            Assert.That(() => Change(_sdr.Id), Throws.TypeOf<NzbDrone.Core.Exceptions.NzbDroneClientException>());
            Assert.That(Db.All<Movie>().Single().QualityProfileId, Is.EqualTo(_composite.Id));
        }
    }
}
