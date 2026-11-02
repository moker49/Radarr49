using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IMakeDownloadDecision
    {
        List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false);
        List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase);
    }

    public class DownloadDecisionMaker : IMakeDownloadDecision
    {
        private readonly IEnumerable<IDownloadDecisionEngineSpecification> _specifications;
        private readonly IParsingService _parsingService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly Logger _logger;
        private readonly IMovieVersionService _movieVersions;

        public DownloadDecisionMaker(IEnumerable<IDownloadDecisionEngineSpecification> specifications,
                                     IParsingService parsingService,
                                     ICustomFormatCalculationService formatCalculator,
                                     IRemoteMovieAggregationService aggregationService,
                                     Logger logger,
                                     IMovieVersionService movieVersions)
        {
            _specifications = specifications;
            _parsingService = parsingService;
            _formatCalculator = formatCalculator;
            _aggregationService = aggregationService;
            _logger = logger;
            _movieVersions = movieVersions;
        }

        public List<DownloadDecision> GetRssDecision(List<ReleaseInfo> reports, bool pushedRelease = false)
        {
            return GetDecisions(reports, pushedRelease).ToList();
        }

        public List<DownloadDecision> GetSearchDecision(List<ReleaseInfo> reports, SearchCriteriaBase searchCriteriaBase)
        {
            return GetDecisions(reports, false, searchCriteriaBase).ToList();
        }

        private IEnumerable<DownloadDecision> GetDecisions(List<ReleaseInfo> reports, bool pushedRelease = false, SearchCriteriaBase searchCriteria = null)
        {
            if (reports.Any())
            {
                _logger.ProgressInfo("Processing {0} releases", reports.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            var reportNumber = 1;
            var versionCache = new Dictionary<int, List<ResolvedMovieVersion>>();
            var contextCache = new Dictionary<(int MovieId, int VersionId), Movie>();

            foreach (var report in reports)
            {
                var reportDecisions = new List<DownloadDecision>();
                _logger.ProgressTrace("Processing release {0}/{1}", reportNumber, reports.Count);
                _logger.Debug("Processing release '{0}' from '{1}'", report.Title, report.Indexer);

                try
                {
                    var parsedMovieInfo = Parser.Parser.ParseMovieTitle(report.Title);

                    if (parsedMovieInfo != null && !parsedMovieInfo.PrimaryMovieTitle.IsNullOrWhiteSpace())
                    {
                        var remoteMovie = report.PendingMovieVersionId.HasValue && report.PendingMovieId.HasValue
                            ? _parsingService.Map(parsedMovieInfo, report.PendingMovieId.Value)
                            : _parsingService.Map(parsedMovieInfo, report.ImdbId.ToString(), report.TmdbId, searchCriteria);
                        remoteMovie.Release = report;
                        remoteMovie.MovieVersionId = report.PendingMovieVersionId;
                        remoteMovie.ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria);

                        if (remoteMovie.Movie == null)
                        {
                            reportDecisions.Add(new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.UnknownMovie, pushedRelease ? "Unknown Movie. Unable to match to existing movie in Library using release title." : "Unknown Movie. Unable to match to correct movie using release title.")));
                        }
                        else
                        {
                            reportDecisions.AddRange(DecideVersions(remoteMovie, new ReleaseDecisionInformation(pushedRelease, searchCriteria), versionCache, contextCache));
                        }
                    }

                    if (searchCriteria != null)
                    {
                        if (parsedMovieInfo == null)
                        {
                            parsedMovieInfo = new ParsedMovieInfo
                            {
                                Languages = LanguageParser.ParseLanguages(report.Title),
                                Quality = QualityParser.ParseQuality(report.Title)
                            };
                        }

                        if (parsedMovieInfo.PrimaryMovieTitle.IsNullOrWhiteSpace())
                        {
                            var remoteMovie = new RemoteMovie
                            {
                                Release = report,
                                ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria),
                                ParsedMovieInfo = parsedMovieInfo,
                                Languages = parsedMovieInfo.Languages,
                            };

                            reportDecisions.Add(new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.UnableToParse, "Unable to parse release")));
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't process release.");

                    var remoteMovie = new RemoteMovie { Release = report, ReleaseSource = GetReleaseSource(pushedRelease, searchCriteria) };
                    reportDecisions.Clear();
                    reportDecisions.Add(new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.Error, "Unexpected error processing release")));
                }

                reportNumber++;

                foreach (var decision in reportDecisions)
                {
                    if (decision.Rejections.Any())
                    {
                        _logger.Debug("Release '{0}' from '{1}' rejected for the following reasons: {2}", report.Title, report.Indexer, string.Join(", ", decision.Rejections));
                    }
                    else
                    {
                        _logger.Debug("Release '{0}' from '{1}' accepted", report.Title, report.Indexer);
                    }

                    yield return decision;
                }
            }
        }

        private List<DownloadDecision> DecideVersions(RemoteMovie source,
                                                     ReleaseDecisionInformation information,
                                                     Dictionary<int, List<ResolvedMovieVersion>> versionCache,
                                                     Dictionary<(int MovieId, int VersionId), Movie> contextCache)
        {
            List<RemoteMovie> contexts;
            try
            {
                if (!versionCache.TryGetValue(source.Movie.Id, out var versions))
                {
                    versions = _movieVersions.Resolve(source.Movie);
                    if (versions == null || versions.Count == 0)
                    {
                        throw new InvalidOperationException("No movie versions could be resolved.");
                    }

                    versionCache[source.Movie.Id] = versions;
                }

                if (source.MovieVersionId.HasValue)
                {
                    versions = versions.Where(v => v.Id == source.MovieVersionId).ToList();
                    if (versions.Count != 1)
                    {
                        throw new InvalidOperationException("The pending release's version no longer belongs to this movie and profile.");
                    }
                }

                contexts = new List<RemoteMovie>();
                foreach (var version in versions)
                {
                    if (!version.Id.HasValue)
                    {
                        contexts.Add(source);
                        continue;
                    }

                    var key = (source.Movie.Id, version.Id.Value);
                    if (!contextCache.TryGetValue(key, out var movie))
                    {
                        movie = _movieVersions.ForVersion(source.Movie, version.Id.Value) ?? throw new InvalidOperationException("The version context could not be restored.");
                        contextCache[key] = movie;
                    }

                    contexts.Add(source.WithVersion(movie, version.Id.Value, version.Priority));
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is ModelNotFoundException)
            {
                return new List<DownloadDecision>
                {
                    new DownloadDecision(source, new DownloadRejection(DownloadRejectionReason.InvalidMovieVersion, e.Message))
                };
            }

            var decisions = new List<DownloadDecision>();
            foreach (var context in contexts)
            {
                _aggregationService.Augment(context);
                context.CustomFormats = _formatCalculator.ParseCustomFormat(context, context.Release.Size);
                context.CustomFormatScore = context.Movie.QualityProfile?.CalculateCustomFormatScore(context.CustomFormats) ?? 0;
                context.DownloadAllowed = true;
                decisions.Add(GetDecisionForReport(context, information));
            }

            return decisions;
        }

        private DownloadDecision GetDecisionForReport(RemoteMovie remoteMovie, ReleaseDecisionInformation information)
        {
            var reasons = Array.Empty<DownloadRejection>();

            foreach (var specifications in _specifications.GroupBy(v => v.Priority).OrderBy(v => v.Key))
            {
                reasons = specifications.Select(c => EvaluateSpec(c, remoteMovie, information))
                                        .Where(c => c != null)
                                        .ToArray();

                if (reasons.Any())
                {
                    break;
                }
            }

            return new DownloadDecision(remoteMovie, reasons.ToArray());
        }

        private DownloadRejection EvaluateSpec(IDownloadDecisionEngineSpecification spec, RemoteMovie remoteMovie, ReleaseDecisionInformation information)
        {
            try
            {
                var result = spec.IsSatisfiedBy(remoteMovie, information);

                if (!result.Accepted)
                {
                    return new DownloadRejection(result.Reason, result.Message, spec.Type);
                }
            }
            catch (NotImplementedException)
            {
                _logger.Trace("Spec " + spec.GetType().Name + " does not care about movies.");
            }
            catch (Exception e)
            {
                e.Data.Add("report", remoteMovie.Release.ToJson());
                e.Data.Add("parsed", remoteMovie.ParsedMovieInfo.ToJson());
                _logger.Error(e, "Couldn't evaluate decision on {0}, with spec: {1}", remoteMovie.Release.Title, spec.GetType().Name);
                return new DownloadRejection(DownloadRejectionReason.DecisionError, $"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }

        private ReleaseSourceType GetReleaseSource(bool pushedRelease, SearchCriteriaBase searchCriteria = null)
        {
            if (searchCriteria == null)
            {
                return pushedRelease ? ReleaseSourceType.ReleasePush : ReleaseSourceType.Rss;
            }

            if (searchCriteria.InteractiveSearch)
            {
                return ReleaseSourceType.InteractiveSearch;
            }
            else if (searchCriteria.UserInvokedSearch)
            {
                return ReleaseSourceType.UserInvokedSearch;
            }
            else
            {
                return ReleaseSourceType.Search;
            }
        }
    }
}
