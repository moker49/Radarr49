using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    public interface IMovieVersionAssignmentService
    {
        List<MovieVersion> Assign(int movieId, int compositeProfileId, Dictionary<int, int?> fileAssignments = null);
    }

    // Backend conversion primitive. The test activation API adds new movies only;
    // existing-movie conversion is not publicly exposed.
    public class MovieVersionAssignmentService : IMovieVersionAssignmentService
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieVersionRepository _versions;
        private readonly ICompositeProfileVersionRepository _definitions;
        private readonly IMediaFileService _files;
        private readonly IQualityProfileRepository _profiles;

        public MovieVersionAssignmentService(IMovieRepository movies,
                                             IMovieVersionRepository versions,
                                             ICompositeProfileVersionRepository definitions,
                                             IMediaFileService files,
                                             IQualityProfileRepository profiles)
        {
            _movies = movies;
            _versions = versions;
            _definitions = definitions;
            _files = files;
            _profiles = profiles;
        }

        public List<MovieVersion> Assign(int movieId, int compositeProfileId, Dictionary<int, int?> fileAssignments = null)
        {
            lock (MovieVersionImportSynchronization.ForMovie(movieId))
            {
                lock (CompositeProfileSynchronization.SyncRoot)
                {
                    return AssignVersions(movieId, compositeProfileId, fileAssignments);
                }
            }
        }

        private List<MovieVersion> AssignVersions(int movieId, int compositeProfileId, Dictionary<int, int?> fileAssignments)
        {
            var movie = _movies.Get(movieId);
            _profiles.Get(compositeProfileId);
            var definitions = _definitions.ForProfile(compositeProfileId);
            new CompositeProfileValidator().ValidateAndThrow(definitions);
            if (definitions.Any(v => !_profiles.Exists(v.QualityProfileId) || _definitions.ForProfile(v.QualityProfileId).Any()))
            {
                throw Invalid("Every version must reference an existing ordinary profile.");
            }

            var files = _files.GetFilesByMovie(movieId);
            if (movie.MovieFileId > 0 && files.All(f => f.Id != movie.MovieFileId))
            {
                throw Invalid("The movie's existing file is missing from the database. Resolve that before assigning versions.");
            }

            if (fileAssignments == null)
            {
                if (files.Any())
                {
                    throw Invalid("Explicitly assign existing files to versions before converting this movie.");
                }

                fileAssignments = definitions.ToDictionary(d => d.Id, _ => (int?)null);
            }

            var assignedIds = fileAssignments.Values.Where(id => id.HasValue).Select(id => id.Value).ToList();
            if (!definitions.Select(d => d.Id).ToHashSet().SetEquals(fileAssignments.Keys) ||
                assignedIds.Any(id => id <= 0) || assignedIds.Distinct().Count() != assignedIds.Count ||
                !files.Select(f => f.Id).ToHashSet().SetEquals(assignedIds))
            {
                throw Invalid("Specify each version once and assign every existing movie file exactly once. Files must belong to this movie.");
            }

            var existing = _versions.ForMovie(movieId);
            if (existing.Any())
            {
                if (movie.QualityProfileId == compositeProfileId && existing.Count == definitions.Count &&
                    existing.All(v => fileAssignments.TryGetValue(v.CompositeProfileVersionId, out var fileId) && fileId == v.MovieFileId))
                {
                    return existing;
                }

                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Movie versions already exist. Reassignment requires an explicit reconciliation operation.");
            }

            var versions = definitions.OrderBy(d => d.Priority).Select(d => new MovieVersion
            {
                MovieId = movieId,
                CompositeProfileVersionId = d.Id,
                Monitored = true,
                MovieFileId = fileAssignments[d.Id]
            }).ToList();

            var primaryFileId = fileAssignments[definitions.Single(d => d.IsPrimary).Id];
            _versions.AssignComposite(movie, compositeProfileId, versions, primaryFileId);
            return versions;
        }

        private static ValidationException Invalid(string message)
        {
            return new ValidationException(new[] { new ValidationFailure("Versions", message) });
        }
    }
}
