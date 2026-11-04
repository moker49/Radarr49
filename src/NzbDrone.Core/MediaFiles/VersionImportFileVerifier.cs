using System;
using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using NLog;
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
        private readonly Logger _logger;

        public VersionImportFileVerifier(IDiskProvider disk, Logger logger)
        {
            _disk = disk;
            _logger = logger;
        }

        public string Hash(string path)
        {
            using var stream = _disk.OpenReadStream(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var timer = Stopwatch.StartNew();
            long total = 0;
            _logger.Debug("Computing import SHA-256 for {0}", path);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    total += read;
                }

                var result = Convert.ToHexString(hash.GetHashAndReset());
                _logger.Debug("Computed import SHA-256 for {0}: {1} bytes in {2:F1} seconds", path, total, timer.Elapsed.TotalSeconds);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
