using System;
using System.Security.Cryptography;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles
{
    public interface IVersionImportFileVerifier
    {
        string Hash(string path);
    }

    public class VersionImportFileVerifier : IVersionImportFileVerifier
    {
        private readonly IDiskProvider _disk;

        public VersionImportFileVerifier(IDiskProvider disk)
        {
            _disk = disk;
        }

        public string Hash(string path)
        {
            using var stream = _disk.OpenReadStream(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
