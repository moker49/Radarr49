using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaFiles;
using Radarr.Http;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/{movieId:int}/unmanagedfile")]
    public class UnmanagedMovieFilesController : Controller
    {
        private readonly IDeleteMediaFiles _files;

        public UnmanagedMovieFilesController(IDeleteMediaFiles files)
        {
            _files = files;
        }

        [HttpDelete("{movieFileId:int}")]
        public IActionResult Delete(int movieId, int movieFileId)
        {
            _files.DeleteUnmanagedFile(movieId, movieFileId);
            return NoContent();
        }
    }
}
