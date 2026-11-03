using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests
{
    [TestFixture]
    public class CompositeNewMovieRepositoryFixture : DbTest<MovieRepository, Movie>
    {
        private QualityProfile _parent;
        private List<CompositeProfileVersion> _definitions;

        [SetUp]
        public void Setup()
        {
            _parent = new QualityProfile { Name = "Composite", Items = new List<QualityProfileQualityItem>(), CompositeTestRoot = "C:/CompositeTest" };
            Db.Insert(_parent);
            _definitions = new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { CompositeProfileId = _parent.Id, QualityProfileId = 100, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { CompositeProfileId = _parent.Id, QualityProfileId = 101, Label = "HDR", Priority = 1 }
            };
            _definitions.ForEach(d => Db.Insert(d));
        }

        private Movie NewMovie(string folder = "Movie")
        {
            return Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = _parent.Id)
                .With(m => m.MovieFileId = 0).With(m => m.Path = "C:/CompositeTest/" + folder).Build();
        }

        [Test]
        public void should_commit_movie_and_both_empty_monitored_versions()
        {
            var movie = NewMovie();
            movie.Monitored = false;
            Subject.InsertWithVersions(new[] { movie });

            var versions = Db.All<MovieVersion>();
            Assert.That(versions.Select(v => v.CompositeProfileVersionId), Is.EquivalentTo(_definitions.Select(d => d.Id)));
            Assert.That(versions.All(v => v.MovieId == movie.Id && v.Monitored && v.MovieFileId == null), Is.True);
            Assert.That(Db.All<Movie>().Single().MovieFileId, Is.Zero);
            Assert.That(Db.All<Movie>().Single().QualityProfileId, Is.EqualTo(_parent.Id));
        }

        [Test]
        public void should_roll_back_entire_batch_when_second_insert_fails()
        {
            // A real database error after the first movie/version writes proves transaction scope.
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("CREATE TRIGGER fail_second_movie BEFORE INSERT ON \"Movies\" WHEN NEW.\"Path\" LIKE '%Fail' BEGIN SELECT RAISE(ABORT, 'simulated failure'); END");
            var movies = new[] { NewMovie("First"), NewMovie("Fail") };

            Assert.That(() => Subject.InsertWithVersions(movies), Throws.Exception);

            Assert.That(Db.All<Movie>(), Is.Empty);
            Assert.That(Db.All<MovieVersion>(), Is.Empty);
            Assert.That(movies.All(m => m.Id == 0), Is.True);
        }

        [TestCase(null)]
        [TestCase("C:/Elsewhere")]
        public void should_recheck_activation_inside_transaction(string root)
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE \"QualityProfiles\" SET \"CompositeTestRoot\" = @root WHERE \"Id\" = @Id", new { root, _parent.Id });

            Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(() => Subject.InsertWithVersions(new[] { NewMovie() }));
            Assert.That(Db.All<Movie>(), Is.Empty);
            Assert.That(Db.All<MovieVersion>(), Is.Empty);
        }

        [Test]
        public void assigned_profile_cannot_be_deactivated_or_rebound()
        {
            Subject.InsertWithVersions(new[] { NewMovie() });
            var repository = Mocker.Resolve<CompositeProfileVersionRepository>();

            Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(() => repository.SetTestRoot(_parent.Id, null));
            Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(() => repository.SetTestRoot(_parent.Id, "C:/Elsewhere"));
        }
    }
}
