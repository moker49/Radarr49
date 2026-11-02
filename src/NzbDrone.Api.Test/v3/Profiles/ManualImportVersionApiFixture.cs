using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MovieImport.Manual;
using NzbDrone.Core.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.ManualImport;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class ManualImportVersionApiFixture : TestBase<ManualImportController>
    {
        [Test]
        public void preview_should_forward_explicit_version_and_bound_download_identity()
        {
            Mocker.GetMock<IManualImportService>().Setup(v => v.GetMediaFiles("folder", "job", 1, true, It.IsAny<ManualImportVersionSelection>())).Returns(new List<ManualImportItem>());

            Subject.GetMediaFiles("folder", "job", 1, true, 7, 2, 3);

            Mocker.GetMock<IManualImportService>().Verify(v => v.GetMediaFiles("folder", "job", 1, true, It.Is<ManualImportVersionSelection>(s => s.MovieVersionId == 7 && s.DownloadClientId == 2 && s.DownloadReservationId == 3)), Times.Once());
        }

        [Test]
        public void explicit_external_folder_preview_should_not_be_replaced_by_library_listing()
        {
            Mocker.GetMock<IManualImportService>().Setup(v => v.GetMediaFiles("folder", null, 1, true, It.IsAny<ManualImportVersionSelection>())).Returns(new List<ManualImportItem>());

            Subject.GetMediaFiles("folder", null, 1, true, 7);

            Mocker.GetMock<IManualImportService>().Verify(v => v.GetMediaFiles("folder", null, 1, true, It.Is<ManualImportVersionSelection>(s => s.MovieVersionId == 7)), Times.Once());
            Mocker.GetMock<IManualImportService>().Verify(v => v.GetMediaFiles(1), Times.Never());
        }

        [Test]
        public void resource_should_expose_managed_identity_without_changing_legacy_fields()
        {
            var item = new ManualImportItem { Path = "movie.mkv", MovieVersionId = 7, MovieFileId = 11, DownloadClientId = 2, DownloadReservationId = 3, DownloadId = "job" };

            var resource = item.ToResource();

            Assert.That(resource.MovieVersionId, Is.EqualTo(7));
            Assert.That(resource.MovieFileId, Is.EqualTo(11));
            Assert.That(resource.DownloadClientId, Is.EqualTo(2));
            Assert.That(resource.DownloadReservationId, Is.EqualTo(3));
            Assert.That(resource.DownloadId, Is.EqualTo("job"));
        }

        [Test]
        public void ordinary_resource_should_leave_version_identity_absent()
        {
            var resource = new ManualImportItem { Path = "movie.mkv" }.ToResource();
            Assert.That(resource.MovieVersionId, Is.Null);
            Assert.That(resource.DownloadReservationId, Is.Null);
        }

        [Test]
        public void reprocessing_should_preserve_selected_identity_in_response()
        {
            var request = new ManualImportReprocessResource
            {
                Path = "movie.mkv", MovieId = 1, MovieVersionId = 7, DownloadId = "job", DownloadClientId = 2, DownloadReservationId = 3,
                ReleaseGroup = "group", Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English }
            };
            Mocker.GetMock<IManualImportService>().Setup(v => v.ReprocessItem("movie.mkv", "job", 1, "group", request.Quality, request.Languages, 0, It.Is<ManualImportVersionSelection>(s => s.MovieVersionId == 7 && s.DownloadClientId == 2 && s.DownloadReservationId == 3)))
                .Returns(new ManualImportItem { MovieVersionId = 7, DownloadClientId = 2, DownloadReservationId = 3 });

            Subject.ReprocessItems(new List<ManualImportReprocessResource> { request });

            Assert.That(request.MovieVersionId, Is.EqualTo(7));
            Assert.That(request.DownloadClientId, Is.EqualTo(2));
            Assert.That(request.DownloadReservationId, Is.EqualTo(3));
        }

        [Test]
        public void library_preview_should_filter_by_requested_version()
        {
            Mocker.GetMock<IManualImportService>().Setup(v => v.GetMediaFiles(1)).Returns(new List<ManualImportItem>
            {
                new ManualImportItem { Path = "sdr.mkv", MovieVersionId = 7 },
                new ManualImportItem { Path = "hdr.mkv", MovieVersionId = 8 }
            });

            Assert.That(Subject.GetMediaFiles(null, null, 1, true, 8).Single().MovieVersionId, Is.EqualTo(8));
        }
    }
}
