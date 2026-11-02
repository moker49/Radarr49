using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Manual;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]
    public class ManualImportVersionServiceFixture : CoreTest<ManualImportVersionService>
    {
        private LocalMovie _local;
        private ManualImportVersionSelection _selection;
        private TrackedDownload _download;

        [SetUp]
        public void Setup()
        {
            var folder = @"C:\Downloads\job".AsOsAgnostic();
            _local = new LocalMovie { Movie = new Movie { Id = 1 }, Path = Path.Combine(folder, "300.mkv") };
            _selection = new ManualImportVersionSelection { MovieVersionId = 7, DownloadClientId = 2, DownloadReservationId = 3 };
            _download = new TrackedDownload
            {
                DownloadClient = 2,
                MovieVersionId = 7,
                DownloadReservationId = 3,
                RemoteMovie = new RemoteMovie { Movie = _local.Movie },
                DownloadItem = new DownloadClientItem { DownloadId = "job", DownloadClientInfo = new DownloadClientItemClientInfo { Id = 2 }, OutputPath = new OsPath(folder) }
            };
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_local.Movie)).Returns(new List<ResolvedMovieVersion> { new ResolvedMovieVersion { Id = 7 } });
            Mocker.GetMock<ITrackedDownloadService>().Setup(v => v.GetTrackedDownloads()).Returns(new List<TrackedDownload> { _download });
            Mocker.GetMock<IDownloadReservationRepository>().Setup(v => v.ForMovie(1)).Returns(new List<DownloadReservation>());
        }

        [Test]
        public void valid_selection_should_preserve_version_client_and_reservation()
        {
            Assert.That(Subject.Prepare(_local, "job", _selection), Is.SameAs(_download));
            Assert.That(_local.MovieVersionId, Is.EqualTo(7));
            Assert.That(_local.DownloadReservationId, Is.EqualTo(3));
            Mocker.GetMock<IMovieVersionImportService>().Verify(v => v.Prepare(_local, _download.DownloadItem), Times.Once());
        }

        [Test]
        public void legacy_selection_should_not_choose_a_managed_version()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", null));
        }

        [TestCase(null, 3)]
        [TestCase(2, null)]
        public void managed_download_requires_client_and_reservation(int? client, int? reservation)
        {
            _selection.DownloadClientId = client;
            _selection.DownloadReservationId = reservation;
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", _selection));
        }

        [TestCase(8, 3)]
        [TestCase(7, 4)]
        public void conflicting_tracked_identity_should_be_rejected(int version, int reservation)
        {
            _download.MovieVersionId = version;
            _download.DownloadReservationId = reservation;
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", _selection));
        }

        [Test]
        public void same_download_id_on_another_client_should_not_be_selected()
        {
            _download.DownloadClient = 4;
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", _selection));
        }

        [Test]
        public void foreign_movie_should_be_rejected()
        {
            _download.RemoteMovie.Movie = new Movie { Id = 9 };
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", _selection));
        }

        [Test]
        public void source_outside_bound_download_path_should_be_rejected()
        {
            _local.Path = @"C:\Downloads\another-job\300.mkv".AsOsAgnostic();
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", _selection));
        }

        [Test]
        public void path_traversal_outside_bound_download_should_be_rejected()
        {
            _local.Path = Path.Combine(_download.DownloadItem.OutputPath.FullPath, "..", "another-job", "300.mkv");
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", _selection));
        }

        [Test]
        public void offline_selection_should_not_hijack_active_reserved_download()
        {
            Mocker.GetMock<ITrackedDownloadService>().Setup(v => v.GetTrackedDownloads()).Returns(new List<TrackedDownload>());
            _selection.DownloadClientId = null;
            _selection.DownloadReservationId = null;
            Mocker.GetMock<IDownloadReservationRepository>().Setup(v => v.ForMovie(1)).Returns(new List<DownloadReservation>
            {
                new DownloadReservation { MovieVersionId = 7, State = DownloadReservationState.Submitted }
            });
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, null, _selection));
        }

        [Test]
        public void offline_explicit_selection_without_active_job_should_pass()
        {
            Mocker.GetMock<ITrackedDownloadService>().Setup(v => v.GetTrackedDownloads()).Returns(new List<TrackedDownload>());
            _selection.DownloadClientId = null;
            _selection.DownloadReservationId = null;
            Assert.That(Subject.Prepare(_local, null, _selection), Is.Null);
            Mocker.GetMock<IMovieVersionImportService>().Verify(v => v.Prepare(_local, null), Times.Once());
        }

        [Test]
        public void ordinary_selection_should_preserve_legacy_tracking()
        {
            _download.MovieVersionId = null;
            _download.DownloadReservationId = null;
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_local.Movie)).Returns(new List<ResolvedMovieVersion> { new ResolvedMovieVersion() });
            Mocker.GetMock<ITrackedDownloadService>().Setup(v => v.Find("job")).Returns(_download);
            Assert.That(Subject.Prepare(_local, "job", null), Is.SameAs(_download));
        }

        [Test]
        public void ordinary_target_should_not_strip_managed_download_identity()
        {
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_local.Movie)).Returns(new List<ResolvedMovieVersion> { new ResolvedMovieVersion() });
            Mocker.GetMock<ITrackedDownloadService>().Setup(v => v.Find("job")).Returns(_download);

            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, "job", null));
        }

        [Test]
        public void dropping_download_id_should_not_convert_reserved_source_to_an_offline_import()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.Prepare(_local, null, new ManualImportVersionSelection { MovieVersionId = 7 }));
        }
    }
}
