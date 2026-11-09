using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using NLog;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles
{
    public interface IVersionImportFileVerifier
    {
        string Hash(string path);
        string CopyAndHash(string source, string destination);
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
            var cached = VersionImportVerificationScope.Find(_disk, path);
            if (cached != null)
            {
                _logger.Debug("Reusing import SHA-256 for unchanged file {0}", path);
                return cached;
            }

            var size = _disk.GetFileSize(path);
            var modified = _disk.FileGetLastWrite(path);
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

                if (_disk.GetFileSize(path) != size || _disk.FileGetLastWrite(path) != modified)
                {
                    throw new IOException("The file changed while its import checksum was calculated.");
                }

                var result = Convert.ToHexString(hash.GetHashAndReset());
                VersionImportVerificationScope.Remember(_disk, path, result);
                _logger.Debug("Computed import SHA-256 for {0}: {1} bytes in {2:F1} seconds", path, total, timer.Elapsed.TotalSeconds);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public string CopyAndHash(string source, string destination)
        {
            if (_disk.FileExists(destination))
            {
                throw new IOException("The import staging destination already exists.");
            }

            var size = _disk.GetFileSize(source);
            var modified = _disk.FileGetLastWrite(source);
            using var input = _disk.OpenReadStream(source);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var timer = Stopwatch.StartNew();
            long total = 0;
            try
            {
                using (var output = _disk.OpenWriteStream(destination))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        output.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                        total += read;
                    }

                    output.Flush(true);
                }

                if (total != size || _disk.GetFileSize(source) != size || _disk.FileGetLastWrite(source) != modified || _disk.GetFileSize(destination) != size)
                {
                    throw new IOException("The incoming source changed or the import staging copy is incomplete.");
                }

                var result = Convert.ToHexString(hash.GetHashAndReset());

                // The destination still needs an independent read; never cache it here.
                VersionImportVerificationScope.Remember(_disk, source, result);
                _logger.Debug("Copied and computed import SHA-256 for {0}: {1} bytes in {2:F1} seconds", source, total, timer.Elapsed.TotalSeconds);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
