using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.MovieFiles;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class MovieFileVersionIdentityFixture : TestBase<MovieFileController>
    {
        [Test]
        public void file_list_must_return_each_persisted_version_identity_without_guessing_unassigned_files()
        {
            var files = new List<MovieFile>
            {
                new MovieFile { Id = 7, MovieId = 1, RelativePath = "SDR.mkv", ManagementProfileId = 4 },
                new MovieFile { Id = 8, MovieId = 1, RelativePath = "HDR.mkv", ManagementProfileId = 5 },
                new MovieFile { Id = 9, MovieId = 1, RelativePath = "Unassigned.mkv" }
            };
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovies(It.IsAny<IEnumerable<int>>())).Returns(files);
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(1)).Returns(new Movie { Id = 1, Path = "C:/Movies/Test", QualityProfile = new QualityProfile() });
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(s => s.ParseCustomFormat(It.IsAny<MovieFile>(), It.IsAny<Movie>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.ForMovie(1)).Returns(new List<MovieVersion>
            {
                new MovieVersion { Id = 201, MovieId = 1, MovieFileId = 7 },
                new MovieVersion { Id = 202, MovieId = 1, MovieFileId = 8 }
            });

            var resources = Subject.GetMovieFiles(new List<int> { 1 }, new List<int>());

            Assert.That(resources.Select(f => f.MovieVersionId), Is.EqualTo(new int?[] { 201, 202, null }));
            Assert.That(resources.Select(f => f.ManagementProfileId), Is.EqualTo(new int?[] { 4, 5, null }));
            Mocker.GetMock<IMovieVersionRepository>().Verify(s => s.ForMovie(1), Times.Once());
        }
    }
}
