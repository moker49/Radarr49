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
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileEditorRepository
    {
        int Save(QualityProfile profile, bool enabled, List<CompositeProfileVersion> versions, string rootFolder, int? retainChildProfileId = null);
        Dictionary<int, int> GetMovieCounts();
    }

    public class CompositeProfileEditorRepository : BasicRepository<QualityProfile>, ICompositeProfileEditorRepository
    {
        private readonly IRootFolderService _roots;
        private readonly IDiskProvider _disk;
        private readonly IMovieRepository _movies;
        private readonly ITrackedDownloadService _tracked;
        private readonly IEventAggregator _events;

        public CompositeProfileEditorRepository(IMainDatabase database, IEventAggregator events, IRootFolderService roots, IDiskProvider disk, IMovieRepository movies, ITrackedDownloadService tracked)
            : base(database, events)
        {
            _roots = roots;
            _disk = disk;
            _movies = movies;
            _tracked = tracked;
            _events = events;
        }

        public int Save(QualityProfile profile, bool enabled, List<CompositeProfileVersion> versions, string rootFolder, int? retainChildProfileId = null)
        {
            List<int> movieIds;
            using (var connection = _database.OpenConnection())
            {
                movieIds = connection.Query<int>("SELECT \"Id\" FROM \"Movies\" WHERE \"QualityProfileId\"=@Id", profile).ToList();
            }

            using var locks = new MovieManagementLocks(movieIds);
            return SaveCore(profile, enabled, versions, rootFolder, retainChildProfileId, movieIds);
        }

        public Dictionary<int, int> GetMovieCounts()
        {
            using var connection = _database.OpenConnection();
            return connection.Query<ProfileMovieCount>("SELECT \"QualityProfileId\" AS \"ProfileId\", COUNT(*) AS \"MovieCount\" FROM \"Movies\" GROUP BY \"QualityProfileId\"").ToDictionary(r => r.ProfileId, r => r.MovieCount);
        }

        private sealed class ProfileMovieCount
        {
            public int ProfileId { get; set; }
            public int MovieCount { get; set; }
        }

        private int SaveCore(QualityProfile profile, bool enabled, List<CompositeProfileVersion> versions, string rootFolder, int? retainChildProfileId, List<int> expectedMovieIds)
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
                var affected = created ? new List<Movie>() : connection.Query<Movie>("SELECT * FROM \"Movies\" WHERE \"QualityProfileId\"=@Id ORDER BY \"Id\"", profile, transaction).ToList();
                Require(expectedMovieIds.ToHashSet().SetEquals(affected.Select(m => m.Id)), "The affected movies changed. Refresh before saving this profile conversion.");
                Require(versions.Where(v => v.Id != 0).Select(v => v.Id).Distinct().Count() == versions.Count(v => v.Id != 0), "Version definition IDs must be distinct.");
                var used = !created && (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"Movies\" WHERE \"QualityProfileId\" = @Id", profile, transaction) != 0 ||
                    connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"MovieVersions\" v JOIN \"CompositeProfileVersions\" d ON v.\"CompositeProfileVersionId\" = d.\"Id\" WHERE d.\"CompositeProfileId\" = @Id AND v.\"Retired\"=FALSE", profile, transaction) != 0);

                if (used && enabled && existing.Count > 0)
                {
                    var replacements = new Queue<CompositeProfileVersion>(existing.Where(e => !versions.Any(v => v.Id == e.Id)).OrderBy(e => e.Priority));
                    foreach (var version in versions.OrderBy(v => v.Priority))
                    {
                        var slot = version.Id == 0 ? (replacements.Count > 0 ? replacements.Dequeue() : null) : existing.SingleOrDefault(e => e.Id == version.Id);
                        Require(slot != null, "The version definitions changed. Refresh and try again.");
                        Require(version.Id == 0 || version.Label == slot.Label, "Version labels remain stable while files are managed.");
                        version.Id = slot.Id;
                        version.Label = slot.Label;
                        version.IsPrimary = slot.IsPrimary;
                    }
                }

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
                    Require(enabled || existing.Any(d => d.QualityProfileId == retainChildProfileId), "Choose which child version stays managed. All other files will be kept as unmanaged copies.");
                    if (enabled && existing.Count > 0)
                    {
                        Require(string.Equals(stored.CompositeTestRoot, rootFolder, StringComparison.OrdinalIgnoreCase), "An assigned composite cannot change its root folder.");
                    }
                }

                var convertFiles = affected.Count > 0 && ((!enabled && existing.Count > 0) || (enabled && (existing.Count == 0 || versions.Any(v => !existing.Any(e => e.Id == v.Id && e.QualityProfileId == v.QualityProfileId)))));
                if (convertFiles)
                {
                    MovieManagementLocks.CheckTracked(_tracked, expectedMovieIds);
                    foreach (var movie in affected)
                    {
                        connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\"=\"MovieFileId\" WHERE \"Id\"=@Id", movie, transaction);
                        MovieProfileConversion.CheckIdle(connection, transaction, movie.Id);
                    }
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
                            var sql = "INSERT INTO \"CompositeProfileVersions\" (\"CompositeProfileId\", \"QualityProfileId\", \"Label\", \"Priority\", \"IsPrimary\") VALUES (@CompositeProfileId, @QualityProfileId, @Label, @Priority, @IsPrimary)";
                            sql += _database.DatabaseType == DatabaseType.PostgreSQL ? " RETURNING \"Id\"" : "; SELECT last_insert_rowid()";
                            version.Id = connection.ExecuteScalar<int>(sql, version, transaction);
                        }
                        else
                        {
                            connection.Execute("UPDATE \"CompositeProfileVersions\" SET \"QualityProfileId\"=@QualityProfileId, \"Label\"=@Label, \"Priority\"=@Priority, \"IsPrimary\"=@IsPrimary WHERE \"Id\"=@Id AND \"CompositeProfileId\"=@CompositeProfileId", version, transaction);
                        }
                    }
                }

                connection.Execute("UPDATE \"QualityProfiles\" SET \"CompositeTestRoot\"=@root, \"MaximumConcurrentDownloads\"=@limit WHERE \"Id\"=@profileId", new { profileId, root = enabled ? rootFolder : null, limit = enabled ? (int?)versions.Count : null }, transaction);
                foreach (var movie in affected.Where(_ => convertFiles))
                {
                    if (enabled)
                    {
                        profile.CompositeTestRoot = rootFolder;
                        CompositeProfileActivation.ValidatePath(movie, profile);
                    }

                    var fileId = MovieProfileConversion.Apply(connection, transaction, movie, profileId, existing, enabled ? versions : new List<CompositeProfileVersion>(), retainChildProfileId, retainChildProfileId);
                    connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\"=@fileId WHERE \"Id\"=@Id", new { movie.Id, fileId }, transaction);
                }

                transaction.Commit();
                if (affected.Count > 0)
                {
                    _events.PublishEvent(new MoviesBulkEditedEvent(affected.Select(m => _movies.Get(m.Id)).ToList()));
                    if (convertFiles)
                    {
                        _events.PublishEvent(new PendingReleasesUpdatedEvent());
                        foreach (var movie in affected)
                        {
                            foreach (var file in connection.Query<MovieFile>("SELECT * FROM \"MovieFiles\" WHERE \"MovieId\"=@Id", movie))
                            {
                                _events.PublishEvent(new MovieFileUpdatedEvent(file));
                            }
                        }
                    }
                }

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
