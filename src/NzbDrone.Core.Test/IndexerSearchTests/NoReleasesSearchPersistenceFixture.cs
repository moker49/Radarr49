using System;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class NoReleasesSearchPersistenceFixture : DbTest<MovieRepository, Movie>
    {
        [Test]
        public void empty_search_date_should_persist_and_clear_without_changing_other_movie_fields()
        {
            var movie = Db.Insert(new Movie { Path = "Movies/Test", Monitored = true });
            movie.LastSearchTime = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            movie.LastNoReleasesSearchTime = movie.LastSearchTime;

            Subject.SetFields(movie, m => m.LastSearchTime, m => m.LastNoReleasesSearchTime);

            Assert.That(Db.All<Movie>().Single().LastNoReleasesSearchTime, Is.EqualTo(movie.LastSearchTime));
            movie.LastNoReleasesSearchTime = null;
            Subject.SetFields(movie, m => m.LastNoReleasesSearchTime);
            var saved = Db.All<Movie>().Single();
            Assert.That(saved.LastNoReleasesSearchTime, Is.Null);
            Assert.That(saved.Path, Is.EqualTo("Movies/Test"));
            Assert.That(saved.Monitored, Is.True);
        }
    }
}
