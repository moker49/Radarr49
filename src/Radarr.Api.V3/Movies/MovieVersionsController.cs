using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using Radarr.Http;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/{movieId:int}/versions")]
    public class MovieVersionsController : Controller
    {
        private readonly IMovieService _movies;
        private readonly IMovieVersionService _versions;
        private readonly IMovieVersionFileDeletionService _deletions;

        public MovieVersionsController(IMovieService movies, IMovieVersionService versions, IMovieVersionFileDeletionService deletions)
        {
            _movies = movies;
            _versions = versions;
            _deletions = deletions;
        }

        [HttpGet]
        public List<MovieVersionResource> Get(int movieId)
        {
            return _versions.Resolve(_movies.GetMovie(movieId)).Select(v => new MovieVersionResource
            {
                Id = v.Id,
                MovieId = v.MovieId,
                CompositeProfileVersionId = v.CompositeProfileVersionId,
                QualityProfileId = v.QualityProfile.Id,
                Label = v.Label,
                Priority = v.Priority,
                IsPrimary = v.IsPrimary,
                Monitored = v.Monitored,
                MovieFileId = v.MovieFileId
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
        public bool IsImplicit => !Id.HasValue;
    }
}
