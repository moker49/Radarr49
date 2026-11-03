using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.Qualities;

namespace NzbDrone.Core.Test.MovieTests
{
    [TestFixture]
    public class MovieVersionStatusServiceFixture : CoreTest<MovieVersionStatusService>
    {
        private Movie _movie;
        private List<ResolvedMovieVersion> _versions;
        private List<MovieFile> _files;

        [SetUp]
        public void Setup()
        {
            var format = new CustomFormat { Id = 10, Name = "Format" };
            var sdr = new QualityProfile { Id = 5, Items = QualityFixture.GetDefaultQualities(), Cutoff = Quality.Bluray1080p.Id, UpgradeAllowed = true, CutoffFormatScore = 25, FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = format, Score = 10 } } };
            var hdr = new QualityProfile { Id = 6, Items = QualityFixture.GetDefaultQualities(), Cutoff = Quality.Bluray2160p.Id, UpgradeAllowed = true, CutoffFormatScore = 25, FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = format, Score = 50 } } };
            _movie = new Movie { Id = 1, MovieFileId = 11, QualityProfileId = 99 };
            _versions = new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 7, MovieId = 1, MovieFileId = 11, IsPrimary = true, Label = "SDR", QualityProfile = sdr, Monitored = true },
                new ResolvedMovieVersion { Id = 8, MovieId = 1, MovieFileId = 12, Label = "HDR", QualityProfile = hdr, Monitored = true }
            };
            _files = new List<MovieFile>
            {
                new MovieFile { Id = 11, MovieId = 1, RelativePath = "SDR.mkv", Quality = new QualityModel(Quality.Bluray1080p), Size = 100 },
                new MovieFile { Id = 12, MovieId = 1, RelativePath = "HDR.mkv", Quality = new QualityModel(Quality.Bluray1080p), Size = 200 }
            };
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_movie)).Returns(_versions);
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesByMovie(1)).Returns(_files);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(v => v.ParseCustomFormat(It.IsAny<MovieFile>(), It.IsAny<Movie>())).Returns(new List<CustomFormat> { format });
            Mocker.SetConstant<IUpgradableSpecification>(Mocker.Resolve<UpgradableSpecification>());
        }

        [Test]
        public void should_calculate_each_file_against_its_own_quality_and_format_profile()
        {
            var statuses = Subject.Get(_movie);
            Assert.That(statuses.Select(s => s.HasFile), Is.EqualTo(new[] { true, true }));
            Assert.That(statuses.Select(s => s.QualityCutoffNotMet), Is.EqualTo(new[] { false, true }));
            Assert.That(statuses.Select(s => s.CustomFormatScore), Is.EqualTo(new[] { 10, 50 }));
            Assert.That(statuses[0].CutoffNotMet, Is.True);
            Mocker.GetMock<ICustomFormatCalculationService>().Verify(v => v.ParseCustomFormat(_files[1], It.Is<Movie>(m => m.ManagedVersion.Id == 8 && m.QualityProfileId == 99 && m.QualityProfile.Id == 6 && m.MovieFileId == 12)), Times.Once());
            Mocker.GetMock<IMovieVersionService>().Verify(v => v.ForVersion(It.IsAny<Movie>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void child_meeting_quality_and_custom_format_cutoffs_should_report_satisfied()
        {
            _files[1].Quality = new QualityModel(Quality.Bluray2160p);
            var hdr = Subject.Get(_movie)[1];
            Assert.That(hdr.QualityCutoffNotMet, Is.False);
            Assert.That(hdr.CutoffNotMet, Is.False);
            Assert.That(hdr.File.Id, Is.EqualTo(12));
        }

        [TestCase(null)]
        [TestCase(99)]
        public void missing_child_should_not_inherit_primary_file_or_cutoff(int? fileId)
        {
            _versions[1].MovieFileId = fileId;
            var hdr = Subject.Get(_movie)[1];
            Assert.That(hdr.HasFile, Is.False);
            Assert.That(hdr.File, Is.Null);
            Assert.That(hdr.QualityCutoffNotMet, Is.False);
            Assert.That(hdr.Error != null, Is.EqualTo(fileId.HasValue));
        }

        [Test]
        public void foreign_file_owner_should_be_reported_as_invalid()
        {
            _files[1].MovieId = 2;
            var hdr = Subject.Get(_movie)[1];
            Assert.That(hdr.HasFile, Is.False);
            Assert.That(hdr.Error, Is.Not.Null);
        }

        [Test]
        public void ordinary_movie_should_keep_implicit_version_status_without_assignments()
        {
            _versions.RemoveAt(1);
            _versions[0].Id = null;
            var status = Subject.Get(_movie).Single();
            Assert.That(status.Version.Id, Is.Null);
            Assert.That(status.HasFile, Is.True);
            Assert.That(status.File.Id, Is.EqualTo(11));
            Mocker.GetMock<IMediaFileService>().Verify(v => v.GetFilesByMovie(1), Times.Once());
        }
    }
}
