using System.IO;
using System.Net;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Profiles.Qualities
{
    public static class CompositeProfileActivation
    {
        public static void ValidateNewMovie(Movie movie, QualityProfile profile)
        {
            ValidatePath(movie, profile);
            if (movie.Id != 0 || movie.MovieFileId != 0)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Existing movies require explicit version conversion.");
            }
        }

        public static void ValidatePath(Movie movie, QualityProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile?.CompositeTestRoot))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Activate the composite profile in a dedicated test root before adding movies.");
            }

            if (string.IsNullOrWhiteSpace(movie.Path) ||
                !Path.IsPathFullyQualified(movie.Path) || !profile.CompositeTestRoot.IsParentPath(Path.GetFullPath(movie.Path)))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Composite test activation permits only movies beneath its configured test root.");
            }
        }
    }
}
