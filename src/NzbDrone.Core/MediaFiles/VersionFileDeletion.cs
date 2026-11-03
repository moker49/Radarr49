using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public enum VersionFileDeletionState
    {
        Prepared = 0,
        Recycling = 1,
        Detached = 2,
        RecyclingExtra = 3,
        EventsPending = 4,
        Completed = 5,
        Cancelled = 6
    }

    public class VersionFileDeletion : ModelBase
    {
        public int MovieId { get; set; }
        public int MovieVersionId { get; set; }
        public int MovieFileId { get; set; }
        public string MoviePath { get; set; }
        public bool Unmonitor { get; set; }
        public VersionFileDeletionState State { get; set; }
        public VersionFileDeletionSnapshot Snapshot { get; set; }
        public int ExtraIndex { get; set; }
        public string RecyclePath { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public string Note { get; set; }
        public bool IsActive => State is not (VersionFileDeletionState.Completed or VersionFileDeletionState.Cancelled);
    }

    public class VersionFileDeletionSnapshot : IEmbeddedDocument
    {
        // Movie is deliberately absent on File to avoid retaining a stale root/context.
        public MovieFile File { get; set; }
        public bool SourceExisted { get; set; }
        public long SourceSize { get; set; }
        public DateTime SourceLastWrite { get; set; }
        public long SourceLastWriteUtcTicks { get; set; }
        public List<VersionFileDeletionExtra> Extras { get; set; } = new List<VersionFileDeletionExtra>();
    }

    public class VersionFileDeletionExtra : IEmbeddedDocument
    {
        public int Id { get; set; }
        public string Table { get; set; }
        public string RelativePath { get; set; }
        public long Size { get; set; }
        public DateTime LastWrite { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public bool SourceExisted { get; set; }
        public bool Preserve { get; set; }
        public string ContentHash { get; set; }
    }
}
