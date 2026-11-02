using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.DiskScanServiceTests
{
    [TestFixture]
    public class ManagedScanPersistenceFixture : DbTest<DiskScanService, MovieFile>
    {
        private Movie _movie;
        private MovieVersion _sdr;
        private MovieVersion _hdr;
        private MovieFile _sdrFile;
        private MovieFile _hdrFile;
        private List<string> _diskFiles;

        [SetUp]
        public void Setup()
        {
            var metadata = Db.Insert(Builder<MovieMetadata>.CreateNew().With(m => m.Id = 0).Build());
            _movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = 10)
                .With(m => m.MovieMetadataId = metadata.Id).With(m => m.MovieFileId = 0).With(m => m.Path = @"C:\Movies\300 (2006)".AsOsAgnostic()).Build();
            Db.Insert(_movie);
            var sdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true });
            var hdr = Db.Insert(new CompositeProfileVersion { CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 });
            _sdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = sdr.Id, Monitored = true });
            _hdr = Db.Insert(new MovieVersion { MovieId = _movie.Id, CompositeProfileVersionId = hdr.Id, Monitored = true });

            Mocker.SetConstant<ICompositeProfileVersionRepository>(Mocker.Resolve<CompositeProfileVersionRepository>());
            Mocker.SetConstant<IMovieVersionRepository>(Mocker.Resolve<MovieVersionRepository>());
            var files = Mocker.Resolve<MediaFileRepository>();
            Mocker.SetConstant<IMediaFileRepository>(files);
            Mocker.SetConstant<IMediaFileService>(Mocker.Resolve<MediaFileService>());
            Mocker.SetConstant<IMovieVersionService>(Mocker.Resolve<MovieVersionService>());
            Mocker.SetConstant<IMediaFileTableCleanupService>(Mocker.Resolve<MediaFileTableCleanupService>());
            Mocker.GetMock<IQualityProfileService>().Setup(v => v.Get(It.IsAny<int>())).Returns<int>(id => new QualityProfile { Id = id });
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(_movie.Id)).Returns(() => Db.Single<Movie>());
            Mocker.GetMock<IMovieRepository>().Setup(v => v.Get(_movie.Id)).Returns(() => Db.Single<Movie>());
            _sdrFile = files.InsertForVersion(File("300 (2006) - SDR.mkv"), _sdr.Id);
            _hdrFile = files.InsertForVersion(File("300 (2006) - HDR.mkv"), _hdr.Id);
            _diskFiles = new List<string> { Path.Combine(_movie.Path, _sdrFile.RelativePath), Path.Combine(_movie.Path, _hdrFile.RelativePath) };
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFiles(_movie.Path, true)).Returns(() => _diskFiles.ToArray());
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFileSize(It.IsAny<string>())).Returns(1);
            Mocker.GetMock<IRootFolderService>().Setup(v => v.GetBestRootFolderPath(_movie.Path, null)).Returns(Path.GetDirectoryName(_movie.Path));
            Mocker.GetMock<IMakeImportDecision>().Setup(v => v.GetImportDecisions(It.IsAny<List<string>>(), It.IsAny<Movie>(), false)).Returns(new List<ImportDecision>());
        }

        private MovieFile File(string relativePath)
        {
            return new MovieFile { MovieId = _movie.Id, RelativePath = relativePath, Quality = new QualityModel(), Languages = new List<Language>(), Size = 1 };
        }

        [Test]
        public void repeated_scans_should_preserve_both_persisted_assignments_and_primary_summary()
        {
            Subject.Scan(_movie);
            Subject.Scan(_movie);

            Assert.That(AllStoredModels.Count, Is.EqualTo(2));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).MovieFileId, Is.EqualTo(_sdrFile.Id));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _hdr.Id).MovieFileId, Is.EqualTo(_hdrFile.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_sdrFile.Id));
        }

        [Test]
        public void missing_secondary_should_detach_only_its_file_and_preserve_primary()
        {
            _diskFiles.Remove(Path.Combine(_movie.Path, _hdrFile.RelativePath));

            Subject.Scan(_movie);

            Assert.That(AllStoredModels.Single().Id, Is.EqualTo(_sdrFile.Id));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _hdr.Id).MovieFileId, Is.Null);
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == _sdr.Id).MovieFileId, Is.EqualTo(_sdrFile.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_sdrFile.Id));
        }
    }
}
