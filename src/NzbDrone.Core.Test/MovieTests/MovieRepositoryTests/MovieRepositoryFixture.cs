using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.CustomFormats;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.MovieRepositoryTests
{
    [TestFixture]

    public class MovieRepositoryFixture : DbTest<MovieRepository, Movie>
    {
        private IQualityProfileRepository _profileRepository;

        [SetUp]
        public void Setup()
        {
            _profileRepository = Mocker.Resolve<QualityProfileRepository>();
            Mocker.SetConstant<IQualityProfileRepository>(_profileRepository);

            Mocker.GetMock<ICustomFormatService>()
                .Setup(x => x.All())
                .Returns(new List<CustomFormat>());
        }

        [Test]
        public void should_load_quality_profile()
        {
            var profile = new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(Quality.Bluray1080p, Quality.DVD, Quality.HDTV720p),
                FormatItems = CustomFormatsTestHelpers.GetDefaultFormatItems(),
                MinFormatScore = 0,
                Cutoff = Quality.Bluray1080p.Id,
                Name = "TestProfile"
            };

            _profileRepository.Insert(profile);

            var movie = Builder<Movie>.CreateNew().BuildNew();
            movie.QualityProfileId = profile.Id;

            Subject.Insert(movie);

            Subject.All().Single().QualityProfile.Should().NotBeNull();
        }

        [Test]
        public void movie_queries_should_load_only_the_designated_primary_file()
        {
            var profile = _profileRepository.Insert(new QualityProfile { Name = "Composite", Items = Qualities.QualityFixture.GetDefaultQualities(), Language = Language.Any });
            var metadata = Builder<MovieMetadata>.CreateNew().With(m => m.Id = 0).Build();
            Db.Insert(metadata);
            var movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.MovieMetadataId = metadata.Id).With(m => m.QualityProfileId = profile.Id).With(m => m.MovieFileId = 0).Build();
            Subject.Insert(movie);
            var secondary = new MovieFile { MovieId = movie.Id, RelativePath = "HDR.mkv", Quality = new QualityModel(), Languages = new List<Language>() };
            var primary = new MovieFile { MovieId = movie.Id, RelativePath = "SDR.mkv", Quality = new QualityModel(), Languages = new List<Language>() };
            Db.Insert(secondary);
            Db.Insert(primary);
            movie.MovieFileId = primary.Id;
            Subject.Update(movie);

            Subject.Get(movie.Id).MovieFile.Id.Should().Be(primary.Id);
            Subject.FindByTitles(new List<string> { metadata.CleanTitle }).Single().MovieFile.Id.Should().Be(primary.Id);
            Subject.All().Single().MovieFile.Id.Should().Be(primary.Id);
        }
    }
}
