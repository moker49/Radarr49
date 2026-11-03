using System.IO;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class VersionImportFileVerifierFixture : CoreTest<VersionImportFileVerifier>
    {
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
    }
}
