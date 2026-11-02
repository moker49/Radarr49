using System.Collections.Generic;
using System.Linq;
using System.Net;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfilePolicy
    {
        void EnsureAssignable(int profileId);
        void EnsureLegacyMutationAllowed(IEnumerable<Movie> movies);
        void EnsureLegacyProfileEditAllowed(int profileId);
        void EnsureProfileDeletionAllowed(int profileId);
    }

    public class CompositeProfilePolicy : ICompositeProfilePolicy
    {
        private readonly ICompositeProfileVersionRepository _definitions;
        private readonly IMovieVersionRepository _versions;

        public CompositeProfilePolicy(ICompositeProfileVersionRepository definitions, IMovieVersionRepository versions)
        {
            _definitions = definitions;
            _versions = versions;
        }

        public void EnsureAssignable(int profileId)
        {
            if (_definitions.ForProfile(profileId).Any())
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Composite profiles are drafts until version-aware downloads and imports are enabled. Select an ordinary profile for now.");
            }
        }

        public void EnsureLegacyMutationAllowed(IEnumerable<Movie> movies)
        {
            // Preflight the entire request before the caller makes any changes.
            foreach (var movie in movies)
            {
                if (_definitions.ForProfile(movie.QualityProfileId).Any() || _versions.ForMovie(movie.Id).Any())
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "Movie {0} is composite-managed. This operation requires the version-aware API.", movie.Id);
                }
            }
        }

        public void EnsureLegacyProfileEditAllowed(int profileId)
        {
            if (_definitions.ForProfile(profileId).Any())
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Composite profile definitions must be edited through the composite profile API.");
            }
        }

        public void EnsureProfileDeletionAllowed(int profileId)
        {
            if (_definitions.All().Any(v => v.CompositeProfileId == profileId || v.QualityProfileId == profileId))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "This profile defines a composite or is referenced by one and cannot be deleted.");
            }
        }
    }
}
