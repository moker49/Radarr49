using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileService
    {
        List<CompositeProfileVersion> Get(int profileId);
        List<CompositeProfileVersion> Save(int profileId, List<CompositeProfileVersion> definitions);
        void Delete(int profileId);
    }

    public class CompositeProfileService : ICompositeProfileService
    {
        private readonly ICompositeProfileVersionRepository _definitions;
        private readonly IQualityProfileRepository _profiles;
        private readonly IMovieRepository _movies;
        private readonly IMovieVersionRepository _versions;

        public CompositeProfileService(ICompositeProfileVersionRepository definitions,
                                       IQualityProfileRepository profiles,
                                       IMovieRepository movies,
                                       IMovieVersionRepository versions)
        {
            _definitions = definitions;
            _profiles = profiles;
            _movies = movies;
            _versions = versions;
        }

        public List<CompositeProfileVersion> Get(int profileId)
        {
            _profiles.Get(profileId);
            return _definitions.ForProfile(profileId).OrderBy(v => v.Priority).ToList();
        }

        public List<CompositeProfileVersion> Save(int profileId, List<CompositeProfileVersion> definitions)
        {
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                return SaveDefinitions(profileId, definitions);
            }
        }

        private List<CompositeProfileVersion> SaveDefinitions(int profileId, List<CompositeProfileVersion> definitions)
        {
            _profiles.Get(profileId);
            if (definitions == null)
            {
                throw Invalid("Two version definitions are required.");
            }

            new CompositeProfileValidator().ValidateAndThrow(definitions);

            var existing = _definitions.ForProfile(profileId);
            var all = _definitions.All().ToList();

            EnsureUnused(profileId, existing);

            if (all.Any(v => v.QualityProfileId == profileId) ||
                definitions.Any(v => v.QualityProfileId == profileId || all.Any(d => d.CompositeProfileId == v.QualityProfileId)))
            {
                throw Invalid("Composite profiles cannot contain composite profiles or themselves.");
            }

            var incomingIds = definitions.Where(v => v.Id != 0).Select(v => v.Id).ToList();
            if (!existing.Select(v => v.Id).OrderBy(id => id).SequenceEqual(incomingIds.OrderBy(id => id)))
            {
                throw Invalid("Preserve the existing version IDs when editing a composite. Refresh and try again.");
            }

            foreach (var definition in definitions)
            {
                if (!_profiles.Exists(definition.QualityProfileId))
                {
                    throw Invalid("Every version must reference an existing ordinary quality profile.");
                }
            }

            var models = definitions.Select(v => new CompositeProfileVersion
            {
                Id = v.Id,
                CompositeProfileId = profileId,
                QualityProfileId = v.QualityProfileId,
                Label = v.Label,
                Priority = v.Priority,
                IsPrimary = v.IsPrimary
            }).ToList();

            _definitions.SaveDefinitions(profileId, models);
            return models.OrderBy(v => v.Priority).ToList();
        }

        public void Delete(int profileId)
        {
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                _profiles.Get(profileId);
                EnsureUnused(profileId, _definitions.ForProfile(profileId));
                _definitions.DeleteDefinitions(profileId);
            }
        }

        private void EnsureUnused(int profileId, List<CompositeProfileVersion> existing)
        {
            if (_movies.All().Any(m => m.QualityProfileId == profileId) ||
                _versions.All().Any(v => existing.Any(d => d.Id == v.CompositeProfileVersionId)))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "An assigned profile cannot be reconfigured without an explicit version conversion.");
            }
        }

        private static ValidationException Invalid(string message)
        {
            return new ValidationException(new[] { new ValidationFailure("Versions", message) });
        }
    }
}
