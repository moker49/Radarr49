using System;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class NoReleasesSearchResourceFixture : TestBase
    {
        [TestCase(false)]
        [TestCase(true)]
        public void movie_resource_should_expose_persisted_no_releases_date(bool emptySearch)
        {
            var movie = new Movie
            {
                Title = "Search test",
                LastNoReleasesSearchTime = emptySearch ? new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) : null
            };

            Assert.That(movie.ToResource(0).LastNoReleasesSearchTime, Is.EqualTo(movie.LastNoReleasesSearchTime));
        }
    }
}
