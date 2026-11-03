using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.Qualities;

namespace NzbDrone.Core.Test.MovieTests.MovieRepositoryTests
{
    [TestFixture]
    public class ManagedWantedRepositoryFixture : DbTest<MovieRepository, Movie>
    {
        private Movie _movie;
        private MovieVersion _sdr;
        private MovieVersion _hdr;
        private QualityProfile _parent;
        private QualityProfile _sdrProfile;
        private QualityProfile _hdrProfile;
        private int _metadataId;

        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());
            _metadataId = 100;
            _parent = Profile("Composite");
            _sdrProfile = Profile("SDR");
            _hdrProfile = Profile("HDR");
            _movie = NewMovie(_parent.Id);
            var sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = _parent.Id, QualityProfileId = _sdrProfile.Id, Label = "SDR", Priority = 0, IsPrimary = true });
            var hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = _parent.Id, QualityProfileId = _hdrProfile.Id, Label = "HDR", Priority = 1 });
            _sdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = sdr.Id, Monitored = true });
            _hdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = hdr.Id, Monitored = true });
        }

        private QualityProfile Profile(string name) => Db.Insert(new QualityProfile { Name = name, Items = QualityFixture.GetDefaultQualities(), Language = Language.Any });

        private Movie NewMovie(int profile)
        {
            var metadata = Builder<MovieMetadata>.CreateNew().With(m => m.Id = 0).With(m => m.TmdbId = ++_metadataId).With(m => m.Year = 2006).Build();
            Db.Insert(metadata);
            var movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.MovieMetadataId = metadata.Id).With(m => m.QualityProfileId = profile).With(m => m.Monitored = true).With(m => m.MovieFileId = 0).Build();
            return Db.Insert(movie);
        }

        private MovieFile FileFor(MovieVersion version, Quality quality)
        {
            var file = new MovieFile { MovieId = _movie.Id, RelativePath = version.Id + ".mkv", Quality = new QualityModel(quality), Languages = new List<Language>() };
            return Mocker.Resolve<MediaFileRepository>().InsertForVersion(file, version.Id);
        }

        private PagingSpec<Movie> Page(int number = 1, int size = 20) => new PagingSpec<Movie> { Page = number, PageSize = size, SortKey = "Movies.Id", SortDirection = SortDirection.Ascending };

        private List<QualitiesBelowCutoff> Cutoffs() => new List<QualitiesBelowCutoff>
        {
            new QualitiesBelowCutoff(_sdrProfile.Id, new[] { Quality.DVD.Id }),
            new QualitiesBelowCutoff(_hdrProfile.Id, new[] { Quality.Bluray1080p.Id })
        };

        [TestCase(true)]
        [TestCase(false)]
        public void either_missing_version_should_include_movie_regardless_of_primary_summary(bool primaryPresent)
        {
            FileFor(primaryPresent ? _sdr : _hdr, Quality.Bluray1080p);
            var page = Subject.MoviesWithoutFiles(Page());
            Assert.That(page.Records.Single().Id, Is.EqualTo(_movie.Id));
            Assert.That(page.TotalRecords, Is.EqualTo(1));
        }

        [Test]
        public void complete_composite_should_not_be_missing_even_if_legacy_summary_is_empty()
        {
            FileFor(_sdr, Quality.Bluray1080p);
            FileFor(_hdr, Quality.Bluray2160p);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE Movies SET MovieFileId = 0 WHERE Id = @Id", _movie);
            Assert.That(Subject.MoviesWithoutFiles(Page()).TotalRecords, Is.Zero);
        }

        [Test]
        public void disabled_missing_child_should_not_make_movie_wanted()
        {
            FileFor(_sdr, Quality.Bluray1080p);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE MovieVersions SET Monitored = @value WHERE Id = @Id", new { value = false, _hdr.Id });
            Assert.That(Subject.MoviesWithoutFiles(Page()).Records, Is.Empty);
        }

        [Test]
        public void missing_assignment_or_dangling_file_pointer_should_remain_visible_as_missing()
        {
            FileFor(_sdr, Quality.Bluray1080p);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE MovieVersions SET MovieFileId = 99999 WHERE Id = @Id", _hdr);
            Assert.That(Subject.MoviesWithoutFiles(Page()).TotalRecords, Is.EqualTo(1));
            connection.Execute("DELETE FROM MovieVersions WHERE Id = @Id", _hdr);
            Assert.That(Subject.MoviesWithoutFiles(Page()).TotalRecords, Is.EqualTo(1));
        }

        [Test]
        public void foreign_movie_file_should_not_satisfy_missing_child()
        {
            var other = NewMovie(_sdrProfile.Id);
            var foreign = Db.Insert(new MovieFile { MovieId = other.Id, RelativePath = "foreign.mkv", Quality = new QualityModel(), Languages = new List<Language>() });
            FileFor(_sdr, Quality.Bluray1080p);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE MovieVersions SET MovieFileId = @file WHERE Id = @Id", new { file = foreign.Id, _hdr.Id });
            Assert.That(Subject.MoviesWithoutFiles(Page()).Records.Any(m => m.Id == _movie.Id), Is.True);
        }

        [Test]
        public void secondary_cutoff_should_use_its_child_profile_instead_of_parent_or_primary()
        {
            FileFor(_sdr, Quality.Bluray1080p);
            FileFor(_hdr, Quality.Bluray1080p);
            Assert.That(Subject.MoviesWhereCutoffUnmet(Page(), Cutoffs()).Records.Single().Id, Is.EqualTo(_movie.Id));
        }

        [Test]
        public void parent_cutoff_should_not_mark_satisfied_children_as_unmet()
        {
            FileFor(_sdr, Quality.Bluray1080p);
            FileFor(_hdr, Quality.Bluray2160p);
            var cutoffs = Cutoffs();
            cutoffs.Add(new QualitiesBelowCutoff(_parent.Id, new[] { Quality.Bluray1080p.Id }));
            Assert.That(Subject.MoviesWhereCutoffUnmet(Page(), cutoffs).TotalRecords, Is.Zero);
        }

        [Test]
        public void disabled_low_quality_child_should_not_be_cutoff_wanted()
        {
            FileFor(_sdr, Quality.Bluray1080p);
            FileFor(_hdr, Quality.Bluray1080p);
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE MovieVersions SET Monitored = @value WHERE Id = @Id", new { value = false, _hdr.Id });
            Assert.That(Subject.MoviesWhereCutoffUnmet(Page(), Cutoffs()).TotalRecords, Is.Zero);
        }

        [Test]
        public void multiple_unmet_children_should_count_movie_once_and_not_count_missing_as_cutoff()
        {
            FileFor(_sdr, Quality.DVD);
            Assert.That(Subject.MoviesWhereCutoffUnmet(Page(), Cutoffs()).TotalRecords, Is.EqualTo(1));
            FileFor(_hdr, Quality.Bluray1080p);
            var page = Subject.MoviesWhereCutoffUnmet(Page(), Cutoffs());
            Assert.That(page.TotalRecords, Is.EqualTo(1));
            Assert.That(page.Records.Count, Is.EqualTo(1));
        }

        [Test]
        public void paging_and_root_monitor_filter_should_apply_before_counts()
        {
            var ordinary = NewMovie(_sdrProfile.Id);
            var first = Subject.MoviesWithoutFiles(Page(1, 1));
            var second = Subject.MoviesWithoutFiles(Page(2, 1));
            Assert.That(first.TotalRecords, Is.EqualTo(2));
            Assert.That(first.Records.Single().Id, Is.Not.EqualTo(second.Records.Single().Id));
            ordinary.Monitored = false;
            Subject.Update(ordinary);
            var request = Page();
            request.FilterExpressions.Add(m => m.Monitored == true);
            var filtered = Subject.MoviesWithoutFiles(request);
            Assert.That(filtered.TotalRecords, Is.EqualTo(1));
            Assert.That(filtered.Records.Single().Id, Is.EqualTo(_movie.Id));
        }

        [Test]
        public void ordinary_cutoff_should_keep_existing_behavior_and_empty_cutoffs_should_be_valid_sql()
        {
            var ordinary = NewMovie(_sdrProfile.Id);
            var file = Db.Insert(new MovieFile { MovieId = ordinary.Id, RelativePath = "ordinary.mkv", Quality = new QualityModel(Quality.DVD), Languages = new List<Language>() });
            ordinary.MovieFileId = file.Id;
            Subject.Update(ordinary);
            Assert.That(Subject.MoviesWhereCutoffUnmet(Page(), Cutoffs()).Records.Single().Id, Is.EqualTo(ordinary.Id));
            Assert.That(Subject.MoviesWhereCutoffUnmet(Page(), new List<QualitiesBelowCutoff>()).TotalRecords, Is.Zero);
        }
    }
}
