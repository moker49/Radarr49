using System;

namespace NzbDrone.Core.MediaFiles
{
    public class PublishedChecksumMismatchException : InvalidOperationException
    {
        public PendingVersionImport Pending { get; }

        public PublishedChecksumMismatchException(PendingVersionImport pending)
            : base("The published copy did not match the source checksum. The failed copy was quarantined and recoverable files were preserved.")
        {
            Pending = pending;
        }
    }
}
