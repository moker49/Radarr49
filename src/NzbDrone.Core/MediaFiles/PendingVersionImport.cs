using System;
using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles
{
    public class PendingVersionImport
    {
        public int MovieId { get; set; }
        public int OperationId { get; set; }
        public IReadOnlyList<VersionImportVerifiedFile> Evidence { get; set; }
    }

    public class VersionImportVerifiedFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string Hash { get; set; }
    }
}
