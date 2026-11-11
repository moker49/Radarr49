using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class ManagedVersionProfileSpecificationFixture : CoreTest<ManagedVersionProfileSpecification>
    {
        [TestCase(false)]
        [TestCase(true)]
        public void unknown_language_should_not_be_classified_as_a_confirmed_mismatch(bool hasUnknownLanguage)
        {
            var local = CreateLocalMovie();
            local.Languages = hasUnknownLanguage ? new List<Language> { Language.Unknown } : null;

            var result = Subject.IsSatisfiedBy(local, null);

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(ImportRejectionReason.VersionProfileNotSatisfied));
        }

        [Test]
        public void known_wrong_language_should_be_classified_as_a_confirmed_mismatch()
        {
            var local = CreateLocalMovie();
            local.Languages = new List<Language> { Language.French };

            var result = Subject.IsSatisfiedBy(local, null);

            Assert.That(result.Reason, Is.EqualTo(ImportRejectionReason.VersionLanguageNotSatisfied));
        }

        [TestCase(899, false)]
        [TestCase(900, true)]
        [TestCase(999, true)]
        [TestCase(1000, true)]
        [TestCase(1500, true)]
        [TestCase(2000, true)]
        [TestCase(2001, true)]
        [TestCase(2200, true)]
        [TestCase(2201, false)]
        public void import_should_allow_ten_percent_tolerance_and_classify_larger_mismatches_for_retry(int megabytes, bool accepted)
        {
            var local = CreateLocalMovie();
            local.Movie.QualityProfile.Language = Language.Any;
            local.Movie.MovieMetadata = new MovieMetadata { Runtime = 100 };
            local.Movie.QualityProfile.SizeDefinitions = new List<ProfileSizeDefinition>
            {
                new ProfileSizeDefinition { QualityId = Quality.Bluray1080p.Id, MinSize = 10, MaxSize = 20 }
            };
            local.Size = megabytes * 1024L * 1024;
            Mocker.GetMock<IQualityDefinitionService>().Setup(q => q.Get(Quality.Bluray1080p)).Returns(new QualityDefinition(Quality.Bluray1080p));

            var result = Subject.IsSatisfiedBy(local, null);

            Assert.That(result.Accepted, Is.EqualTo(accepted));
            if (!accepted)
            {
                Assert.That(result.Reason, Is.EqualTo(ImportRejectionReason.VersionSizeNotSatisfied));
            }
        }

        private static LocalMovie CreateLocalMovie()
        {
            return new LocalMovie
            {
                MovieVersionId = 7,
                Quality = new QualityModel(Quality.Bluray1080p),
                Movie = new Movie
                {
                    QualityProfile = new QualityProfile
                    {
                        Language = Language.English,
                        Items = new List<QualityProfileQualityItem>
                        {
                            new QualityProfileQualityItem { Quality = Quality.Bluray1080p, Allowed = true }
                        }
                    }
                }
            };
        }
    }
}
