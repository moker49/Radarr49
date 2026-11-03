using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class VersionImportSubscriberFixture : CoreTest
    {
        [Test]
        public void journal_import_history_handlers_should_not_infer_or_insert_other_history()
        {
            var imported = new MovieFileImportedEvent(new LocalMovie(), new MovieFile(), new List<DeletedMovieFile>(), true, null) { ImportOperationId = 42 };
            Mocker.Resolve<HistoryService>().Handle(imported);
            Mocker.Resolve<DownloadHistoryService>().Handle(imported);
            Assert.That(Mocker.GetMock<IHistoryRepository>().Invocations, Is.Empty);
            Assert.That(Mocker.GetMock<IDownloadHistoryRepository>().Invocations, Is.Empty);
            Assert.That(Mocker.GetMock<IHistoryService>().Invocations, Is.Empty);
        }

        [Test]
        public void journal_upgrade_delete_should_not_run_legacy_detach_folder_or_sidecar_cleanup()
        {
            Mocker.GetMock<IConfigService>().SetupGet(v => v.DeleteEmptyFolders).Returns(true);
            var deleted = new MovieFileDeletedEvent(new MovieFile { Id = 11, MovieId = 1 }, DeleteMediaFileReason.Upgrade) { ImportOperationId = 42 };
            Mocker.Resolve<HistoryService>().Handle(deleted);
            Mocker.Resolve<MovieService>().Handle(deleted);
            Mocker.Resolve<Core.MediaFiles.MediaFileDeletionService>().Handle(deleted);
            Mocker.Resolve<SubtitleFileService>().HandleAsync(deleted);
            Assert.That(Mocker.GetMock<IHistoryRepository>().Invocations, Is.Empty);
            Assert.That(Mocker.GetMock<IMovieRepository>().Invocations, Is.Empty);
            Assert.That(Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Invocations, Is.Empty);
            Assert.That(Mocker.GetMock<IDiskProvider>().Invocations, Is.Empty);
        }
    }
}
