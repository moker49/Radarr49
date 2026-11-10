using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Movies;
using Radarr.Http;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/managed")]
    public class ManagedMovieDeletionController : Controller
    {
        private readonly IManagedMovieDeletionService _deletion;

        public ManagedMovieDeletionController(IManagedMovieDeletionService deletion)
        {
            _deletion = deletion;
        }

        [HttpDelete("{id:int}")]
        public IActionResult Delete(int id, bool deleteFiles = false, bool addImportExclusion = false)
        {
            _deletion.Delete(new List<int> { id }, deleteFiles, addImportExclusion);
            return NoContent();
        }

        [HttpDelete]
        public IActionResult DeleteMany([FromBody] ManagedMovieDeletionResource request)
        {
            if (request?.MovieIds == null || request.MovieIds.Count == 0)
            {
                return BadRequest("At least one movie must be selected.");
            }

            _deletion.Delete(request.MovieIds, request.DeleteFiles, request.AddImportExclusion);
            return NoContent();
        }
    }

    public class ManagedMovieDeletionResource
    {
        public List<int> MovieIds { get; set; }
        public bool DeleteFiles { get; set; }
        public bool AddImportExclusion { get; set; }
    }
}
