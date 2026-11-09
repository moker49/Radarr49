using System;
using System.IO;
using System.Security.Cryptography;
using Moq;
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

        private void UseRealFiles()
        {
            var disk = Mocker.GetMock<IDiskProvider>();
            disk.Setup(d => d.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
            disk.Setup(d => d.GetFileSize(It.IsAny<string>())).Returns<string>(p => new FileInfo(p).Length);
            disk.Setup(d => d.FileGetLastWrite(It.IsAny<string>())).Returns<string>(File.GetLastWriteTimeUtc);
            disk.Setup(d => d.OpenReadStream(It.IsAny<string>())).Returns<string>(File.OpenRead);
            disk.Setup(d => d.OpenWriteStream(It.IsAny<string>())).Returns<string>(p => new FileStream(p, FileMode.CreateNew));
            disk.Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), false)).Callback<string, string, bool>((a, b, _) => File.Move(a, b));
        }

        [Test]
        public void copy_hash_and_destination_verification_should_read_each_file_only_once()
        {
            UseRealFiles();
            var source = GetTempFilePath();
            var stage = GetTempFilePath();
            var destination = GetTempFilePath();
            var bytes = new byte[2097189];
            new Random(42).NextBytes(bytes);
            File.WriteAllBytes(source, bytes);
            using (new VersionImportVerificationScope())
            {
                var hash = Subject.CopyAndHash(source, stage);
                Assert.That(hash, Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
                Assert.That(Subject.Hash(stage), Is.EqualTo(hash));
                Assert.That(Subject.Hash(stage), Is.EqualTo(hash));
                VersionImportVerificationScope.Move(Mocker.Resolve<IDiskProvider>(), stage, destination);
                Assert.That(Subject.Hash(destination), Is.EqualTo(hash));
                Assert.That(Subject.Hash(source), Is.EqualTo(hash));
            }

            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(bytes));
            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(source), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(stage), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(destination), Times.Never());
            using (new VersionImportVerificationScope())
            {
                Subject.Hash(destination);
            }

            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(destination), Times.Once());
        }

        [Test]
        public void changed_file_metadata_must_invalidate_verification_in_the_same_attempt()
        {
            UseRealFiles();
            var path = GetTempFilePath();
            File.WriteAllText(path, "abc");
            using (new VersionImportVerificationScope())
            {
                var original = Subject.Hash(path);
                File.WriteAllText(path, "xyz");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
                Assert.That(Subject.Hash(path), Is.Not.EqualTo(original));
            }

            Mocker.GetMock<IDiskProvider>().Verify(d => d.OpenReadStream(path), Times.Exactly(2));
        }

        [Test]
        public void recovery_must_detect_changed_bytes_even_when_size_and_timestamp_are_unchanged()
        {
            UseRealFiles();
            var path = GetTempFilePath();
            File.WriteAllText(path, "abc");
            var modified = File.GetLastWriteTimeUtc(path);
            string original;
            using (new VersionImportVerificationScope())
            {
                original = Subject.Hash(path);
            }

            File.WriteAllText(path, "xyz");
            File.SetLastWriteTimeUtc(path, modified);
            using (new VersionImportVerificationScope())
            {
                Assert.That(Subject.Hash(path), Is.Not.EqualTo(original));
            }
        }

        [Test]
        public void failed_copy_must_not_record_a_verified_source()
        {
            UseRealFiles();
            var source = GetTempFilePath();
            var stage = GetTempFilePath();
            File.WriteAllText(source, "abc");
            Mocker.GetMock<IDiskProvider>().Setup(d => d.OpenWriteStream(stage)).Throws(new IOException("Simulated write failure"));
            using (new VersionImportVerificationScope())
            {
                Assert.Throws<IOException>(() => Subject.CopyAndHash(source, stage));
                Assert.That(VersionImportVerificationScope.Find(Mocker.Resolve<IDiskProvider>(), source), Is.Null);
            }
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
