using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using Radarr.Http;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/{movieId:int}/versions")]
    public class MovieVersionsController : Controller
    {
        private readonly IMovieService _movies;
        private readonly IMovieVersionStatusService _statuses;
        private readonly IMovieVersionFileDeletionService _deletions;

        public MovieVersionsController(IMovieService movies, IMovieVersionStatusService statuses, IMovieVersionFileDeletionService deletions)
        {
            _movies = movies;
            _statuses = statuses;
            _deletions = deletions;
        }

        [HttpGet]
        public List<MovieVersionResource> Get(int movieId)
        {
            return _statuses.Get(_movies.GetMovie(movieId)).Select(s => new MovieVersionResource
            {
                Id = s.Version.Id,
                MovieId = s.Version.MovieId,
                CompositeProfileVersionId = s.Version.CompositeProfileVersionId,
                QualityProfileId = s.Version.QualityProfile.Id,
                Label = s.Version.Label,
                Priority = s.Version.Priority,
                IsPrimary = s.Version.IsPrimary,
                Monitored = s.Version.Monitored,
                MovieFileId = s.Version.MovieFileId,
                HasFile = s.HasFile,
                Quality = s.File?.Quality,
                SizeOnDisk = s.File?.Size ?? 0,
                QualityCutoffNotMet = s.QualityCutoffNotMet,
                CutoffNotMet = s.CutoffNotMet,
                CustomFormatScore = s.CustomFormatScore,
                Error = s.Error
            }).ToList();
        }

        [HttpDelete("{movieVersionId:int}/file/{movieFileId:int}")]
        public IActionResult DeleteFile(int movieId, int movieVersionId, int movieFileId, bool unmonitor = true)
        {
            _deletions.Delete(movieId, movieVersionId, movieFileId, unmonitor);
            return NoContent();
        }
    }

    public class MovieVersionResource
    {
        public int? Id { get; set; }
        public int MovieId { get; set; }
        public int? CompositeProfileVersionId { get; set; }
        public int QualityProfileId { get; set; }
        public string Label { get; set; }
        public int Priority { get; set; }
        public bool IsPrimary { get; set; }
        public bool Monitored { get; set; }
        public int? MovieFileId { get; set; }
        public bool HasFile { get; set; }
        public QualityModel Quality { get; set; }
        public long SizeOnDisk { get; set; }
        public bool QualityCutoffNotMet { get; set; }
        public bool CutoffNotMet { get; set; }
        public int CustomFormatScore { get; set; }
        public string Error { get; set; }
        public bool IsImplicit => !Id.HasValue;
    }
}
