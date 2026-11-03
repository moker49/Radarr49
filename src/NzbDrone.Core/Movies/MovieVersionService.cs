using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    public interface IMovieVersionService
    {
        List<ResolvedMovieVersion> Resolve(Movie movie);
        ResolvedMovieVersion Get(Movie movie, int movieVersionId);
        Movie ForVersion(Movie movie, int movieVersionId);
    }

    public class MovieVersionService : IMovieVersionService
    {
        private readonly ICompositeProfileVersionRepository _definitions;
        private readonly IMovieVersionRepository _versions;
        private readonly IQualityProfileRepository _profiles;
        private readonly IMediaFileService _files;
        private readonly IVersionFileDeletionRepository _deletions;

        public MovieVersionService(ICompositeProfileVersionRepository definitions,
                                   IMovieVersionRepository versions,
                                   IQualityProfileRepository profiles,
                                   IMediaFileService files,
                                   IVersionFileDeletionRepository deletions)
        {
            _definitions = definitions;
            _versions = versions;
            _profiles = profiles;
            _files = files;
            _deletions = deletions;
        }

        public ResolvedMovieVersion Get(Movie movie, int movieVersionId)
        {
            if (movieVersionId <= 0)
            {
                throw new InvalidOperationException("A persisted managed version ID is required.");
            }

            return Resolve(movie).SingleOrDefault(v => v.Id == movieVersionId) ??
                throw new InvalidOperationException("The download's managed version no longer belongs to this movie and profile.");
        }

        public Movie ForVersion(Movie movie, int movieVersionId)
        {
            _deletions.CheckReady(movie.Id, movieVersionId);
            var version = Get(movie, movieVersionId);
            var file = version.MovieFileId.HasValue ? _files.GetMovie(version.MovieFileId.Value) : null;
            if (version.MovieFileId.HasValue && (file == null || file.MovieId != movie.Id))
            {
                throw new InvalidOperationException("The managed version's file does not belong to this movie.");
            }

            return movie.WithVersion(version, file);
        }

        public List<ResolvedMovieVersion> Resolve(Movie movie)
        {
            var definitions = _definitions.ForProfile(movie.QualityProfileId);

            if (definitions.Count == 0)
            {
                return new List<ResolvedMovieVersion>
                {
                    new ResolvedMovieVersion
                    {
                        MovieId = movie.Id,
                        QualityProfile = _profiles.Get(movie.QualityProfileId),
                        IsPrimary = true,
                        Monitored = movie.Monitored,
                        MovieFileId = movie.MovieFileId > 0 ? movie.MovieFileId : null
                    }
                };
            }

            if (definitions.Count < 2 || definitions.Count(v => v.IsPrimary) != 1 ||
                definitions.Any(v => string.IsNullOrWhiteSpace(v.Label) || v.Priority < 0) ||
                definitions.Select(v => v.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Count ||
                definitions.Select(v => v.Priority).Distinct().Count() != definitions.Count)
            {
                throw new InvalidOperationException("Composite profile requires distinct labels and priorities and exactly one primary version.");
            }

            var versions = _versions.ForMovie(movie.Id);
            var result = new List<ResolvedMovieVersion>();
            var assignedFiles = new HashSet<int>();

            foreach (var definition in definitions.OrderBy(v => v.Priority))
            {
                if (_definitions.ForProfile(definition.QualityProfileId).Count != 0)
                {
                    throw new InvalidOperationException("Composite profiles cannot contain composite profiles.");
                }

                var assignments = versions.Where(v => v.CompositeProfileVersionId == definition.Id).ToList();
                if (assignments.Count != 1)
                {
                    throw new InvalidOperationException("Each composite version must have an explicit movie assignment before it can be used.");
                }

                var version = assignments[0];
                if (version.MovieFileId.HasValue &&
                    (version.MovieFileId.Value <= 0 || !assignedFiles.Add(version.MovieFileId.Value)))
                {
                    throw new InvalidOperationException("A movie file can belong to only one managed version.");
                }

                result.Add(new ResolvedMovieVersion
                {
                    Id = version.Id,
                    MovieId = movie.Id,
                    CompositeProfileVersionId = definition.Id,
                    Label = definition.Label,
                    Priority = definition.Priority,
                    IsPrimary = definition.IsPrimary,
                    Monitored = movie.Monitored && version.Monitored,
                    MovieFileId = version.MovieFileId,
                    QualityProfile = _profiles.Get(definition.QualityProfileId)
                });
            }

            return result;
        }
    }
}
