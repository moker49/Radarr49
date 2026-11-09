using System;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public sealed class VersionImportCopyScope : IDisposable
    {
        [ThreadStatic]
        private static bool _active;

        private readonly bool _previous;
        public static bool IsActive => _active;

        public VersionImportCopyScope()
        {
            _previous = _active;
            _active = true;
        }

        public void Dispose() => _active = _previous;
    }
}
