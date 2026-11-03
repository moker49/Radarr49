using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        private ImportApprovedMovie RuntimeImporter()
        {
            Mocker.GetMock<IMovieRepository>().Setup(v => v.Get(_movie.Id)).Returns(() => Root());
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(It.IsAny<Movie>())).Returns(() => new List<ResolvedMovieVersion> { Resolve(_sdr.Id), Resolve(_hdr.Id) });
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.ForVersion(It.IsAny<Movie>(), It.IsAny<int>())).Returns<Movie, int>((movie, id) =>
            {
                Mocker.Resolve<VersionFileDeletionRepository>().CheckReady(movie.Id, id);
                var version = Resolve(id);
                return movie.WithVersion(version, Db.All<MovieFile>().SingleOrDefault(f => f.Id == version.MovieFileId));
            });
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesWithRelativePath(_movie.Id, It.IsAny<string>())).Returns<int, string>((movie, relative) => Db.All<MovieFile>().Where(f => f.MovieId == movie && string.Equals(f.RelativePath, relative, StringComparison.OrdinalIgnoreCase)).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(v => v.Update(It.IsAny<MovieFile>())).Callback<MovieFile>(f => Mocker.Resolve<MediaFileRepository>().Update(f));
            Mocker.SetConstant<IDownloadReservationRepository>(Mocker.Resolve<DownloadReservationRepository>());
            Mocker.SetConstant<IMovieVersionImportService>(Mocker.Resolve<MovieVersionImportService>());
            Mocker.SetConstant<IVersionImportRecoveryService>(Recovery());
            Mocker.SetConstant<IManagedMovieImportService>(Mocker.Resolve<ManagedMovieImportService>());
            return Mocker.Resolve<ImportApprovedMovie>();
        }

        private LocalMovie RuntimeLocal() => new LocalMovie
        {
            Movie = Root().WithVersion(Resolve(_sdr.Id), _old), MovieVersionId = _sdr.Id,
            Path = _source, Size = 150, Quality = new QualityModel(Quality.Bluray1080p), Languages = new List<Language> { Language.English }, SceneName = "Release.SDR"
        };

        [TestCase(ImportMode.Move, false)]
        [TestCase(ImportMode.Copy, true)]
        public void approved_managed_import_should_complete_journal_before_returning_success(ImportMode mode, bool retainsSource)
        {
            var local = RuntimeLocal();
            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(local) }, true, null, mode).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Imported));
            var operation = _operations.All().Single();
            AssertCompletedImport(operation);
            Assert.That(_disk.ContainsKey(_source), Is.EqualTo(retainsSource));
            Assert.That(local.Movie.MovieFileId, Is.EqualTo(operation.NewMovieFileId));
            Assert.That(local.Movie.MovieFile.MovieVersionId, Is.EqualTo(_sdr.Id));
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<MovieFile>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileImportedEvent>()), Times.Once());
            Mocker.GetMock<IExtraService>().Verify(v => v.ImportMovie(It.IsAny<LocalMovie>(), It.IsAny<MovieFile>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IExtraService>().Verify(v => v.CreateAfterMovieImport(It.IsAny<Movie>(), It.Is<MovieFile>(f => f.Id == operation.NewMovieFileId)), Times.Once());
        }

        [Test]
        public void failed_runtime_copy_should_report_operation_and_block_fresh_import_intents()
        {
            var importer = RuntimeImporter();
            Mocker.GetMock<IDiskTransferService>().Setup(v => v.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false)).Returns<string, string, TransferMode, bool>((s, d, mode, overwrite) =>
            {
                _disk.Add(d, (10, new string('D', 64)));
                return TransferMode.Copy;
            });
            var local = RuntimeLocal();
            var result = importer.Import(new List<ImportDecision> { new ImportDecision(local) }, true).Single();
            var operation = _operations.All().Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Skipped));
            Assert.That(result.Errors.Single(), Does.Contain("operation " + operation.Id));
            Assert.That(operation.State, Is.EqualTo(VersionFileImportState.Staging));
            Assert.That(operation.Snapshot.LastRecoveryError, Does.Contain("staging"));
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(importer.Import(new List<ImportDecision> { new ImportDecision(RuntimeLocal()) }, true).Single().Result, Is.EqualTo(ImportResultType.Skipped));
            Assert.That(_operations.All().Count, Is.EqualTo(1));
            Assert.That(_recycled, Is.Empty);
        }

        [Test]
        public void automatic_bound_import_should_keep_copy_source_and_exact_reservation_identity()
        {
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "runtime-job", SourceTitle = "300 SDR", State = DownloadReservationState.Submitted });
            var client = new DownloadClientItem { DownloadId = "runtime-job", DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7 }, CanMoveFiles = false, OutputPath = new OsPath(Path.GetDirectoryName(_source)) };
            var local = RuntimeLocal();
            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(local) }, true, client).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Imported));
            var operation = _operations.All().Single();
            AssertCompletedImport(operation);
            Assert.That(operation.DownloadReservationId, Is.EqualTo(_reservation.Id));
            Assert.That(operation.CopyOnly, Is.True);
            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(Db.Single<DownloadReservation>().ImportedMovieFileId, Is.EqualTo(operation.NewMovieFileId));
        }

        [Test]
        public void existing_managed_file_refresh_should_update_in_place_without_detaching_subtitles()
        {
            var subtitle = Db.Insert(new SubtitleFile { MovieId = _movie.Id, MovieFileId = _old.Id, RelativePath = "300 (2006) - SDR.en.srt", Extension = ".srt", Language = Language.English });
            var local = RuntimeLocal();
            local.Path = Full(_old.RelativePath);
            local.ReleaseGroup = "Edited group";
            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(local) }, false).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(Db.All<MovieFile>().Single(f => f.Id == _old.Id).ReleaseGroup, Is.EqualTo("Edited group"));
            Assert.That(Db.Single<SubtitleFile>().MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(Db.Single<SubtitleFile>().Id, Is.EqualTo(subtitle.Id));
            Assert.That(Db.Single<Movie>().MovieFileId, Is.EqualTo(_old.Id));
            Assert.That(_operations.All(), Is.Empty);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileDeletedEvent>()), Times.Never());
        }

        [Test]
        public void metadata_provider_failure_should_leave_runtime_import_successful_and_complete()
        {
            Mocker.GetMock<IExtraService>().Setup(v => v.CreateAfterMovieImport(It.IsAny<Movie>(), It.IsAny<MovieFile>())).Throws(new IOException("Metadata provider failed"));
            var local = RuntimeLocal();
            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(local) }, true).Single();
            Assert.That(result.Result, Is.EqualTo(ImportResultType.Imported));
            AssertCompletedImport(_operations.All().Single());
            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
