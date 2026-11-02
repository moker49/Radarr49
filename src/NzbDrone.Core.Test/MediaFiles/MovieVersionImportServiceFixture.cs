using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class MovieVersionImportServiceFixture : CoreTest<MovieVersionImportService>
    {
        private Movie _movie;
        private LocalMovie _local;
        private DownloadClientItem _download;
        private DownloadReservation _reservation;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie
            {
                Id = 1,
                Path = @"C:\Movies\300 (2006)".AsOsAgnostic(),
                QualityProfile = new QualityProfile
                {
                    Language = Language.Any,
                    Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.Bluray1080p, Allowed = true } }
                },
                ManagedVersion = new ResolvedMovieVersion { Id = 7, Label = "HDR" }
            };
            _local = new LocalMovie
            {
                Movie = _movie,
                MovieVersionId = 7,
                Path = @"C:\Downloads\300.mkv".AsOsAgnostic(),
                Quality = new QualityModel(Quality.Bluray1080p)
            };
            _download = new DownloadClientItem { DownloadId = "job", DownloadClientInfo = new DownloadClientItemClientInfo { Id = 2 } };
            _reservation = new DownloadReservation { Id = 3, MovieId = 1, MovieVersionId = 7, State = DownloadReservationState.Submitted };
            Mocker.GetMock<IMovieRepository>().Setup(v => v.Get(1)).Returns(_movie);
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_movie)).Returns(new List<ResolvedMovieVersion> { _movie.ManagedVersion });
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.ForVersion(_movie, 7)).Returns(_movie);
            Mocker.GetMock<IDownloadReservationRepository>().Setup(v => v.FindForDownload(2, "job")).Returns(new List<DownloadReservation> { _reservation });
            Mocker.GetMock<IDownloadReservationRepository>().Setup(v => v.Get(3)).Returns(_reservation);
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFileSize(_local.Path)).Returns(1024 * 1024 * 1024);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(v => v.ParseCustomFormat(_local)).Returns(new List<CustomFormat>());
            Mocker.GetMock<IQualityDefinitionService>().Setup(v => v.Get(Quality.Bluray1080p)).Returns(new QualityDefinition { Quality = Quality.Bluray1080p });
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesWithRelativePath(1, It.IsAny<string>())).Returns(new List<MovieFile>());
        }

        [Test]
        public void should_bind_client_import_to_its_reserved_version()
        {
            _local.MovieVersionId = null;
            _movie.ManagedVersion = null;
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_movie)).Returns(new List<ResolvedMovieVersion> { new ResolvedMovieVersion { Id = 7 } });

            Subject.Prepare(_local, _download);

            Assert.That(_local.MovieVersionId, Is.EqualTo(7));
            Assert.That(_local.DownloadReservationId, Is.EqualTo(3));
        }

        [TestCase(2, 7)]
        [TestCase(1, 8)]
        public void conflicting_reservation_should_be_rejected(int movieId, int versionId)
        {
            _reservation.MovieId = movieId;
            _reservation.MovieVersionId = versionId;
            Assert.Throws<MovieVersionImportException>(() => Subject.Prepare(_local, _download));
        }

        [Test]
        public void unassigned_existing_file_should_require_explicit_version()
        {
            _local.MovieVersionId = null;
            _movie.ManagedVersion = null;
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_movie)).Returns(new List<ResolvedMovieVersion> { new ResolvedMovieVersion { Id = 7 } });
            Assert.Throws<MovieVersionImportException>(() => Subject.Prepare(_local, null));
        }

        [Test]
        public void occupied_destination_should_be_rejected_before_write()
        {
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FileExists(Path.Combine(_movie.Path, "300 (2006) - HDR.mkv"))).Returns(true);
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void other_versions_registered_path_should_be_rejected()
        {
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesWithRelativePath(1, It.IsAny<string>())).Returns(new List<MovieFile> { new MovieFile { Id = 99 } });
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void empty_source_should_be_rejected()
        {
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFileSize(_local.Path)).Returns(0);
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void file_must_satisfy_child_custom_format_threshold()
        {
            _movie.QualityProfile.MinFormatScore = 100;
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void file_must_satisfy_child_size_settings()
        {
            _movie.QualityProfile.SizeDefinitions = new List<ProfileSizeDefinition>
            {
                new ProfileSizeDefinition { QualityId = Quality.Bluray1080p.Id, MinSize = 100 }
            };

            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void unsafe_version_label_should_be_rejected()
        {
            _movie.ManagedVersion.Label = "../HDR";
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void existing_file_inside_movie_folder_can_be_registered_explicitly()
        {
            _local.Path = Path.Combine(_movie.Path, "..hidden.mkv");
            Mocker.GetMock<IDiskProvider>().Setup(v => v.GetFileSize(_local.Path)).Returns(1024 * 1024 * 1024);

            Assert.DoesNotThrow(() => Subject.ValidateForWrite(_local, null, false));
        }

        [Test]
        public void completed_reservation_should_not_be_reimported()
        {
            _reservation.State = DownloadReservationState.Imported;
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void managed_script_import_should_be_rejected()
        {
            Mocker.GetMock<IConfigService>().SetupGet(v => v.UseScriptImport).Returns(true);
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void valid_reserved_import_should_pass()
        {
            Assert.DoesNotThrow(() => Subject.ValidateForWrite(_local, _download, true));
        }

        [Test]
        public void existing_file_outside_movie_folder_should_be_rejected()
        {
            Assert.Throws<MovieVersionImportException>(() => Subject.ValidateForWrite(_local, null, false));
        }
    }
}
