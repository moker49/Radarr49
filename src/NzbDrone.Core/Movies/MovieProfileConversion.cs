using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using Dapper;
using FluentValidation;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    public static class MovieProfileConversion
    {
        public static void CheckIdle(IDbConnection connection, IDbTransaction transaction, int movieId)
        {
            var args = new { movieId };
            Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"DownloadReservations\" WHERE \"MovieId\"=@movieId AND \"State\" IN (0,1,2,5)", args, transaction) == 0 &&
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileImports\" WHERE \"MovieId\"=@movieId AND \"State\" BETWEEN 0 AND 10", args, transaction) == 0 &&
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"VersionFileDeletions\" WHERE \"MovieId\"=@movieId AND \"State\" BETWEEN 0 AND 4", args, transaction) == 0,
                "Finish or reconcile this movie's downloads and file operations before changing its managed versions.");
        }

        public static int Apply(IDbConnection connection, IDbTransaction transaction, Movie movie, int targetProfileId, List<CompositeProfileVersion> source, List<CompositeProfileVersion> destination, int? retainChildProfileId = null, int? existingFileChildProfileId = null)
        {
            CheckIdle(connection, transaction, movie.Id);
            var args = new { movieId = movie.Id };
            var files = connection.Query<MovieFile>("SELECT * FROM \"MovieFiles\" WHERE \"MovieId\"=@movieId", args, transaction).ToList();
            var versions = connection.Query<MovieVersion>("SELECT * FROM \"MovieVersions\" WHERE \"MovieId\"=@movieId", args, transaction).ToList();
            var active = versions.Where(v => !v.Retired).ToList();
            Require(source.Count == active.Count && active.All(v => source.Any(d => d.Id == v.CompositeProfileVersionId)), "Movie version ownership is inconsistent. Refresh and reconcile before conversion.");
            Require(active.Where(v => v.MovieFileId.HasValue).All(v => files.Any(f => f.Id == v.MovieFileId && !f.IsUnmanaged)), "A managed version's file is missing or marked unmanaged.");
            Require(movie.MovieFileId == 0 || files.Any(f => f.Id == movie.MovieFileId && !f.IsUnmanaged), "The movie's managed file is missing or marked unmanaged.");
            foreach (var version in active.Where(v => v.MovieFileId.HasValue))
            {
                var owner = source.Single(d => d.Id == version.CompositeProfileVersionId);
                files.Single(f => f.Id == version.MovieFileId).ManagementProfileId = owner.QualityProfileId;
            }

            if (source.Count == 0 && movie.MovieFileId > 0)
            {
                files.Single(f => f.Id == movie.MovieFileId).ManagementProfileId ??= movie.QualityProfileId;
            }

            var assignments = new Dictionary<int, int?>();
            var monitoring = new Dictionary<int, bool>();
            var existingFileProfileId = existingFileChildProfileId ?? movie.QualityProfileId;
            int? simpleFile = null;
            if (destination.Count == 0)
            {
                if (source.Count == 0)
                {
                    simpleFile = movie.MovieFileId > 0 ? movie.MovieFileId : null;
                }
                else
                {
                    var match = source.SingleOrDefault(d => d.QualityProfileId == (retainChildProfileId ?? targetProfileId));
                    Require(match != null, "No exact child-profile match exists. Choose which version to keep managed in the UI; existing files have not been changed.");
                    simpleFile = active.Single(v => v.CompositeProfileVersionId == match.Id).MovieFileId;
                }
            }
            else
            {
                new CompositeProfileValidator().ValidateAndThrow(destination);
                if (source.Count == 0 && movie.MovieFileId > 0)
                {
                    var identity = files.Single(f => f.Id == movie.MovieFileId).ManagementProfileId;
                    if (destination.Any(d => d.QualityProfileId == identity))
                    {
                        existingFileProfileId = identity.Value;
                    }

                    Require(destination.Any(d => d.QualityProfileId == existingFileProfileId), "The existing file has no exact destination-profile match. Choose its destination version in the UI.");
                }

                foreach (var definition in destination)
                {
                    var oldDefinition = source.SingleOrDefault(d => d.Id == definition.Id) ?? source.SingleOrDefault(d => d.QualityProfileId == definition.QualityProfileId);
                    var oldVersion = oldDefinition == null ? null : active.Single(v => v.CompositeProfileVersionId == oldDefinition.Id);
                    var fileId = oldVersion?.MovieFileId;
                    if (source.Count == 0 && movie.MovieFileId > 0 && definition.QualityProfileId == existingFileProfileId)
                    {
                        fileId = movie.MovieFileId;
                    }

                    if (!fileId.HasValue)
                    {
                        var retained = files.Where(f => f.IsUnmanaged && f.ManagementProfileId == definition.QualityProfileId).ToList();
                        if (retained.Count == 1)
                        {
                            fileId = retained[0].Id;
                        }
                    }

                    assignments.Add(definition.Id, fileId);
                    monitoring.Add(definition.Id, oldVersion?.Monitored ?? versions.SingleOrDefault(v => v.CompositeProfileVersionId == definition.Id)?.Monitored ?? true);
                }
            }

            var selected = destination.Count == 0 ? (simpleFile.HasValue ? new List<int> { simpleFile.Value } : new List<int>()) : assignments.Values.Where(v => v.HasValue).Select(v => v.Value).ToList();
            Require(selected.Distinct().Count() == selected.Count, "A file cannot belong to more than one managed version.");
            connection.Execute("DELETE FROM \"PendingReleases\" WHERE \"MovieId\"=@movieId", args, transaction);
            connection.Execute("UPDATE \"MovieVersions\" SET \"Retired\"=@retired, \"MovieFileId\"=NULL WHERE \"MovieId\"=@movieId", new { movieId = movie.Id, retired = true }, transaction);
            foreach (var file in files)
            {
                file.IsUnmanaged = !selected.Contains(file.Id);
                if (!file.IsUnmanaged && destination.Count > 0)
                {
                    file.ManagementProfileId = destination.Single(d => assignments[d.Id] == file.Id).QualityProfileId;
                }

                connection.Execute("UPDATE \"MovieFiles\" SET \"IsUnmanaged\"=@IsUnmanaged, \"ManagementProfileId\"=@ManagementProfileId WHERE \"Id\"=@Id AND \"MovieId\"=@MovieId", file, transaction);
            }

            foreach (var definition in destination)
            {
                var version = versions.SingleOrDefault(v => v.CompositeProfileVersionId == definition.Id);
                var values = new { movieId = movie.Id, definitionId = definition.Id, fileId = assignments[definition.Id], monitored = monitoring[definition.Id], retired = false };
                if (version == null)
                {
                    connection.Execute("INSERT INTO \"MovieVersions\" (\"MovieId\",\"CompositeProfileVersionId\",\"MovieFileId\",\"Monitored\",\"Retired\") VALUES (@movieId,@definitionId,@fileId,@monitored,@retired)", values, transaction);
                }
                else
                {
                    connection.Execute("UPDATE \"MovieVersions\" SET \"MovieFileId\"=@fileId,\"Monitored\"=@monitored,\"Retired\"=@retired WHERE \"MovieId\"=@movieId AND \"CompositeProfileVersionId\"=@definitionId", values, transaction);
                }
            }

            return destination.Count == 0 ? simpleFile ?? 0 : assignments[destination.Single(d => d.IsPrimary).Id] ?? 0;
        }

        public static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, message);
            }
        }
    }
}
