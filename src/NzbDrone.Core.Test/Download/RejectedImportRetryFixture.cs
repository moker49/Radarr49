using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class RejectedImportRetryFixture : CoreTest<RejectedImportService>
    {
        [TestCase(ImportRejectionReason.NoImportableVideo, true)]
        [TestCase(ImportRejectionReason.UnsupportedExtension, true)]
        [TestCase(ImportRejectionReason.VersionLanguageNotSatisfied, true)]
        [TestCase(ImportRejectionReason.VersionProfileNotSatisfied, false)]
        [TestCase(ImportRejectionReason.ArchiveFile, false)]
        [TestCase(ImportRejectionReason.InvalidMovieVersion, false)]
        [TestCase(ImportRejectionReason.FileLocked, false)]
        [TestCase(ImportRejectionReason.MinimumFreeSpace, false)]
        public void only_explicit_content_failures_should_trigger_replacement(ImportRejectionReason reason, bool retry)
        {
            var tracked = Download();
            Subject.Process(tracked, Result(reason));
            Assert.That(tracked.State == TrackedDownloadState.FailedPending, Is.EqualTo(retry));
            Assert.That(tracked.ImportFailureReason, Is.EqualTo(retry ? reason : (ImportRejectionReason?)null));
        }

        [Test]
        public void operational_rejection_must_override_a_retryable_content_rejection()
        {
            var tracked = Download();
            var result = new ImportResult(new ImportDecision(null, new ImportRejection(ImportRejectionReason.VersionLanguageNotSatisfied, "Language"), new ImportRejection(ImportRejectionReason.MinimumFreeSpace, "Disk full")), "Rejected");
            Subject.Process(tracked, result);
            Assert.That(tracked.State, Is.Not.EqualTo(TrackedDownloadState.FailedPending));
        }

        [Test]
        public void language_rejection_alongside_a_sample_must_still_retry()
        {
            var tracked = Download();
            Subject.Process(tracked, new List<ImportResult> { Result(ImportRejectionReason.VersionLanguageNotSatisfied), Result(ImportRejectionReason.Sample) });
            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.FailedPending));
        }

        [Test]
        public void unassigned_composite_download_must_not_be_globally_blocklisted()
        {
            var tracked = Download();
            tracked.MovieVersionId = null;
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(d => d.ForProfile(It.IsAny<int>())).Returns(new List<CompositeProfileVersion> { new CompositeProfileVersion() });
            Subject.Process(tracked, Result(ImportRejectionReason.NoImportableVideo));
            Assert.That(tracked.State, Is.Not.EqualTo(TrackedDownloadState.FailedPending));
        }

        [Test]
        public void unfinished_operation_must_prevent_content_failure_cleanup()
        {
            var tracked = Download();
            Mocker.GetMock<IVersionFileImportRepository>().Setup(i => i.ForMovie(1)).Returns(new List<VersionFileImport>
            {
                new VersionFileImport { MovieVersionId = 7, State = VersionFileImportState.Installing }
            });
            Subject.Process(tracked, Result(ImportRejectionReason.NoImportableVideo));
            Assert.That(tracked.State, Is.Not.EqualTo(TrackedDownloadState.FailedPending));
        }

        [Test]
        public void incomplete_download_must_not_be_failed_for_missing_video()
        {
            var tracked = Download();
            tracked.DownloadItem.Status = DownloadItemStatus.Downloading;
            Subject.Process(tracked, Result(ImportRejectionReason.NoImportableVideo));
            Assert.That(tracked.State, Is.Not.EqualTo(TrackedDownloadState.FailedPending));
        }

        private static TrackedDownload Download() => new TrackedDownload
        {
            MovieVersionId = 7,
            DownloadItem = new DownloadClientItem { Title = "Movie", Status = DownloadItemStatus.Completed },
            RemoteMovie = new RemoteMovie { Movie = new Movie { Id = 1 }, Release = new ReleaseInfo() }
        };

        private static ImportResult Result(ImportRejectionReason reason) => new ImportResult(new ImportDecision(null, new ImportRejection(reason, "Rejected content")), "Rejected content");
    }
}
