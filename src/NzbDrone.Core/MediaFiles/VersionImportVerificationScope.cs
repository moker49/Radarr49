using System;
using System.Collections.Generic;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles
{
    // Evidence lives only for one synchronous import attempt, never across recovery.
    public sealed class VersionImportVerificationScope : IDisposable
    {
        [ThreadStatic]
        private static Dictionary<string, (long Size, DateTime Modified, string Hash)> _verified;

        private readonly Dictionary<string, (long Size, DateTime Modified, string Hash)> _previous;

        public VersionImportVerificationScope()
        {
            _previous = _verified;
            _verified = new Dictionary<string, (long, DateTime, string)>(StringComparer.Ordinal);
        }

        public static string Find(IDiskProvider disk, string path)
        {
            if (_verified != null && _verified.TryGetValue(path, out var entry) &&
                disk.FileExists(path) && disk.GetFileSize(path) == entry.Size && disk.FileGetLastWrite(path) == entry.Modified)
            {
                return entry.Hash;
            }

            return null;
        }

        public static void Remember(IDiskProvider disk, string path, string hash)
        {
            if (_verified != null)
            {
                _verified[path] = (disk.GetFileSize(path), disk.FileGetLastWrite(path), hash);
            }
        }

        public static void Move(IDiskProvider disk, string source, string destination)
        {
            var hash = Find(disk, source);
            var size = disk.GetFileSize(source);
            var modified = disk.FileGetLastWrite(source);
            disk.MoveFile(source, destination);
            if (hash != null && disk.GetFileSize(destination) == size && disk.FileGetLastWrite(destination) == modified)
            {
                Remember(disk, destination, hash);
            }

            _verified?.Remove(source);
        }

        public void Dispose()
        {
            _verified = _previous;
        }
    }
}
