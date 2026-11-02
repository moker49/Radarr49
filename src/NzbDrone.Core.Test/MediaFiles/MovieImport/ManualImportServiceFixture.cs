using System.Collections.Generic;
using System.IO;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.MediaFiles.MovieImport.Manual;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]
    public class ManualImportServiceFixture : CoreTest<ManualImportService>
    {
        private Movie _movie;
        private ManualImportFile _file;
        private TrackedDownload _download;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, Path = @"C:\Movies\300 (2006)".AsOsAgnostic(), QualityProfile = new QualityProfile() };
            _file = new ManualImportFile
            {
                MovieId = 1, MovieVersionId = 7, DownloadClientId = 2, DownloadReservationId = 3, DownloadId = "job",
                Path = @"C:\Downloads\job\300.2006.mkv".AsOsAgnostic(), Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English }
            };
            _download = new TrackedDownload
            {
                DownloadClient = 2, MovieVersionId = 7, DownloadReservationId = 3, RemoteMovie = new RemoteMovie { Movie = _movie },
                DownloadItem = new DownloadClientItem { DownloadId = "job", CanMoveFiles = true, OutputPath = new OsPath(Path.GetDirectoryName(_file.Path)) }
            };
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(1)).Returns(_movie);
            Mocker.GetMock<IManualImportVersionService>().Setup(v => v.Prepare(It.IsAny<LocalMovie>(), "job", _file))
                .Callback<LocalMovie, string, ManualImportVersionSelection>((local, id, selection) =>
                {
                    local.MovieVersionId = selection.MovieVersionId;
                    local.DownloadReservationId = selection.DownloadReservationId;
                }).Returns(_download);
            Mocker.GetMock<IAggregationService>().Setup(v => v.Augment(It.IsAny<LocalMovie>(), It.IsAny<DownloadClientItem>())).Returns<LocalMovie, DownloadClientItem>((local, item) => local);
            Mocker.GetMock<ICustomFormatCalculationService>().Setup(v => v.ParseCustomFormat(It.IsAny<LocalMovie>())).Returns(new List<CustomFormat>());
            Mocker.GetMock<IImportApprovedMovie>().Setup(v => v.Import(It.IsAny<List<ImportDecision>>(), true, _download.DownloadItem, ImportMode.Auto))
                .Returns<List<ImportDecision>, bool, DownloadClientItem, ImportMode>((decisions, fresh, item, mode) => new List<ImportResult> { new ImportResult(decisions[0]) });
        }

        [Test]
        public void command_should_pass_selected_identity_and_client_item_to_importer()
        {
            Subject.Execute(new ManualImportCommand { Files = new List<ManualImportFile> { _file } });

            Mocker.GetMock<IImportApprovedMovie>().Verify(v => v.Import(It.Is<List<ImportDecision>>(d => d[0].LocalMovie.MovieVersionId == 7 && d[0].LocalMovie.DownloadReservationId == 3), true, _download.DownloadItem, ImportMode.Auto), Times.Once());
        }

        [Test]
        public void unverified_import_should_not_complete_or_delete_download_folder()
        {
            Mocker.GetMock<IDiskProvider>().Setup(v => v.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDownloadedMovieImportService>().Setup(v => v.ShouldDeleteFolder(It.IsAny<DirectoryInfo>(), _movie)).Returns(true);

            Subject.Execute(new ManualImportCommand { Files = new List<ManualImportFile> { _file } });

            Mocker.GetMock<ICompletedDownloadService>().Verify(v => v.VerifyImport(_download, It.IsAny<List<ImportResult>>()), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(v => v.DeleteFolder(It.IsAny<string>(), true), Times.Never());
            Assert.That(_download.State, Is.Not.EqualTo(TrackedDownloadState.Imported));
        }

        [Test]
        public void invalid_later_selection_should_fail_before_any_imports()
        {
            var invalid = new ManualImportFile { MovieId = 1, Path = _file.Path + ".other.mkv" };
            Mocker.GetMock<IManualImportVersionService>().Setup(v => v.Prepare(It.IsAny<LocalMovie>(), null, invalid)).Throws(new NzbDroneClientException(System.Net.HttpStatusCode.Conflict, "Select a version"));

            Assert.Throws<NzbDroneClientException>(() => Subject.Execute(new ManualImportCommand { Files = new List<ManualImportFile> { _file, invalid } }));

            Mocker.GetMock<IImportApprovedMovie>().Verify(v => v.Import(It.IsAny<List<ImportDecision>>(), It.IsAny<bool>(), It.IsAny<DownloadClientItem>(), It.IsAny<ImportMode>()), Times.Never());
        }

        [Test]
        public void one_source_should_not_be_selected_for_both_versions_in_one_command()
        {
            var duplicate = new ManualImportFile { MovieId = 1, MovieVersionId = 8, Path = _file.Path };
            Assert.Throws<NzbDroneClientException>(() => Subject.Execute(new ManualImportCommand { Files = new List<ManualImportFile> { _file, duplicate } }));
            Mocker.GetMock<IImportApprovedMovie>().Verify(v => v.Import(It.IsAny<List<ImportDecision>>(), It.IsAny<bool>(), It.IsAny<DownloadClientItem>(), It.IsAny<ImportMode>()), Times.Never());
        }

        [Test]
        public void reprocessing_should_keep_selected_child_context_for_scores_and_decisions()
        {
            var selection = new ManualImportVersionSelection { MovieVersionId = 7 };
            var child = new Movie { Id = 1, Path = _movie.Path, QualityProfile = new QualityProfile { Id = 2 } };
            Mocker.GetMock<IManualImportVersionService>().Setup(v => v.Prepare(It.IsAny<LocalMovie>(), null, selection))
                .Callback<LocalMovie, string, ManualImportVersionSelection>((local, id, selected) =>
                {
                    local.Movie = child;
                    local.MovieVersionId = selected.MovieVersionId;
                });
            Mocker.GetMock<IMakeImportDecision>().Setup(v => v.GetDecision(It.IsAny<LocalMovie>(), null)).Returns<LocalMovie, DownloadClientItem>((local, item) => new ImportDecision(local));

            var result = Subject.ReprocessItem(_file.Path, null, 1, "group", _file.Quality, _file.Languages, 0, selection);

            Assert.That(result.MovieVersionId, Is.EqualTo(7));
            Assert.That(result.Movie.QualityProfile.Id, Is.EqualTo(2));
        }
    }
}
