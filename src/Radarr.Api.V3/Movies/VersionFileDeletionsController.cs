using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;
using Radarr.Http;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/{movieId:int}/versionfiledeletions")]
    public class VersionFileDeletionsController : Controller
    {
        private readonly IVersionFileDeletionRepository _operations;
        private readonly IVersionFileDeletionRecoveryService _recovery;

        public VersionFileDeletionsController(IVersionFileDeletionRepository operations, IVersionFileDeletionRecoveryService recovery)
        {
            _operations = operations;
            _recovery = recovery;
        }

        [HttpGet]
        public List<VersionFileDeletionResource> Get(int movieId) => _operations.ForMovie(movieId).Select(r => new VersionFileDeletionResource
        {
            Id = r.Id, MovieId = r.MovieId, MovieVersionId = r.MovieVersionId, MovieFileId = r.MovieFileId,
            State = r.State, IsActive = r.IsActive, Unmonitor = r.Unmonitor, MoviePath = r.MoviePath,
            RelativePath = r.Snapshot.File.RelativePath, ExtraIndex = r.ExtraIndex, ExtraCount = r.Snapshot.Extras.Count,
            CurrentExtra = r.ExtraIndex < r.Snapshot.Extras.Count ? r.Snapshot.Extras[r.ExtraIndex].RelativePath : null,
            SourceExisted = r.Snapshot.SourceExisted, SourceSize = r.Snapshot.SourceSize, SourceLastWrite = r.Snapshot.SourceLastWrite,
            RecyclePath = r.RecyclePath, Created = r.Created, Updated = r.Updated, Note = r.Note
        }).ToList();

        [HttpPost("{operationId:int}/resume")]
        public IActionResult Resume(int movieId, int operationId, [FromBody] VersionFileDeletionResumeResource request)
        {
            _recovery.Resume(movieId, operationId, request?.RetryOriginalSource ?? false, request?.PreserveChangedExtras ?? false, request?.Note);
            return NoContent();
        }

        [HttpPost("{operationId:int}/cancel")]
        public IActionResult Cancel(int movieId, int operationId, [FromBody] VersionFileDeletionResumeResource request)
        {
            _recovery.Cancel(movieId, operationId, request?.Note);
            return NoContent();
        }
    }

    public class VersionFileDeletionResumeResource
    {
        public bool RetryOriginalSource { get; set; }
        public bool PreserveChangedExtras { get; set; }
        public string Note { get; set; }
    }

    public class VersionFileDeletionResource
    {
        public int Id { get; set; }
        public int MovieId { get; set; }
        public int MovieVersionId { get; set; }
        public int MovieFileId { get; set; }
        public string MoviePath { get; set; }
        public string RelativePath { get; set; }
        public VersionFileDeletionState State { get; set; }
        public bool IsActive { get; set; }
        public bool Unmonitor { get; set; }
        public int ExtraIndex { get; set; }
        public int ExtraCount { get; set; }
        public string CurrentExtra { get; set; }
        public long SourceSize { get; set; }
        public bool SourceExisted { get; set; }
        public DateTime SourceLastWrite { get; set; }
        public string RecyclePath { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public string Note { get; set; }
    }
}
