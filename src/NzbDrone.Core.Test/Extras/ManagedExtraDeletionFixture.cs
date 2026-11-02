using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ManagedExtraDeletionFixture : CoreTest<SubtitleFileService>
    {
        private MovieFile _file;
        private List<SubtitleFile> _extras;

        [SetUp]
        public void Setup()
        {
            _file = new MovieFile { Id = 11, MovieId = 1, MovieVersionId = 7 };
            var movie = new Movie { Id = 1, Path = @"C:\Movies\300 (2006)".AsOsAgnostic() };
            _extras = new List<SubtitleFile> { new SubtitleFile { Id = 1, MovieId = 1, MovieFileId = 11, RelativePath = "SDR.en.srt" } };
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(1)).Returns(movie);
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Setup(v => v.GetFilesByMovieFile(11)).Returns(_extras);
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Setup(v => v.GetFilesByMovie(1)).Returns(() => _extras);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetParentFolder(It.IsAny<string>())).Returns<string>(Path.GetDirectoryName);
        }

        [Test]
        public void delayed_upgrade_cleanup_should_not_delete_a_replacement_subtitle_at_the_same_path()
        {
            _extras.Add(new SubtitleFile { Id = 2, MovieId = 1, MovieFileId = 12, RelativePath = "SDR.en.srt" });
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Setup(v => v.GetFilesByMovieFile(11)).Returns(new List<SubtitleFile> { _extras[0] });

            Subject.HandleAsync(new MovieFileDeletedEvent(_file, DeleteMediaFileReason.Upgrade));

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Verify(v => v.DeleteForMovieFile(11), Times.Once());
        }

        [Test]
        public void metadata_refresh_should_not_delete_existing_sidecars()
        {
            Subject.HandleAsync(new MovieFileDeletedEvent(_file, DeleteMediaFileReason.ManualOverride));

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void normal_cleanup_should_delete_only_the_removed_files_extras()
        {
            Subject.HandleAsync(new MovieFileDeletedEvent(_file, DeleteMediaFileReason.MissingFromDisk));

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Verify(v => v.DeleteForMovieFile(11), Times.Once());
        }
    }
}
