using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class WantedMovieSearchFixture : CoreTest<MovieSearchService>
    {
        private Movie _movie;
        private List<MovieVersionStatus> _versions;
        private List<NzbDrone.Core.Queue.Queue> _queue;
        private List<DownloadDecision> _selected;

        [SetUp]
        public void Setup()
        {
            _selected = null;
            _movie = new Movie { Id = 1, Monitored = true };
            _versions = new List<MovieVersionStatus>
            {
                new MovieVersionStatus { Version = new ResolvedMovieVersion { Id = 7, MovieId = 1, Monitored = true }, File = new NzbDrone.Core.MediaFiles.MovieFile { Id = 11 }, QualityCutoffNotMet = true },
                new MovieVersionStatus { Version = new ResolvedMovieVersion { Id = 8, MovieId = 1, Monitored = true } }
            };
            _queue = new List<NzbDrone.Core.Queue.Queue>();
            Mocker.GetMock<IMovieService>().Setup(v => v.MoviesWithoutFiles(It.IsAny<PagingSpec<Movie>>())).Returns(new PagingSpec<Movie> { Records = new List<Movie> { _movie } });
            Mocker.GetMock<IMovieCutoffService>().Setup(v => v.MoviesWhereCutoffUnmet(It.IsAny<PagingSpec<Movie>>())).Returns(new PagingSpec<Movie> { Records = new List<Movie> { _movie } });
            Mocker.GetMock<IMovieVersionStatusService>().Setup(v => v.Get(_movie)).Returns(_versions);
            Mocker.GetMock<IQueueService>().Setup(v => v.GetQueue()).Returns(_queue);
            Mocker.GetMock<ISearchForReleases>().Setup(v => v.MovieSearch(1, true, false)).ReturnsAsync(new List<DownloadDecision> { Decision(7), Decision(8), Decision(null), Decision(8, 2) });
            Mocker.GetMock<IProcessDownloadDecisions>().Setup(v => v.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns<List<DownloadDecision>>(decisions =>
                {
                    _selected = decisions;
                    return Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>()));
                });
        }

        private DownloadDecision Decision(int? version, int movie = 1) => new DownloadDecision(new RemoteMovie { Movie = new Movie { Id = movie }, MovieVersionId = version });
        private void Missing() => Subject.Execute(new MissingMoviesSearchCommand { Trigger = CommandTrigger.Manual });
        private void Cutoff() => Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Manual });

        [Test]
        public void missing_search_should_only_process_missing_version_not_primary_upgrade()
        {
            Missing();
            Assert.That(_selected.Select(d => d.RemoteMovie.MovieVersionId), Is.EqualTo(new int?[] { 8 }));
        }

        [Test]
        public void cutoff_search_should_only_process_present_version_below_its_quality_cutoff()
        {
            Cutoff();
            Assert.That(_selected.Select(d => d.RemoteMovie.MovieVersionId), Is.EqualTo(new int?[] { 7 }));
        }

        [Test]
        public void queued_sdr_should_not_hide_missing_hdr()
        {
            _queue.Add(new NzbDrone.Core.Queue.Queue { Movie = _movie, RemoteMovie = new RemoteMovie { MovieVersionId = 7 } });
            Missing();
            Assert.That(_selected.Single().RemoteMovie.MovieVersionId, Is.EqualTo(8));
        }

        [TestCase(null)]
        [TestCase(8)]
        public void ambiguous_or_matching_queue_should_block_missing_target(int? version)
        {
            _queue.Add(new NzbDrone.Core.Queue.Queue { Movie = _movie, RemoteMovie = new RemoteMovie { MovieVersionId = version } });
            Missing();
            Mocker.GetMock<ISearchForReleases>().Verify(v => v.MovieSearch(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void queued_other_movie_should_not_block_target_with_same_version_marker()
        {
            _queue.Add(new NzbDrone.Core.Queue.Queue { Movie = new Movie { Id = 2 }, RemoteMovie = new RemoteMovie { MovieVersionId = 8 } });
            Missing();
            Assert.That(_selected.Single().RemoteMovie.MovieVersionId, Is.EqualTo(8));
        }

        [Test]
        public void unmonitored_or_invalid_missing_child_should_not_be_searched()
        {
            _versions[1].Version.Monitored = false;
            Missing();
            Assert.That(_selected, Is.Null);
            _versions[1].Version.Monitored = true;
            _versions[1].Error = "Invalid file ownership";
            Missing();
            Assert.That(_selected, Is.Null);
        }

        [Test]
        public void ordinary_implicit_missing_search_should_process_only_unversioned_decisions()
        {
            _versions.RemoveAt(0);
            _versions[0].Version.Id = null;
            Missing();
            Assert.That(_selected.Single().RemoteMovie.MovieVersionId, Is.Null);
        }

        [Test]
        public void ordinary_queued_movie_should_keep_whole_movie_exclusion()
        {
            _versions.RemoveAt(0);
            _versions[0].Version.Id = null;
            _queue.Add(new NzbDrone.Core.Queue.Queue { Movie = _movie });
            Missing();
            Assert.That(_selected, Is.Null);
        }

        [Test]
        public void conflicting_queue_version_identities_should_block_ambiguous_movie()
        {
            _queue.Add(new NzbDrone.Core.Queue.Queue { Movie = new Movie { Id = 1, ManagedVersion = new ResolvedMovieVersion { Id = 7 } }, RemoteMovie = new RemoteMovie { MovieVersionId = 8 } });
            Missing();
            Assert.That(_selected, Is.Null);
        }

        [Test]
        public void cutoff_search_should_not_treat_custom_format_only_upgrade_as_quality_cutoff()
        {
            _versions[0].QualityCutoffNotMet = false;
            _versions[0].CutoffNotMet = true;
            Cutoff();
            Assert.That(_selected, Is.Null);
        }
    }
}
