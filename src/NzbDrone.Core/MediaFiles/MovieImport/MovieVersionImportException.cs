using System;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public class MovieVersionImportException : Exception
    {
        public MovieVersionImportException(string message)
            : base(message)
        {
        }
    }
}
