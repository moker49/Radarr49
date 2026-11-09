using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        [Test]
        public void complete_runtime_import_should_read_incoming_video_only_for_copy_and_independent_verification()
        {
            var content = new Dictionary<string, byte[]>();
            foreach (var path in _disk.Keys.ToList())
            {
                var bytes = Enumerable.Repeat((byte)_disk[path].Hash[0], (int)_disk[path].Size).ToArray();
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                _disk[path] = (bytes.Length, hash);
                content[hash] = bytes;
            }

            var reads = new List<(string Path, long Size)>();
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenReadStream(It.IsAny<string>())).Returns<string>(path =>
            {
                reads.Add((path, _disk[path].Size));
                var physical = GetTempFilePath();
                System.IO.File.WriteAllBytes(physical, content[_disk[path].Hash]);
                return System.IO.File.OpenRead(physical);
            });
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenWriteStream(It.IsAny<string>())).Returns<string>(path =>
                new CapturedImportStream(GetTempFilePath(), bytes =>
                {
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    content[hash] = bytes;
                    _disk[path] = (bytes.Length, hash);
                }));
            Mocker.SetConstant<IVersionImportFileVerifier>(Mocker.Resolve<VersionImportFileVerifier>());

            var result = RuntimeImporter().Import(new List<ImportDecision> { new ImportDecision(RuntimeLocal()) }, true).Single();

            Assert.That(result.Result, Is.EqualTo(ImportResultType.Imported));
            var operation = _operations.All().Single();
            AssertCompletedImport(operation);
            Assert.That(reads.Count(p => p.Path == _source && p.Size == 150), Is.EqualTo(1), "Source checksum must be computed during the copy.");
            Assert.That(reads.Count(p => p.Path == Full(operation.StagingRelativePath) && p.Size == 150), Is.EqualTo(1), "Staged destination must be verified independently once.");
            Assert.That(reads.Count(p => p.Path == Full(operation.DestinationRelativePath) && p.Size == 150), Is.Zero, "Renaming a verified staging file must not restart hashing.");
        }

        [Test]
        public void interrupted_streamed_copy_without_recorded_checksum_must_preserve_original_and_source()
        {
            var local = RuntimeLocal();
            var operation = Subject.Prepare(local, File(null), false, deferSourceHash: true);
            _operations.Advance(operation.Id, VersionFileImportState.Prepared, VersionFileImportState.Staging);
            _disk.Add(Full(operation.StagingRelativePath), _disk[_source]);

            Assert.Throws<InvalidOperationException>(() => Subject.Resume(_movie.Id, operation.Id));
            Assert.That(_disk.ContainsKey(_source), Is.True);
            Assert.That(_disk[Full(_old.RelativePath)].Hash, Is.EqualTo(new string('B', 64)));
            Assert.That(_operations.Get(operation.Id).State, Is.EqualTo(VersionFileImportState.Staging));
        }

        private sealed class CapturedImportStream : FileStream
        {
            private readonly string _path;
            private readonly Action<byte[]> _onClose;
            private bool _closed;

            public CapturedImportStream(string path, Action<byte[]> onClose)
                : base(path, FileMode.CreateNew)
            {
                _path = path;
                _onClose = onClose;
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && !_closed)
                {
                    _closed = true;
                    _onClose(System.IO.File.ReadAllBytes(_path));
                }
            }
        }
    }
}
