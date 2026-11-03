using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;
using Radarr.Http;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/{movieId:int}/versionfileimports")]
    public class VersionFileImportsController : Controller
    {
        private readonly IVersionFileImportRepository _operations;
        private readonly IVersionImportRecoveryService _recovery;

        public VersionFileImportsController(IVersionFileImportRepository operations, IVersionImportRecoveryService recovery)
        {
            _operations = operations;
            _recovery = recovery;
        }

        [HttpGet]
        public List<VersionFileImportResource> Get(int movieId) => _operations.ForMovie(movieId).Select(ToResource).ToList();

        [HttpGet("{operationId:int}")]
        public VersionFileImportResource GetOperation(int movieId, int operationId) => ToResource(_recovery.Get(movieId, operationId));

        [HttpPost("{operationId:int}/resume")]
        public IActionResult Resume(int movieId, int operationId, [FromBody] VersionFileImportResumeResource request)
        {
            _recovery.Resume(movieId, operationId, request?.Checkpoint, request?.RetryPhysical ?? false, request?.PreserveChangedExtras ?? false, request?.PreserveChangedSources ?? false, request?.Note);
            return NoContent();
        }

        [HttpPost("{operationId:int}/cancel")]
        public IActionResult Cancel(int movieId, int operationId, [FromBody] VersionFileImportCancelResource request)
        {
            _recovery.Cancel(movieId, operationId, request?.Checkpoint, request?.Note);
            return NoContent();
        }

        private static VersionFileImportResource ToResource(VersionFileImport operation)
        {
            var snapshot = operation.Snapshot;
            return new VersionFileImportResource
            {
                Id = operation.Id, MovieId = operation.MovieId, MovieVersionId = operation.MovieVersionId,
                DownloadReservationId = operation.DownloadReservationId, OldMovieFileId = operation.OldMovieFileId,
                NewMovieFileId = operation.NewMovieFileId, MoviePath = operation.MoviePath, SourcePath = operation.SourcePath,
                DestinationRelativePath = operation.DestinationRelativePath, StagingRelativePath = operation.StagingRelativePath,
                BackupRelativePath = operation.BackupRelativePath, CopyOnly = operation.CopyOnly, State = operation.State,
                IsActive = operation.IsActive, CanCancel = operation.State is VersionFileImportState.Prepared or VersionFileImportState.Staging or VersionFileImportState.Staged,
                Checkpoint = VersionImportRecoveryCheckpoint.For(operation), SourceSize = snapshot.Incoming.Size,
                SourceHash = snapshot.SourceHash, PreviousSize = snapshot.Previous?.Size, PreviousHash = snapshot.PreviousHash,
                Created = operation.Created, Updated = operation.Updated, Note = operation.Note,
                LastRecoveryAttempt = snapshot.LastRecoveryAttempt, LastRecoveryError = snapshot.LastRecoveryError,
                CurrentCleanupIndex = VersionImportRecoveryCheckpoint.CurrentCleanupIndex(operation),
                ImportHistoryId = snapshot.Events?.ImportHistoryId, DeletedHistoryId = snapshot.Events?.DeletedHistoryId,
                DownloadHistoryId = snapshot.Events?.DownloadHistoryId,
                Sidecars = snapshot.IncomingExtras.Select((extra, index) => new VersionFileImportSidecarResource
                {
                    Index = index, SourcePath = extra.SourcePath, DestinationRelativePath = extra.DestinationRelativePath,
                    StagingRelativePath = extra.StagingRelativePath, BackupRelativePath = extra.BackupRelativePath,
                    Size = extra.Size, ContentHash = extra.ContentHash, State = extra.State, RegisteredExtraId = extra.RegisteredExtraId
                }).ToList(),
                Cleanup = snapshot.Cleanup?.Select((item, index) => new VersionFileImportCleanupResource
                {
                    Index = index, Kind = item.Kind, Path = item.Path, Existed = item.Existed, Size = item.Size,
                    ContentHash = item.ContentHash, Preserve = item.Preserve, State = item.State,
                    RecyclePath = item.RecyclePath, Note = item.Note, Table = item.Table, ExtraId = item.ExtraId
                }).ToList() ?? new List<VersionFileImportCleanupResource>(),
                Events = snapshot.Events?.Deliveries.Select((delivery, index) => new VersionFileImportDeliveryResource
                {
                    Index = index, Kind = delivery.Kind, State = delivery.State
                }).ToList() ?? new List<VersionFileImportDeliveryResource>()
            };
        }
    }

    public class VersionFileImportResumeResource
    {
        public string Checkpoint { get; set; }
        public bool RetryPhysical { get; set; }
        public bool PreserveChangedExtras { get; set; }
        public bool PreserveChangedSources { get; set; }
        public string Note { get; set; }
    }

    public class VersionFileImportCancelResource
    {
        public string Checkpoint { get; set; }
        public string Note { get; set; }
    }

    public class VersionFileImportResource
    {
        public int Id { get; set; }
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
        public bool IsActive { get; set; }
        public bool CanCancel { get; set; }
        public string Checkpoint { get; set; }
        public long SourceSize { get; set; }
        public string SourceHash { get; set; }
        public long? PreviousSize { get; set; }
        public string PreviousHash { get; set; }
        public int? CurrentCleanupIndex { get; set; }
        public int? ImportHistoryId { get; set; }
        public int? DeletedHistoryId { get; set; }
        public int? DownloadHistoryId { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public string Note { get; set; }
        public DateTime? LastRecoveryAttempt { get; set; }
        public string LastRecoveryError { get; set; }
        public List<VersionFileImportSidecarResource> Sidecars { get; set; }
        public List<VersionFileImportCleanupResource> Cleanup { get; set; }
        public List<VersionFileImportDeliveryResource> Events { get; set; }
    }

    public class VersionFileImportSidecarResource
    {
        public int Index { get; set; }
        public string SourcePath { get; set; }
        public string DestinationRelativePath { get; set; }
        public string StagingRelativePath { get; set; }
        public string BackupRelativePath { get; set; }
        public long Size { get; set; }
        public string ContentHash { get; set; }
        public VersionImportExtraState State { get; set; }
        public int? RegisteredExtraId { get; set; }
    }

    public class VersionFileImportCleanupResource
    {
        public int Index { get; set; }
        public VersionImportCleanupKind Kind { get; set; }
        public string Path { get; set; }
        public bool Existed { get; set; }
        public long Size { get; set; }
        public string ContentHash { get; set; }
        public bool Preserve { get; set; }
        public VersionImportCleanupState State { get; set; }
        public string RecyclePath { get; set; }
        public string Note { get; set; }
        public string Table { get; set; }
        public int? ExtraId { get; set; }
    }

    public class VersionFileImportDeliveryResource
    {
        public int Index { get; set; }
        public VersionImportEventKind Kind { get; set; }
        public VersionImportDeliveryState State { get; set; }
    }
}
