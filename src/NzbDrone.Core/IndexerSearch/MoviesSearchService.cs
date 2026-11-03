using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.IndexerSearch
{
    public class MovieSearchService : IExecute<MoviesSearchCommand>, IExecute<MissingMoviesSearchCommand>, IExecute<CutoffUnmetMoviesSearchCommand>
    {
        private readonly IMovieService _movieService;
        private readonly IMovieCutoffService _movieCutoffService;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IQueueService _queueService;
        private readonly Logger _logger;
        private readonly IMovieVersionStatusService _versions;

        private enum WantedSearch
        {
            None,
            Missing,
            QualityCutoff
        }

        public MovieSearchService(IMovieService movieService,
                                   IMovieCutoffService movieCutoffService,
                                   ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IQueueService queueService,
                                   Logger logger,
                                   IMovieVersionStatusService versions)
        {
            _movieService = movieService;
            _movieCutoffService = movieCutoffService;
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _queueService = queueService;
            _logger = logger;
            _versions = versions;
        }

        public void Execute(MoviesSearchCommand message)
        {
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;

            var movies = _movieService.GetMovies(message.MovieIds)
                .Where(m => (m.Monitored && m.IsAvailable()) || userInvokedSearch)
                .ToList();

            SearchForBulkMovies(movies, userInvokedSearch).GetAwaiter().GetResult();
        }

        public void Execute(MissingMoviesSearchCommand message)
        {
            var pagingSpec = new PagingSpec<Movie>
            {
                Page = 1,
                PageSize = 100000,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Monitored == true);

            var movies = _movieService.MoviesWithoutFiles(pagingSpec).Records.ToList();

            SearchForBulkMovies(movies, message.Trigger == CommandTrigger.Manual, WantedSearch.Missing).GetAwaiter().GetResult();
        }

        public void Execute(CutoffUnmetMoviesSearchCommand message)
        {
            var pagingSpec = new PagingSpec<Movie>
            {
                Page = 1,
                PageSize = 100000,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Monitored == true);

            var movies = _movieCutoffService.MoviesWhereCutoffUnmet(pagingSpec).Records.ToList();

            SearchForBulkMovies(movies, message.Trigger == CommandTrigger.Manual, WantedSearch.QualityCutoff).GetAwaiter().GetResult();
        }

        private async Task SearchForBulkMovies(List<Movie> movies, bool userInvokedSearch, WantedSearch wanted = WantedSearch.None)
        {
            _logger.ProgressInfo("Performing search for {0} movies", movies.Count);
            var downloadedCount = 0;
            var queue = wanted == WantedSearch.None ? null : _queueService.GetQueue();

            foreach (var movieId in movies.GroupBy(e => e.Id).OrderBy(g => g.Min(m => m.LastSearchTime ?? DateTime.MinValue)))
            {
                List<DownloadDecision> decisions;

                try
                {
                    HashSet<int?> targets = null;
                    if (wanted != WantedSearch.None)
                    {
                        var statuses = _versions.Get(movieId.First());
                        var queued = queue.Where(q => q.Movie?.Id == movieId.Key).ToList();
                        targets = statuses.Where(s => s.Version.Monitored && s.Error == null &&
                                (wanted == WantedSearch.Missing ? !s.HasFile : s.HasFile && s.QualityCutoffNotMet) &&
                                !IsQueued(queued, s.Version.Id))
                            .Select(s => s.Version.Id).ToHashSet();
                        if (targets.Count == 0)
                        {
                            continue;
                        }
                    }

                    decisions = await _releaseSearchService.MovieSearch(movieId.Key, userInvokedSearch, false);
                    if (targets != null)
                    {
                        decisions = decisions.Where(d => d.RemoteMovie?.Movie?.Id == movieId.Key && targets.Contains(d.RemoteMovie.MovieVersionId)).ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to search for movie: [{0}]", movieId.Key);
                    continue;
                }

                var processDecisions = await _processDownloadDecisions.ProcessDecisions(decisions);

                downloadedCount += processDecisions.Grabbed.Count;
            }

            _logger.ProgressInfo("Completed search for {0} movies. {1} reports downloaded.", movies.Count, downloadedCount);
        }

        private static bool IsQueued(IEnumerable<Queue.Queue> queue, int? versionId) => queue.Any(q =>
        {
            if (!versionId.HasValue)
            {
                return true;
            }

            var remote = q.RemoteMovie?.MovieVersionId;
            var context = q.Movie?.ManagedVersion?.Id;
            if (remote.HasValue && context.HasValue && remote != context)
            {
                return true;
            }

            var queuedVersion = remote ?? context;
            return !queuedVersion.HasValue || queuedVersion <= 0 || queuedVersion == versionId;
        });
    }
}
