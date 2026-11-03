using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public enum VersionFileImportState
    {
        Prepared = 0,
        Staging = 1,
        Staged = 2,
        BackingUp = 3,
        Installing = 4,
        Registered = 5,
        Finalizing = 6,
        CleaningOldExtras = 7,
        RecyclingBackup = 8,
        RemovingSource = 9,
        EventsPending = 10,
        Completed = 11,
        Cancelled = 12
    }

    public class VersionFileImport : ModelBase
    {
        public int MovieId { get; set; }
        public int MovieVersionId { get; set; }
        public int? DownloadReservationId { get; set; }
        public int? OldMovieFileId { get; set; }
        public int? NewMovieFileId { get; set; }
        public string MoviePath { get; set; }
        public string SourcePath { get; set; }
        public string DestinationRelativePath { get; set; }
        public string StagingRelativePath { get; set; }
        public string BackupRelativePath { get; set; }
        public bool CopyOnly { get; set; }
        public VersionFileImportState State { get; set; }
        public VersionFileImportSnapshot Snapshot { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public string Note { get; set; }
        public bool IsActive => State is not (VersionFileImportState.Completed or VersionFileImportState.Cancelled);
    }

    public class VersionFileImportSnapshot : IEmbeddedDocument
    {
        public MovieFile Incoming { get; set; }
        public MovieFile Previous { get; set; }
        public bool PreviousExisted { get; set; }
        public string SourceHash { get; set; }
        public string PreviousHash { get; set; }
        public ParsedMovieInfo FileMovieInfo { get; set; }
        public ParsedMovieInfo FolderMovieInfo { get; set; }
        public bool SceneSource { get; set; }
        public DownloadClientItemClientInfo DownloadClientInfo { get; set; }
        public string DownloadId { get; set; }
        public int CustomFormatScore { get; set; }
        public List<VersionImportFormat> CustomFormats { get; set; } = new List<VersionImportFormat>();
        public GrabbedReleaseInfo Release { get; set; }
        public List<VersionFileDeletionExtra> PreviousExtras { get; set; } = new List<VersionFileDeletionExtra>();
        public List<VersionImportSourceExtra> IncomingExtras { get; set; } = new List<VersionImportSourceExtra>();
        public List<VersionImportCleanupItem> Cleanup { get; set; }
        public VersionImportEventProgress Events { get; set; }
        public string LastRecoveryError { get; set; }
        public DateTime? LastRecoveryAttempt { get; set; }
    }

    public class VersionImportFormat : IEmbeddedDocument
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class VersionImportEventProgress : IEmbeddedDocument
    {
        public int ImportHistoryId { get; set; }
        public int? DeletedHistoryId { get; set; }
        public int? DownloadHistoryId { get; set; }
        public List<VersionImportDelivery> Deliveries { get; set; } = new List<VersionImportDelivery>();
    }

    public class VersionImportDelivery : IEmbeddedDocument
    {
        public VersionImportEventKind Kind { get; set; }
        public VersionImportDeliveryState State { get; set; }
    }

    public enum VersionImportEventKind
    {
        OldFileDeleted = 0,
        DeleteCompleted = 1,
        FileAdded = 2,
        FileImported = 3
    }

    public enum VersionImportDeliveryState
    {
        Prepared = 0,
        Dispatching = 1,
        Dispatched = 2
    }

    public class VersionImportSourceExtra : IEmbeddedDocument
    {
        public string SourcePath { get; set; }
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string ContentHash { get; set; }
        public string DestinationRelativePath { get; set; }
        public string StagingRelativePath { get; set; }
        public string BackupRelativePath { get; set; }
        public VersionImportExtraState State { get; set; }
        public SubtitleFile Subtitle { get; set; }
        public int? RegisteredExtraId { get; set; }
    }

    public enum VersionImportExtraState
    {
        Prepared = 0,
        Copying = 1,
        Staged = 2,
        BackingUp = 3,
        Installing = 4,
        Registered = 5
    }

    public enum VersionImportCleanupKind
    {
        OldExtra = 0,
        Backup = 1,
        Source = 2
    }

    public enum VersionImportCleanupState
    {
        Prepared = 0,
        Recycling = 1,
        Finished = 2
    }

    public class VersionImportCleanupItem : IEmbeddedDocument
    {
        // Source paths are absolute; old extras and backups are movie-relative.
        public VersionImportCleanupKind Kind { get; set; }
        public string Path { get; set; }
        public bool Existed { get; set; }
        public long Size { get; set; }
        public string ContentHash { get; set; }
        public bool Preserve { get; set; }
        public string Table { get; set; }
        public int? ExtraId { get; set; }
        public VersionImportCleanupState State { get; set; }
        public string RecyclePath { get; set; }
        public string Note { get; set; }
    }
}
