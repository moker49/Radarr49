using System;

namespace NzbDrone.Core.MediaFiles
{
    public class VersionFileDeletionPendingException : InvalidOperationException
    {
        public VersionFileDeletionPendingException()
            : base("A managed version file operation is unfinished. Inspect and resume or reconcile it before changing files.")
        {
        }
    }
}
