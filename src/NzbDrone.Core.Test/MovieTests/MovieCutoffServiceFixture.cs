using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieCutoffServiceFixture : CoreTest<MovieCutoffService>
    {
        [Test]
        public void no_below_cutoff_qualities_should_clear_reused_page_count_and_records()
        {
            Mocker.GetMock<IQualityProfileService>().Setup(v => v.All()).Returns(new List<QualityProfile>());
            var page = new PagingSpec<Movie> { TotalRecords = 25, Records = new List<Movie> { new Movie() } };
            var result = Subject.MoviesWhereCutoffUnmet(page);
            Assert.That(result.TotalRecords, Is.Zero);
            Assert.That(result.Records, Is.Empty);
            Mocker.GetMock<IMovieRepository>().Verify(v => v.MoviesWhereCutoffUnmet(It.IsAny<PagingSpec<Movie>>(), It.IsAny<List<QualitiesBelowCutoff>>()), Times.Never());
        }

        [Test]
        public void each_profile_should_contribute_its_own_cutoff_quality_set()
        {
            Mocker.GetMock<IQualityProfileService>().Setup(v => v.All()).Returns(new List<QualityProfile>
            {
                new QualityProfile { Id = 5, UpgradeAllowed = true, Cutoff = Quality.Bluray1080p.Id, Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.DVD, Allowed = true }, new QualityProfileQualityItem { Quality = Quality.Bluray1080p, Allowed = true } } },
                new QualityProfile { Id = 6, UpgradeAllowed = true, Cutoff = Quality.Bluray2160p.Id, Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.Bluray1080p, Allowed = true }, new QualityProfileQualityItem { Quality = Quality.Bluray2160p, Allowed = true } } }
            });
            Subject.MoviesWhereCutoffUnmet(new PagingSpec<Movie>());
            Mocker.GetMock<IMovieRepository>().Verify(v => v.MoviesWhereCutoffUnmet(It.IsAny<PagingSpec<Movie>>(), It.Is<List<QualitiesBelowCutoff>>(p => p.Count == 2 && p[0].ProfileId == 5 && p[0].QualityIds.Single() == Quality.DVD.Id && p[1].ProfileId == 6 && p[1].QualityIds.Single() == Quality.Bluray1080p.Id)), Times.Once());
        }
    }
}
