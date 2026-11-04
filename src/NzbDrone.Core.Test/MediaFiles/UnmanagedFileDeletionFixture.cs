using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class UnmanagedFileDeletionFixture : DbTest<NzbDrone.Core.MediaFiles.MediaFileDeletionService, MovieFile>
    {
        private Movie _movie;
        private MovieFile _managed;
        private MovieFile _unmanaged;

        [SetUp]
        public void Setup()
        {
            _movie = Db.Insert(new Movie { Path = Path.GetFullPath("C:/RetainedMovies/Movie"), QualityProfileId = 1 });
            _managed = Db.Insert(File("Movie - SDR.mkv", false));
            _unmanaged = Db.Insert(File("Movie - HDR.mkv", true));
            _movie.MovieFileId = _managed.Id;
            Db.Update(_movie);
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(_movie.Id)).Returns(_movie);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(_managed.Id)).Returns(_managed);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(_unmanaged.Id)).Returns(_unmanaged);
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(_movie.Id)).Returns(new List<MovieFile> { _managed, _unmanaged });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetParentFolder(It.IsAny<string>())).Returns<string>(Path.GetDirectoryName);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetDirectories(It.IsAny<string>())).Returns(new[] { _movie.Path });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileSize(It.IsAny<string>())).Returns(10);
        }

        private MovieFile File(string path, bool unmanaged)
        {
            return new MovieFile { MovieId = _movie.Id, RelativePath = path, IsUnmanaged = unmanaged, Size = 10, Quality = new QualityModel(), Languages = new List<Language>() };
        }

        [Test]
        public void explicit_delete_should_touch_only_the_selected_unmanaged_copy()
        {
            Subject.DeleteUnmanagedFile(_movie.Id, _unmanaged.Id);

            Mocker.GetMock<IRecycleBinProvider>().Verify(r => r.DeleteFile(Path.Combine(_movie.Path, _unmanaged.RelativePath), It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>().Verify(r => r.DeleteFile(Path.Combine(_movie.Path, _managed.RelativePath), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(_unmanaged, DeleteMediaFileReason.Manual), Times.Once());
            Assert.That(_movie.MovieFileId, Is.EqualTo(_managed.Id));
        }

        [Test]
        public void managed_file_must_not_be_deleted_through_unmanaged_route()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteUnmanagedFile(_movie.Id, _managed.Id));

            Mocker.GetMock<IRecycleBinProvider>().Verify(r => r.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void an_active_download_should_block_physical_deletion()
        {
            Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = 1, DownloadClientId = 1, State = DownloadReservationState.Submitted, SourceTitle = "Active" });

            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteUnmanagedFile(_movie.Id, _unmanaged.Id));

            Mocker.GetMock<IRecycleBinProvider>().Verify(r => r.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }
    }
}
