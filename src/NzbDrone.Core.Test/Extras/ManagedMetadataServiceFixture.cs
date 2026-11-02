using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ManagedMetadataServiceFixture : CoreTest<MetadataService>
    {
        private Movie _movie;
        private MovieFile _file;
        private Mock<IMetadata> _consumer;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, MovieFileId = 11, Path = @"C:\Movies\300 (2006)".AsOsAgnostic() };
            _file = new MovieFile { Id = 11, MovieId = 1, RelativePath = "300 (2006) - SDR.mkv" };
            _consumer = new Mock<IMetadata>();
            _consumer.Setup(v => v.MovieMetadata(_movie, It.IsAny<MovieFile>())).Returns(new MetadataFileResult("movie.nfo", "contents"));
            Mocker.GetMock<IMetadataFactory>().Setup(v => v.Enabled()).Returns(new List<IMetadata> { _consumer.Object });
            Mocker.GetMock<IExtraFileVersionService>().Setup(v => v.IsManaged(_movie)).Returns(true);
            Mocker.GetMock<IMetadataFileService>().Setup(v => v.GetFilesByMovie(1)).Returns(new List<MetadataFile>());
        }

        [Test]
        public void shared_nfo_should_have_no_file_owner()
        {
            var result = Subject.CreateAfterMovieImport(_movie, _file).Single();

            Assert.That(result.MovieFileId, Is.Null);
            Mocker.GetMock<IDiskProvider>().Verify(v => v.WriteAllText(Path.Combine(_movie.Path, "movie.nfo"), "contents"), Times.Once());
        }

        [Test]
        public void secondary_should_not_overwrite_shared_primary_metadata()
        {
            _file.Id = 12;
            _file.RelativePath = "300 (2006) - HDR.mkv";
            _movie.ManagedVersion = new ResolvedMovieVersion { Id = 8, IsPrimary = false };

            Assert.That(Subject.CreateAfterMovieImport(_movie, _file), Is.Empty);
            Mocker.GetMock<IDiskProvider>().Verify(v => v.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void file_specific_nfo_should_keep_its_version_file_owner()
        {
            _file.Id = 12;
            _file.RelativePath = "300 (2006) - HDR.mkv";
            _consumer.Setup(v => v.MovieMetadata(_movie, _file)).Returns(new MetadataFileResult("300 (2006) - HDR.nfo", "hdr contents"));

            Assert.That(Subject.CreateAfterMovieImport(_movie, _file).Single().MovieFileId, Is.EqualTo(12));
        }

        [Test]
        public void shared_metadata_rewrite_should_reuse_its_record()
        {
            var existing = new MetadataFile { Id = 90, MovieId = 1, MovieFileId = null, RelativePath = "movie.nfo", Type = MetadataType.MovieMetadata, Consumer = _consumer.Object.GetType().Name };
            Mocker.GetMock<IMetadataFileService>().Setup(v => v.GetFilesByMovie(1)).Returns(new List<MetadataFile> { existing });

            Assert.That(Subject.CreateAfterMovieImport(_movie, _file).Single().Id, Is.EqualTo(90));
        }
    }
}
