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
