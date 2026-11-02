using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class MovieVersionFileDeletionServiceFixture : CoreTest<MovieVersionFileDeletionService>
    {
        private Movie _movie;
        private MovieFile _file;
        private List<MovieFile> _files;
        private List<DownloadReservation> _reservations;
        private List<TrackedDownload> _tracked;

        [SetUp]
        public void Setup()
        {
            _file = new MovieFile { Id = 11, MovieId = 1, RelativePath = "300 (2006) - SDR.mkv" };
            _movie = new Movie
            {
                Id = 1, Path = @"C:\Movies\300 (2006)".AsOsAgnostic(), MovieFileId = 11, MovieFile = _file,
                ManagedVersion = new ResolvedMovieVersion { Id = 7, MovieFileId = 11, IsPrimary = true }
            };
            _files = new List<MovieFile> { _file, new MovieFile { Id = 12, MovieId = 1, RelativePath = "300 (2006) - HDR.mkv" } };
            _reservations = new List<DownloadReservation>();
            _tracked = new List<TrackedDownload>();
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(1)).Returns(_movie);
            Mocker.GetMock<IMovieService>().Setup(v => v.AllMoviePaths()).Returns(new Dictionary<int, string> { { 1, _movie.Path } });
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.ForVersion(_movie, It.IsAny<int>())).Returns(_movie);
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesByMovie(1)).Returns(_files);
            Mocker.GetMock<IDownloadReservationRepository>().Setup(v => v.ForMovie(1)).Returns(_reservations);
            Mocker.GetMock<ITrackedDownloadService>().Setup(v => v.GetTrackedDownloads()).Returns(_tracked);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetDirectories(It.IsAny<string>())).Returns(new[] { _movie.Path });
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileExists(It.IsAny<string>())).Returns(true);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_delete_only_the_exact_file_and_apply_explicit_monitoring_choice(bool unmonitor)
        {
            Subject.Delete(1, 7, 11, unmonitor);

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(Path.Combine(_movie.Path, _file.RelativePath), It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.DeleteManagedVersionFile(_file, 7, unmonitor), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
        }

        [TestCase(8, 11)]
        [TestCase(7, 12)]
        public void stale_or_conflicting_identity_should_not_touch_files(int version, int file)
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(1, version, file, true));
            AssertNoDeletion();
        }

        [TestCase(DownloadReservationState.Prepared)]
        [TestCase(DownloadReservationState.Dispatching)]
        [TestCase(DownloadReservationState.Submitted)]
        [TestCase(DownloadReservationState.Uncertain)]
        public void active_reservation_should_prevent_physical_deletion(DownloadReservationState state)
        {
            _reservations.Add(new DownloadReservation { MovieVersionId = 7, State = state });
            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(1, 7, 11, true));
            AssertNoDeletion();
        }

        [Test]
        public void another_versions_active_download_should_not_block_targeted_deletion()
        {
            _reservations.Add(new DownloadReservation { MovieVersionId = 8, State = DownloadReservationState.Submitted });
            _tracked.Add(new TrackedDownload { MovieVersionId = 8, IsTrackable = true, RemoteMovie = new RemoteMovie { Movie = _movie } });

            Assert.DoesNotThrow(() => Subject.Delete(1, 7, 11, true));
            Mocker.GetMock<IMediaFileService>().Verify(v => v.DeleteManagedVersionFile(_file, 7, true), Times.Once());
        }

        [TestCase(null)]
        [TestCase(7)]
        public void active_tracked_target_or_unresolved_identity_should_block_deletion(int? version)
        {
            _tracked.Add(new TrackedDownload { MovieVersionId = version, IsTrackable = true, RemoteMovie = new RemoteMovie { Movie = _movie } });
            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(1, 7, 11, true));
            AssertNoDeletion();
        }

        [TestCase("../outside.mkv")]
        [TestCase("C:\\outside.mkv")]
        public void file_path_outside_movie_folder_should_be_rejected(string relative)
        {
            _file.RelativePath = relative.AsOsAgnostic();
            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(1, 7, 11, true));
            AssertNoDeletion();
        }

        [Test]
        public void shared_registered_path_should_not_be_recycled()
        {
            _files[1].RelativePath = _file.RelativePath;
            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(1, 7, 11, true));
            AssertNoDeletion();
        }

        [Test]
        public void overlapping_movie_folder_should_not_be_recycled()
        {
            Mocker.GetMock<IMovieService>().Setup(v => v.AllMoviePaths()).Returns(new Dictionary<int, string> { { 2, _movie.Path } });
            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(1, 7, 11, true));
            AssertNoDeletion();
        }

        [Test]
        public void unavailable_root_should_not_detach_file_ownership()
        {
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns(false);
            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(1, 7, 11, true));
            AssertNoDeletion();
        }

        [Test]
        public void already_missing_file_can_be_detached_when_the_root_is_available()
        {
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileExists(It.IsAny<string>())).Returns(false);
            Subject.Delete(1, 7, 11, false);
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.DeleteManagedVersionFile(_file, 7, false), Times.Once());
        }

        [Test]
        public void trailing_folder_separator_should_use_the_actual_parent_root()
        {
            var root = Path.GetDirectoryName(_movie.Path);
            _movie.Path += Path.DirectorySeparatorChar;

            Subject.Delete(1, 7, 11, true);

            Mocker.GetMock<IDiskProvider>().Verify(v => v.FolderExists(root), Times.Once());
        }

        [Test]
        public void recycle_failure_should_preserve_database_ownership()
        {
            Mocker.GetMock<IRecycleBinProvider>().Setup(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("Recycle failed"));
            Assert.Throws<IOException>(() => Subject.Delete(1, 7, 11, true));
            Mocker.GetMock<IMediaFileService>().Verify(v => v.DeleteManagedVersionFile(It.IsAny<MovieFile>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        }

        private void AssertNoDeletion()
        {
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.DeleteManagedVersionFile(It.IsAny<MovieFile>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
