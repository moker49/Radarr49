using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    public partial class ImportApprovedMoviesFixture
    {
        [Test]
        public void single_file_audit_registration_must_not_reclaim_an_unmanaged_copy()
        {
            var local = _approvedDecisions[0].LocalMovie;
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesWithRelativePath(local.Movie.Id, It.IsAny<string>()))
                .Returns(new List<MovieFile> { new MovieFile { Id = 90, MovieId = local.Movie.Id, IsUnmanaged = true } });

            var result = Subject.Import(_approvedDecisions, false);

            Assert.That(result[0].Result, Is.EqualTo(ImportResultType.Skipped));
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<MovieFile>()), Times.Never());
        }

        [TestCase(ImportMode.Auto, true, false)]
        [TestCase(ImportMode.Auto, false, true)]
        [TestCase(ImportMode.Copy, true, true)]
        [TestCase(ImportMode.Move, false, false)]
        public void managed_download_should_delegate_entire_import_to_durable_pipeline(ImportMode mode, bool canMove, bool copyOnly)
        {
            GivenNewDownload();
            var local = _approvedDecisions.Single().LocalMovie;
            local.MovieVersionId = 7;
            local.DownloadItem = _downloadClientItem;
            _downloadClientItem.CanMoveFiles = canMove;
            var results = Subject.Import(_approvedDecisions, true, null, mode);
            Assert.That(results.Single().Result, Is.EqualTo(ImportResultType.Imported));
            Mocker.GetMock<IManagedMovieImportService>().Verify(v => v.Import(local, It.Is<MovieFile>(f => f.MovieVersionId == 7 && f.Id == 0 && f.OriginalFilePath != null), copyOnly), Times.Once());
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<MovieFile>()), Times.Never());
            Mocker.GetMock<IExtraService>().Verify(v => v.ImportMovie(It.IsAny<LocalMovie>(), It.IsAny<MovieFile>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Never());
        }

        [Test]
        public void failed_journal_should_return_skipped_with_recovery_operation_in_error()
        {
            GivenNewDownload();
            var local = _approvedDecisions.Single().LocalMovie;
            local.MovieVersionId = 7;
            Mocker.GetMock<IManagedMovieImportService>().Setup(v => v.Import(local, It.IsAny<MovieFile>(), It.IsAny<bool>())).Throws(new MovieVersionImportException("Managed import operation 42 requires inspection"));
            var result = Subject.Import(_approvedDecisions, true).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Skipped));
            Assert.That(result.Errors.Single(), Does.Contain("42"));
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<MovieFile>()), Times.Never());
        }

        [Test]
        public void failed_managed_version_should_not_suppress_other_version()
        {
            GivenNewDownload();
            var local = _approvedDecisions.Single().LocalMovie;
            local.MovieVersionId = 7;
            var hdr = new LocalMovie { Movie = local.Movie, MovieVersionId = 8, Path = local.Path + ".hdr.mkv", Quality = local.Quality };
            Mocker.GetMock<IManagedMovieImportService>().Setup(v => v.Import(local, It.IsAny<MovieFile>(), It.IsAny<bool>())).Throws(new MovieVersionImportException("SDR remains pending"));
            var results = Subject.Import(new List<ImportDecision> { new ImportDecision(local), new ImportDecision(hdr) }, true);
            Assert.That(results.Count(r => r.Result == ImportResultType.Imported), Is.EqualTo(1));
            Mocker.GetMock<IManagedMovieImportService>().Verify(v => v.Import(hdr, It.IsAny<MovieFile>(), It.IsAny<bool>()), Times.Once());
        }

        [Test]
        public void managed_import_should_use_selected_flags_without_guessing_from_reused_download_id()
        {
            GivenNewDownload();
            var local = _approvedDecisions.Single().LocalMovie;
            local.MovieVersionId = 7;
            local.IndexerFlags = (IndexerFlags)8;
            Subject.Import(_approvedDecisions, true, _downloadClientItem);
            Mocker.GetMock<IManagedMovieImportService>().Verify(v => v.Import(local, It.Is<MovieFile>(f => f.IndexerFlags == (IndexerFlags)8), It.IsAny<bool>()), Times.Once());
            Mocker.GetMock<IHistoryService>().Verify(v => v.FindByDownloadId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void managed_existing_file_refresh_should_keep_file_identity_and_extra_ownership()
        {
            var local = _approvedDecisions.Single().LocalMovie;
            local.MovieVersionId = 7;
            var added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            local.Movie.MovieFile = new MovieFile { Id = 11, RelativePath = Path.GetFileName(local.Path), SceneName = "Original scene", OriginalFilePath = "original/file.mkv", DateAdded = added };
            var result = Subject.Import(_approvedDecisions, false).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Imported));
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Update(It.Is<MovieFile>(f => f.Id == 11 && f.DateAdded == added && f.SceneName == "Original scene" && f.OriginalFilePath == "original/file.mkv")), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<MovieFile>()), Times.Never());
            Mocker.GetMock<IManagedMovieImportService>().Verify(v => v.Import(It.IsAny<LocalMovie>(), It.IsAny<MovieFile>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
