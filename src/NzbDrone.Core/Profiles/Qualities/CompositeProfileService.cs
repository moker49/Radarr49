using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileService
    {
        List<CompositeProfileVersion> Get(int profileId);
        List<CompositeProfileVersion> Save(int profileId, List<CompositeProfileVersion> definitions, int maximumConcurrentDownloads = 2);
        int GetMaximumConcurrentDownloads(int profileId);
        string GetTestRoot(int profileId);
        void SetTestRoot(int profileId, string rootFolderPath);
        void Delete(int profileId);
    }

    public class CompositeProfileService : ICompositeProfileService
    {
        private readonly ICompositeProfileVersionRepository _definitions;
        private readonly IQualityProfileRepository _profiles;
        private readonly IMovieRepository _movies;
        private readonly IMovieVersionRepository _versions;
        private readonly IRootFolderService _roots;
        private readonly IDiskProvider _disk;

        public CompositeProfileService(ICompositeProfileVersionRepository definitions,
                                       IQualityProfileRepository profiles,
                                       IMovieRepository movies,
                                       IMovieVersionRepository versions,
                                       IRootFolderService roots,
                                       IDiskProvider disk)
        {
            _definitions = definitions;
            _profiles = profiles;
            _movies = movies;
            _versions = versions;
            _roots = roots;
            _disk = disk;
        }

        public List<CompositeProfileVersion> Get(int profileId)
        {
            _profiles.Get(profileId);
            return _definitions.ForProfile(profileId).OrderBy(v => v.Priority).ToList();
        }

        public int GetMaximumConcurrentDownloads(int profileId)
        {
            _profiles.Get(profileId);
            var count = _definitions.ForProfile(profileId).Count;
            return count == 0 ? 1 : count;
        }

        public string GetTestRoot(int profileId)
        {
            return _profiles.Get(profileId).CompositeTestRoot;
        }

        public void SetTestRoot(int profileId, string rootFolderPath)
        {
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                _profiles.Get(profileId);
                var definitions = _definitions.ForProfile(profileId);
                EnsureUnused(profileId, definitions);
                if (rootFolderPath != null)
                {
                    if (string.IsNullOrWhiteSpace(rootFolderPath) || !Path.IsPathFullyQualified(rootFolderPath))
                    {
                        throw Invalid("An absolute configured test root is required.");
                    }

                    rootFolderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolderPath));
                    if (!_roots.All().Any(r => r.Path.PathEquals(rootFolderPath)) || !_disk.FolderExists(rootFolderPath))
                    {
                        throw Invalid("The test root must exist and be configured as a root folder.");
                    }

                    if (_movies.All().Any(m => rootFolderPath.PathEquals(m.Path) || rootFolderPath.IsParentPath(m.Path)))
                    {
                        throw Invalid("Choose a dedicated test root containing no managed movies.");
                    }

                    new CompositeProfileValidator().ValidateAndThrow(definitions);
                    if (definitions.Any(v => !_profiles.Exists(v.QualityProfileId) || _definitions.ForProfile(v.QualityProfileId).Any()))
                    {
                        throw Invalid("Every version must reference an existing ordinary profile.");
                    }
                }

                _definitions.SetTestRoot(profileId, rootFolderPath);
            }
        }

        public List<CompositeProfileVersion> Save(int profileId, List<CompositeProfileVersion> definitions, int maximumConcurrentDownloads = 2)
        {
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                return SaveDefinitions(profileId, definitions, maximumConcurrentDownloads);
            }
        }

        private List<CompositeProfileVersion> SaveDefinitions(int profileId, List<CompositeProfileVersion> definitions, int maximumConcurrentDownloads)
        {
            EnsureDraft(profileId);
            if (maximumConcurrentDownloads is < 1 or > 2)
            {
                throw Invalid("Maximum concurrent downloads must be 1 or 2.");
            }

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

            // Retain the old aggregate field for API/storage compatibility. Scheduling
            // now enforces one slot per version, independently of its previous value.
            _definitions.SaveDefinitions(profileId, models, models.Count);
            return models.OrderBy(v => v.Priority).ToList();
        }

        public void Delete(int profileId)
        {
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                EnsureDraft(profileId);
                EnsureUnused(profileId, _definitions.ForProfile(profileId));
                _definitions.DeleteDefinitions(profileId);
            }
        }

        private void EnsureDraft(int profileId)
        {
            if (!string.IsNullOrWhiteSpace(_profiles.Get(profileId).CompositeTestRoot))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Deactivate the unused composite before editing its definitions.");
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
