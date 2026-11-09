using System.Threading;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadImportPipelineFixture : CoreTest<DownloadImportPipeline>
    {
        [TearDown]
        public void StopWorkers() => Subject.Handle(new ApplicationShutdownRequested());

        [Test]
        public void verification_must_not_block_copies_for_any_download_and_second_verification_must_wait()
        {
            using var verifying = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var verified = new ManualResetEventSlim();
            using var hdrCopied = new ManualResetEventSlim();
            using var otherMovieCopied = new ManualResetEventSlim();
            using var nextVerified = new ManualResetEventSlim();
            Subject.QueueVerification("movie-1-sdr", () =>
            {
                verifying.Set();
                release.Wait();
                verified.Set();
            });
            try
            {
                Assert.That(verifying.Wait(5000), Is.True);
                Assert.That(Subject.QueueVerification("movie-1-sdr", () => { }), Is.False);
                Subject.QueueVerification("movie-1-hdr", () => nextVerified.Set());
                Subject.QueueCopy("movie-1-hdr", () => hdrCopied.Set());
                Subject.QueueCopy("movie-2-sdr", () => otherMovieCopied.Set());
                Assert.That(hdrCopied.Wait(5000), Is.True);
                Assert.That(otherMovieCopied.Wait(5000), Is.True);
                Assert.That(nextVerified.IsSet, Is.False);
            }
            finally
            {
                release.Set();
                Assert.That(verified.Wait(5000), Is.True);
                Assert.That(nextVerified.Wait(5000), Is.True);
            }
        }

        [Test]
        public void copy_worker_must_serialize_and_deduplicate_copies()
        {
            using var copying = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var copied = new ManualResetEventSlim();
            using var nextCopied = new ManualResetEventSlim();
            var scopeActive = false;
            Subject.QueueCopy("first", () =>
            {
                scopeActive = VersionImportCopyScope.IsActive;
                copying.Set();
                release.Wait();
                copied.Set();
            });
            try
            {
                Assert.That(copying.Wait(5000), Is.True);
                Assert.That(scopeActive, Is.True);
                Assert.That(Subject.QueueCopy("first", () => { }), Is.False);
                Subject.QueueCopy("second", () => nextCopied.Set());
                Assert.That(nextCopied.IsSet, Is.False);
            }
            finally
            {
                release.Set();
                Assert.That(copied.Wait(5000), Is.True);
                Assert.That(nextCopied.Wait(5000), Is.True);
            }
        }

        [Test]
        public void shutdown_must_reject_new_work()
        {
            Subject.Handle(new ApplicationShutdownRequested());
            Assert.That(Subject.QueueCopy("copy", () => { }), Is.False);
            Assert.That(Subject.QueueVerification("verification", () => { }), Is.False);
        }
    }
}
