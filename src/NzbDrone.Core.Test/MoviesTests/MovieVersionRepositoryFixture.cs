using System;
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

namespace NzbDrone.Core.Test.MoviesTests
{
    [TestFixture]
    public class MovieVersionRepositoryFixture : DbTest<MovieVersionRepository, MovieVersion>
    {
        private Movie _movie;
        private List<MovieVersion> _versions;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = 1).With(m => m.MovieFileId = 0).Build();
            Db.Insert(_movie);
            var sdr = new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true };
            var hdr = new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 };
            Db.Insert(sdr);
            Db.Insert(hdr);
            _versions = new List<MovieVersion>
            {
                new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = sdr.Id, Monitored = true },
                new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = hdr.Id, Monitored = true }
            };
        }

        [Test]
        public void should_persist_both_versions_and_selected_profile_together()
        {
            Subject.AssignComposite(_movie, 10, _versions, null);

            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            Assert.That(connection.ExecuteScalar<int>("SELECT \"QualityProfileId\" FROM \"Movies\" WHERE \"Id\" = @Id", _movie), Is.EqualTo(10));
            Assert.That(Subject.ForMovie(_movie.Id).Count, Is.EqualTo(2));
            Assert.That(_versions.All(v => v.Id > 0), Is.True);
        }

        [Test]
        public void stale_movie_should_not_be_overwritten()
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("UPDATE \"Movies\" SET \"QualityProfileId\" = 2 WHERE \"Id\" = @Id", _movie);

            Assert.Throws<InvalidOperationException>(() => Subject.AssignComposite(_movie, 10, _versions, null));
            Assert.That(Subject.ForMovie(_movie.Id), Is.Empty);
            Assert.That(connection.ExecuteScalar<int>("SELECT \"QualityProfileId\" FROM \"Movies\" WHERE \"Id\" = @Id", _movie), Is.EqualTo(2));
        }

        [Test]
        public void failure_after_first_insert_should_roll_back_assignments_and_profile()
        {
            var file = Builder<MovieFile>.CreateNew().With(f => f.Id = 0).With(f => f.MovieId = _movie.Id).With(f => f.Quality = new QualityModel()).With(f => f.Languages = new List<Language>()).Build();
            Db.Insert(file);
            _versions.ForEach(v => v.MovieFileId = file.Id);

            Assert.That(() => Subject.AssignComposite(_movie, 10, _versions, file.Id), Throws.Exception);

            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            Assert.That(Subject.ForMovie(_movie.Id), Is.Empty);
            Assert.That(connection.ExecuteScalar<int>("SELECT \"QualityProfileId\" FROM \"Movies\" WHERE \"Id\" = @Id", _movie), Is.EqualTo(1));
            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieFiles\" WHERE \"Id\" = @Id", file), Is.EqualTo(1));
        }
    }
}
