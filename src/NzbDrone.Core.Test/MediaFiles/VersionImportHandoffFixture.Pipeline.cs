using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        [Test]
        public void destination_mismatch_should_allow_only_one_recopy_from_a_verified_source()
        {
            CorruptPublication();
            var recovery = Recovery();
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, DownloadClientId = 7, DownloadId = "recopy", State = DownloadReservationState.Submitted, SourceTitle = "300 SDR" });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ImportResult result;
                using (new VersionImportCopyScope())
                {
                    var local = RuntimeLocal();
                    local.DownloadReservationId = _reservation.Id;
                    var client = new DownloadClientItem { DownloadId = "recopy", DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7 }, CanMoveFiles = true };
                    result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(local) }, true, client).Single();
                }

                Assert.That(result.Result, Is.EqualTo(ImportResultType.PendingVerification));
                Assert.Throws<PublishedChecksumMismatchException>(() => recovery.CompletePublished(result.PendingImport));
                Assert.That(recovery.PrepareCopyRetry(result.PendingImport), Is.EqualTo(attempt == 0));
            }

            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
        }

        [Test]
        public void deferred_verification_failure_must_roll_back_the_published_file_and_keep_its_source()
        {
            CorruptPublication();
            ImportResult result;
            using (new VersionImportCopyScope())
            {
                result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(RuntimeLocal()) }, true).Single();
            }

            Assert.That(result.Result, Is.EqualTo(ImportResultType.PendingVerification));
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('D', 64)));
            Assert.Throws<PublishedChecksumMismatchException>(() => Recovery().CompletePublished(result.PendingImport));
            Assert.That(_operations.Get(result.PendingImport.OperationId).Snapshot.RollbackCompleted, Is.True);
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(_operations.Get(result.PendingImport.OperationId).NewMovieFileId, Is.Null);
        }

        [Test]
        public void hdr_copy_and_publication_must_finish_while_sdr_verification_is_blocked()
        {
            var importer = RuntimeImporter();
            var recovery = Recovery();
            var sdrLocal = RuntimeLocal();
            ImportResult sdr;
            using (new VersionImportCopyScope())
            {
                sdr = importer.Import(new List<ImportDecision> { new ImportDecision(sdrLocal) }, true).Single();
            }

            Assert.That(sdr.Result, Is.EqualTo(ImportResultType.PendingVerification));
            using var verifying = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var sdrPath = Full(_old.RelativePath);
            Mocker.GetMock<IVersionImportFileVerifier>().Setup(v => v.Hash(It.IsAny<string>())).Returns<string>(path =>
            {
                if (path == sdrPath && _disk[path].Size == 150)
                {
                    verifying.Set();
                    release.Wait();
                }

                return _disk[path].Hash;
            });
            var hdrLocal = RuntimeLocal();
            hdrLocal.MovieVersionId = _hdr.Id;
            hdrLocal.Movie = Root().WithVersion(Resolve(_hdr.Id), _other);
            hdrLocal.Path = _source + ".hdr.mkv";
            _disk.Add(hdrLocal.Path, (200, new string('D', 64)));
            var verification = Task.Run(() => recovery.CompletePublished(sdr.PendingImport));
            Task<ImportResult> copying = null;
            try
            {
                Assert.That(verifying.Wait(5000), Is.True);
                Assert.Throws<NzbDroneClientException>(() => recovery.Resume(_movie.Id, sdr.PendingImport.OperationId));
                copying = Task.Run(() =>
                {
                    using var scope = new VersionImportCopyScope();
                    return importer.Import(new List<ImportDecision> { new ImportDecision(hdrLocal) }, true).Single();
                });
                Assert.That(copying.Wait(5000), Is.True, "HDR copy must not wait for SDR verification or its movie lock.");
                Assert.That(copying.Result.Result, Is.EqualTo(ImportResultType.PendingVerification));
                var hdr = _operations.Get(copying.Result.PendingImport.OperationId);
                Assert.That(_disk.ContainsKey(Full(hdr.DestinationRelativePath)), Is.True);
                Assert.That(hdr.State, Is.EqualTo(VersionFileImportState.Installing));
                Assert.That(verification.IsCompleted, Is.False);
            }
            finally
            {
                release.Set();
                Assert.That(verification.Wait(5000), Is.True);
                if (copying != null)
                {
                    Assert.That(copying.Wait(5000), Is.True);
                }
            }

            recovery.CompletePublished(copying.Result.PendingImport);
            Assert.That(_operations.Get(sdr.PendingImport.OperationId).State, Is.EqualTo(VersionFileImportState.Completed));
            Assert.That(_operations.Get(copying.Result.PendingImport.OperationId).State, Is.EqualTo(VersionFileImportState.Completed));
        }
    }
}
