using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ExistingManagedMetadataFixture : CoreTest<ExistingMetadataImporter>
    {
        private Movie _movie;
        private Mock<IMetadata> _consumer;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, Path = @"C:\Movies\300 (2006)".AsOsAgnostic(), MovieFileId = 11 };
            _consumer = new Mock<IMetadata>();
            Mocker.SetConstant<IEnumerable<IMetadata>>(new[] { _consumer.Object });
            Mocker.GetMock<IExtraFileVersionService>().Setup(v => v.IsManaged(_movie)).Returns(true);
            Mocker.GetMock<IExtraFileService<MetadataFile>>().Setup(v => v.GetFilesByMovie(1)).Returns(new List<MetadataFile>());
        }

        [TestCase("poster.jpg", MetadataType.MovieImage)]
        [TestCase("movie.nfo", MetadataType.MovieMetadata)]
        [TestCase("300 (2006).nfo", MetadataType.MovieMetadata)]
        public void recognized_shared_metadata_should_not_belong_to_primary_file(string filename, MetadataType type)
        {
            var path = Path.Combine(_movie.Path, filename);
            _consumer.Setup(v => v.FindMetadataFile(_movie, path)).Returns(new MetadataFile { MovieId = 1, Type = type, RelativePath = filename });

            var result = Subject.ProcessFiles(_movie, new List<string> { path }, new List<string>(), null).Single();

            Assert.That(result.MovieFileId, Is.Null);
        }

        [Test]
        public void file_specific_metadata_should_belong_to_matched_version()
        {
            var path = Path.Combine(_movie.Path, "300 (2006) - HDR.nfo");
            _consumer.Setup(v => v.FindMetadataFile(_movie, path)).Returns(new MetadataFile { MovieId = 1, Type = MetadataType.MovieMetadata, RelativePath = "300 (2006) - HDR.nfo" });
            Mocker.GetMock<IExtraFileVersionService>().Setup(v => v.Resolve(_movie, path, true)).Returns(new Movie { MovieFileId = 12 });

            Assert.That(Subject.ProcessFiles(_movie, new List<string> { path }, new List<string>(), null).Single().MovieFileId, Is.EqualTo(12));
        }

        [Test]
        public void unrecognized_version_metadata_should_not_be_assigned_by_title()
        {
            var path = Path.Combine(_movie.Path, "300 (2006) - unknown.nfo");
            _consumer.Setup(v => v.FindMetadataFile(_movie, path)).Returns(new MetadataFile { MovieId = 1, Type = MetadataType.MovieMetadata });

            Assert.That(Subject.ProcessFiles(_movie, new List<string> { path }, new List<string>(), null), Is.Empty);
        }
    }
}
