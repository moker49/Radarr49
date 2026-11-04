using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using Dapper;
using FluentValidation;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileEditorRepository
    {
        int Save(QualityProfile profile, bool enabled, List<CompositeProfileVersion> versions, string rootFolder);
    }

    public class CompositeProfileEditorRepository : BasicRepository<QualityProfile>, ICompositeProfileEditorRepository
    {
        private readonly IRootFolderService _roots;
        private readonly IDiskProvider _disk;

        public CompositeProfileEditorRepository(IMainDatabase database, IEventAggregator events, IRootFolderService roots, IDiskProvider disk)
            : base(database, events)
        {
            _roots = roots;
            _disk = disk;
        }

        public int Save(QualityProfile profile, bool enabled, List<CompositeProfileVersion> versions, string rootFolder)
        {
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                versions ??= new List<CompositeProfileVersion>();
                if (enabled)
                {
                    new CompositeProfileValidator().ValidateAndThrow(versions);
                    if (!string.IsNullOrWhiteSpace(rootFolder))
                    {
                        Require(Path.IsPathFullyQualified(rootFolder), "Choose a configured root folder.");
                        rootFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
                        Require(_roots.All().Any(r => r.Path.PathEquals(rootFolder)) && _disk.FolderExists(rootFolder), "The selected root folder must exist and be configured.");
                    }
                }

                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

                // Serialize definition changes against profile assignment and other editors.
                connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\" = \"Name\"", transaction: transaction);
                var created = profile.Id == 0;
                var stored = created ? null : connection.QuerySingleOrDefault<QualityProfile>("SELECT * FROM \"QualityProfiles\" WHERE \"Id\" = @Id", profile, transaction);
                Require(created || stored != null, "The profile no longer exists. Refresh and try again.");
                var all = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\"", transaction: transaction).ToList();
                var existing = created ? new List<CompositeProfileVersion>() : all.Where(v => v.CompositeProfileId == profile.Id).ToList();
                Require(versions.Where(v => v.Id != 0).Select(v => v.Id).Distinct().Count() == versions.Count(v => v.Id != 0), "Version definition IDs must be distinct.");
                var used = !created && (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Movies\" WHERE \"QualityProfileId\" = @Id", profile, transaction) != 0 ||
                    connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" v JOIN \"CompositeProfileVersions\" d ON v.\"CompositeProfileVersionId\" = d.\"Id\" WHERE d.\"CompositeProfileId\" = @Id", profile, transaction) != 0);

                if (enabled)
                {
                    Require(created || !all.Any(v => v.QualityProfileId == profile.Id), "A profile referenced by a composite cannot itself become composite.");
                    foreach (var version in versions)
                    {
                        Require((created || version.QualityProfileId != profile.Id) && !all.Any(v => v.CompositeProfileId == version.QualityProfileId) &&
                            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"QualityProfiles\" WHERE \"Id\" = @QualityProfileId", version, transaction) == 1,
                            "Select existing non-composite profiles, excluding this profile.");
                        Require(version.Id == 0 || existing.Any(v => v.Id == version.Id), "A version definition changed. Refresh and try again.");
                    }
                }

                if (used)
                {
                    Require(enabled && existing.Count == versions.Count && versions.All(v => existing.Any(e => e.Id == v.Id && e.QualityProfileId == v.QualityProfileId && e.Label == v.Label && e.IsPrimary == v.IsPrimary)),
                        "An assigned profile can change priority, but changing its versions or composite mode requires an explicit conversion.");
                    Require(string.Equals(stored.CompositeTestRoot, rootFolder, StringComparison.OrdinalIgnoreCase), "An assigned composite cannot change its root folder.");
                }

                if (created)
                {
                    profile = Insert(connection, transaction, profile);
                }
                else if (enabled)
                {
                    connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\" = @Name WHERE \"Id\" = @Id", profile, transaction);
                }
                else
                {
                    profile.SizeDefinitions ??= stored.SizeDefinitions;
                    profile.UseGlobalSizeDefinitions ??= stored.UseGlobalSizeDefinitions;
                    connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\"=@Name, \"Cutoff\"=@Cutoff, \"Items\"=@Items, \"MinFormatScore\"=@MinFormatScore, \"CutoffFormatScore\"=@CutoffFormatScore, \"MinUpgradeFormatScore\"=@MinUpgradeFormatScore, \"FormatItems\"=@FormatItems, \"Language\"=@Language, \"UpgradeAllowed\"=@UpgradeAllowed, \"SizeDefinitions\"=@SizeDefinitions, \"UseGlobalSizeDefinitions\"=@UseGlobalSizeDefinitions WHERE \"Id\"=@Id", profile, transaction);
                }

                var profileId = profile.Id;
                var keep = enabled ? versions.Select(v => v.Id).ToHashSet() : new HashSet<int>();
                foreach (var removed in existing.Where(v => !keep.Contains(v.Id)))
                {
                    connection.Execute("DELETE FROM \"CompositeProfileVersions\" WHERE \"Id\" = @Id", removed, transaction);
                }

                connection.Execute("UPDATE \"CompositeProfileVersions\" SET \"Priority\" = -\"Id\" WHERE \"CompositeProfileId\" = @profileId", new { profileId }, transaction);
                if (enabled)
                {
                    foreach (var version in versions)
                    {
                        version.CompositeProfileId = profileId;
                        if (version.Id == 0)
                        {
                            connection.Execute("INSERT INTO \"CompositeProfileVersions\" (\"CompositeProfileId\", \"QualityProfileId\", \"Label\", \"Priority\", \"IsPrimary\") VALUES (@CompositeProfileId, @QualityProfileId, @Label, @Priority, @IsPrimary)", version, transaction);
                        }
                        else
                        {
                            connection.Execute("UPDATE \"CompositeProfileVersions\" SET \"QualityProfileId\"=@QualityProfileId, \"Label\"=@Label, \"Priority\"=@Priority, \"IsPrimary\"=@IsPrimary WHERE \"Id\"=@Id AND \"CompositeProfileId\"=@CompositeProfileId", version, transaction);
                        }
                    }
                }

                connection.Execute("UPDATE \"QualityProfiles\" SET \"CompositeTestRoot\"=@root, \"MaximumConcurrentDownloads\"=@limit WHERE \"Id\"=@profileId", new { profileId, root = enabled ? rootFolder : null, limit = enabled ? (int?)versions.Count : null }, transaction);
                transaction.Commit();
                if (created)
                {
                    ModelCreated(profile);
                }
                else
                {
                    ModelUpdated(profile);
                }

                return profileId;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, message);
            }
        }
    }
}
