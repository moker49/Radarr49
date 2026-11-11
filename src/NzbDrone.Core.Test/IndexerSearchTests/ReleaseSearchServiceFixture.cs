using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
        public class ReleaseSearchServiceFixture : CoreTest<ReleaseSearchService>
    {
        private Mock<IIndexer> _mockIndexer;
        private Movie _movie;

        [SetUp]
        public void SetUp()
        {
            _mockIndexer = Mocker.GetMock<IIndexer>();
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _mockIndexer.SetupGet(s => s.SupportsSearch).Returns(true);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _mockIndexer.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<Parser.Model.ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns(new List<DownloadDecision>());

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            Mocker.GetMock<IMovieTranslationService>()
                .Setup(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>()))
                .Returns(new List<MovieTranslation>());
        }

        private List<SearchCriteriaBase> WatchForSearchCriteria()
        {
            var result = new List<SearchCriteriaBase>();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Callback<MovieSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            return result;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task empty_search_should_record_its_date_for_automatic_and_interactive_searches(bool interactive)
        {
            WatchForSearchCriteria();
            Mocker.GetMock<IIndexerFactory>().Setup(f => f.InteractiveSearchEnabled(true)).Returns(new List<IIndexer> { _mockIndexer.Object });
            _movie.LastNoReleasesSearchTime = null;

            await Subject.MovieSearch(_movie, true, interactive);

            Assert.That(_movie.LastNoReleasesSearchTime, Is.EqualTo(_movie.LastSearchTime));
            Assert.That(_movie.LastNoReleasesSearchTime, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
            Mocker.GetMock<IMovieService>().Verify(s => s.UpdateLastSearchTime(_movie), Times.Once());
        }

        [Test]
        public async Task finding_reports_should_clear_empty_search_even_when_all_decisions_are_rejected()
        {
            _movie.LastNoReleasesSearchTime = DateTime.UtcNow.AddDays(-1);
            _mockIndexer.Setup(i => i.Fetch(It.IsAny<MovieSearchCriteria>())).ReturnsAsync(new List<ReleaseInfo> { new ReleaseInfo() });

            await Subject.MovieSearch(_movie, true, false);

            Assert.That(_movie.LastNoReleasesSearchTime, Is.Null);
        }

        [Test]
        public async Task no_enabled_indexers_should_not_record_an_empty_search()
        {
            var previous = DateTime.UtcNow.AddDays(-1);
            _movie.LastNoReleasesSearchTime = previous;
            Mocker.GetMock<IIndexerFactory>().Setup(f => f.AutomaticSearchEnabled(true)).Returns(new List<IIndexer>());

            await Subject.MovieSearch(_movie, true, false);

            Assert.That(_movie.LastNoReleasesSearchTime, Is.EqualTo(previous));
            Mocker.GetMock<IMovieService>().Verify(s => s.UpdateLastSearchTime(It.IsAny<Movie>()), Times.Never());
        }

        [Test]
        public async Task thrown_indexer_failure_should_not_record_an_empty_search()
        {
            var previous = DateTime.UtcNow.AddDays(-1);
            _movie.LastNoReleasesSearchTime = previous;
            _mockIndexer.Setup(i => i.Fetch(It.IsAny<MovieSearchCriteria>())).ThrowsAsync(new Exception("Indexer unavailable"));

            await Subject.MovieSearch(_movie, true, false);

            Assert.That(_movie.LastNoReleasesSearchTime, Is.EqualTo(previous));
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public async Task failure_recorded_by_http_indexer_should_not_be_mistaken_for_an_empty_search()
        {
            WatchForSearchCriteria();
            _movie.LastNoReleasesSearchTime = null;
            Mocker.GetMock<IIndexerStatusRepository>().Setup(s => s.FindByProviderId(1)).Returns(() => new IndexerStatus { MostRecentFailure = DateTime.UtcNow });

            await Subject.MovieSearch(_movie, true, false);

            Assert.That(_movie.LastNoReleasesSearchTime, Is.Null);
        }

        [Test]
        public async Task Tags_IndexerTags_MovieNoTags_IndexerNotIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 3 }
            });

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task Tags_IndexerNoTags_MovieTags_IndexerIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1
            });

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 3 })
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
        }

        [Test]
        public async Task Tags_IndexerAndMovieTagsMatch_IndexerIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 1, 2, 3 }
            });

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 3, 4, 5 })
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
        }

        [Test]
        public async Task Tags_IndexerAndMovieTagsMismatch_IndexerNotIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 1, 2, 3 }
            });

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 4, 5, 6 })
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }
    }
}
