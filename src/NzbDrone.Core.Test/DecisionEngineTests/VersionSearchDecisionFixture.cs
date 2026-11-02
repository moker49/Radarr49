using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class VersionSearchDecisionFixture : CoreTest<DownloadDecisionMaker>
    {
        private RemoteMovie _source;
        private Movie _sdr;
        private Movie _hdr;

        [SetUp]
        public void Setup()
        {
            _sdr = Context(1, 0, 10);
            _hdr = Context(2, 15, 30);
            _source = new RemoteMovie { Movie = _sdr, ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.WEBDL1080p) } };
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>())).Returns(_source);
            Mocker.GetMock<IParsingService>().Setup(s => s.Map(It.IsAny<ParsedMovieInfo>(), 1)).Returns(_source);
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.Resolve(It.IsAny<Movie>())).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 201, Priority = 0 }, new ResolvedMovieVersion { Id = 202, Priority = 1 }
            });
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.ForVersion(It.IsAny<Movie>(), 201)).Returns(_sdr);
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.ForVersion(It.IsAny<Movie>(), 202)).Returns(_hdr);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<IQualityDefinitionService>().Setup(s => s.Get(It.IsAny<Quality>())).Returns(new QualityDefinition(Quality.WEBDL1080p));
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(new[]
            {
                new AcceptableSizeSpecification(Mocker.GetMock<IQualityDefinitionService>().Object, TestLogger)
            });
        }

        private static Movie Context(int profileId, double min, double max)
        {
            var movie = new Movie
            {
                Id = 1,
                QualityProfileId = 10,
                QualityProfile = new QualityProfile
                {
                    Id = profileId,
                    SizeDefinitions = new List<ProfileSizeDefinition>
                    {
                        new ProfileSizeDefinition { QualityId = Quality.WEBDL1080p.Id, MinSize = min, MaxSize = max }
                    }
                }
            };
            movie.MovieMetadata.Value.Runtime = 100;
            return movie;
        }

        [TestCase(500, 201)]
        [TestCase(2000, 202)]
        public void one_release_should_be_evaluated_independently_for_both_profiles(int sizeMb, int acceptedVersion)
        {
            var releases = new List<ReleaseInfo> { new ReleaseInfo { Title = "Alien.1979.1080p.WEB-DL", Size = sizeMb.Megabytes() } };

            var decisions = Subject.GetRssDecision(releases);

            Assert.That(decisions.Count, Is.EqualTo(2));
            Assert.That(decisions.Single(d => d.Approved).RemoteMovie.MovieVersionId, Is.EqualTo(acceptedVersion));
            Assert.That(decisions[0].RemoteMovie, Is.Not.SameAs(decisions[1].RemoteMovie));
            Assert.That(_source.Movie.QualityProfile.Id, Is.EqualTo(1));
        }

        [Test]
        public void version_aggregation_should_not_mutate_another_versions_parsed_data()
        {
            Mocker.GetMock<IRemoteMovieAggregationService>().Setup(s => s.Augment(It.IsAny<RemoteMovie>()))
                .Callback<RemoteMovie>(r =>
                {
                    r.ParsedMovieInfo.Languages.Add(Language.English);
                    r.Release.Languages.Add(Language.French);
                });
            var release = new ReleaseInfo { Title = "Alien.1979.1080p.WEB-DL", Size = 500.Megabytes() };

            var decisions = Subject.GetRssDecision(new List<ReleaseInfo> { release });

            Assert.That(decisions.All(d => d.RemoteMovie.ParsedMovieInfo.Languages.Count == 1 && d.RemoteMovie.Release.Languages.Count == 1), Is.True);
            Assert.That(release.Languages, Is.Empty);
            Assert.That(_source.ParsedMovieInfo.Languages, Is.Empty);
        }

        [Test]
        public void batch_should_resolve_each_movie_and_version_context_once()
        {
            var releases = new List<ReleaseInfo>
            {
                new ReleaseInfo { Title = "Alien.1979.1080p.WEB-DL", Size = 500.Megabytes() },
                new ReleaseInfo { Title = "Alien.1979.1080p.WEB-DL", Size = 2000.Megabytes() }
            };

            Assert.That(Subject.GetRssDecision(releases).Count, Is.EqualTo(4));

            Mocker.GetMock<IMovieVersionService>().Verify(s => s.Resolve(It.IsAny<Movie>()), Times.Once());
            Mocker.GetMock<IMovieVersionService>().Verify(s => s.ForVersion(It.IsAny<Movie>(), 201), Times.Once());
            Mocker.GetMock<IMovieVersionService>().Verify(s => s.ForVersion(It.IsAny<Movie>(), 202), Times.Once());
        }

        [Test]
        public void pending_release_should_only_recheck_its_original_version()
        {
            var releases = new List<ReleaseInfo>
            {
                new ReleaseInfo { Title = "Alien.1979.1080p.WEB-DL", Size = 2000.Megabytes(), PendingMovieId = 1, PendingMovieVersionId = 202 }
            };

            var decision = Subject.GetRssDecision(releases).Single();

            Assert.That(decision.RemoteMovie.MovieVersionId, Is.EqualTo(202));
            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void stale_pending_version_should_be_rejected_without_guessing()
        {
            var releases = new List<ReleaseInfo>
            {
                new ReleaseInfo { Title = "Alien.1979.1080p.WEB-DL", PendingMovieId = 1, PendingMovieVersionId = 999 }
            };

            var decision = Subject.GetRssDecision(releases).Single();

            Assert.That(decision.Rejections.Single().Reason, Is.EqualTo(DownloadRejectionReason.InvalidMovieVersion));
        }
    }
}
