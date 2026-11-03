using System.Collections.Generic;
using System.Linq;
using System.Net;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfilePolicy
    {
        void EnsureAssignable(int profileId);
        bool EnsureNewMovieAllowed(Movie movie);
        void EnsureDownloadAllowed(Movie movie, int? movieVersionId);
        void EnsureLegacyMutationAllowed(IEnumerable<Movie> movies);
        void EnsureLegacyProfileEditAllowed(int profileId);
        void EnsureProfileDeletionAllowed(int profileId);
    }

    public class CompositeProfilePolicy : ICompositeProfilePolicy
    {
        private readonly ICompositeProfileVersionRepository _definitions;
        private readonly IMovieVersionRepository _versions;
        private readonly IQualityProfileRepository _profiles;
        private readonly IRootFolderService _roots;
        private readonly IDiskProvider _disk;

        public CompositeProfilePolicy(ICompositeProfileVersionRepository definitions, IMovieVersionRepository versions, IQualityProfileRepository profiles, IRootFolderService roots, IDiskProvider disk)
        {
            _definitions = definitions;
            _versions = versions;
            _profiles = profiles;
            _roots = roots;
            _disk = disk;
        }

        public bool EnsureNewMovieAllowed(Movie movie)
        {
            if (!_definitions.ForProfile(movie.QualityProfileId).Any())
            {
                return false;
            }

            var profile = _profiles.Get(movie.QualityProfileId);
            CompositeProfileActivation.ValidateNewMovie(movie, profile);
            if (!_roots.All().Any(r => r.Path.PathEquals(profile.CompositeTestRoot)) || !_disk.FolderExists(profile.CompositeTestRoot) ||
                (_disk.FolderExists(movie.Path) && _disk.GetFiles(movie.Path, true).Any()))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Composite additions require the activated root to remain configured and the movie directory to contain no existing files.");
            }

            return true;
        }

        public void EnsureDownloadAllowed(Movie movie, int? movieVersionId)
        {
            var definitions = _definitions.ForProfile(movie.QualityProfileId);
            if (!definitions.Any())
            {
                if (movieVersionId.HasValue || _versions.ForMovie(movie.Id).Any())
                {
                    throw new NzbDroneClientException(HttpStatusCode.Conflict, "The movie no longer has its composite profile.");
                }

                return;
            }

            CompositeProfileActivation.ValidatePath(movie, _profiles.Get(movie.QualityProfileId));
            if (movieVersionId is not > 0 || !_versions.ForMovie(movie.Id).Any(v => v.Id == movieVersionId && definitions.Any(d => d.Id == v.CompositeProfileVersionId)))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Composite downloads require an explicit assigned version.");
            }
        }

        public void EnsureAssignable(int profileId)
        {
            if (_definitions.ForProfile(profileId).Any())
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Changing an existing movie to a composite requires explicit version conversion.");
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
