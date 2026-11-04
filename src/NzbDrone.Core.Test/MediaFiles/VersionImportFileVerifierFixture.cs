using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class VersionImportFileVerifierFixture : CoreTest<VersionImportFileVerifier>
    {
        [TestCase(0)]
        [TestCase(2097189)]
        public void large_buffer_hash_must_match_sha256_for_empty_and_multibuffer_files(int length)
        {
            var bytes = new byte[length];
            new Random(42).NextBytes(bytes);
            var path = GetTempFilePath();
            File.WriteAllBytes(path, bytes);
            var stream = File.OpenRead(path);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.OpenReadStream("test")).Returns(stream);
            Assert.That(Subject.Hash("test"), Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
            Assert.That(stream.CanRead, Is.False);
        }

        [Test]
        public void a_short_read_must_not_be_treated_as_end_of_file()
        {
            var bytes = new byte[67];
            new Random(42).NextBytes(bytes);
            var path = GetTempFilePath();
            File.WriteAllBytes(path, bytes);
            var stream = new ShortReadStream(path);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.OpenReadStream("test")).Returns(stream);
            Assert.That(Subject.Hash("test"), Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
        }

        [Test]
        public void should_hash_actual_bytes_with_sha256_and_close_stream()
        {
            var path = GetTempFilePath();
            File.WriteAllText(path, "abc");
            FileStream stream = null;
            Mocker.GetMock<IDiskProvider>().Setup(v => v.OpenReadStream(path)).Returns(() => stream = File.OpenRead(path));

            Assert.That(Subject.Hash(path), Is.EqualTo("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
            Assert.That(stream.CanRead, Is.False);
        }

        private sealed class ShortReadStream : FileStream
        {
            public ShortReadStream(string path)
                : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7));
        }
    }
}
