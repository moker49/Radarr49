using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    public interface IMovieProfileConversionRepository
    {
        bool RequiresConversion(List<Movie> movies, int targetProfileId);
        List<Movie> UpdateProfiles(List<Movie> movies, int? retainChildProfileId = null, int? existingFileChildProfileId = null);
    }

    public sealed class MovieManagementLocks : IDisposable
    {
        private readonly List<object> _locks = new List<object>();

        public MovieManagementLocks(IEnumerable<int> movieIds)
        {
            foreach (var id in movieIds.Distinct().OrderBy(id => id))
            {
                var sync = MovieVersionImportSynchronization.ForMovie(id);
                Monitor.Enter(sync);
                _locks.Add(sync);
            }
        }

        public void Dispose()
        {
            for (var i = _locks.Count - 1; i >= 0; i--)
            {
                Monitor.Exit(_locks[i]);
            }
        }

        public static void CheckTracked(ITrackedDownloadService tracked, IEnumerable<int> ids)
        {
            var movieIds = ids.ToHashSet();
            MovieProfileConversion.Require(tracked.GetTrackedDownloads()?.Any(t => t.IsTrackable && t.RemoteMovie?.Movie != null && movieIds.Contains(t.RemoteMovie.Movie.Id) &&
                t.State is not (TrackedDownloadState.Imported or TrackedDownloadState.Failed or TrackedDownloadState.FailedPending or TrackedDownloadState.Ignored)) != true,
                "Finish or remove active client downloads before changing managed versions.");
        }
    }

    public class MovieProfileConversionRepository : BasicRepository<Movie>, IMovieProfileConversionRepository
    {
        private readonly IMovieRepository _movies;
        private readonly ITrackedDownloadService _tracked;
        private readonly IEventAggregator _events;

        public MovieProfileConversionRepository(IMainDatabase database, IEventAggregator events, IMovieRepository movies, ITrackedDownloadService tracked)
            : base(database, events)
        {
            _movies = movies;
            _tracked = tracked;
            _events = events;
        }

        public bool RequiresConversion(List<Movie> movies, int targetProfileId)
        {
            using var connection = _database.OpenConnection();
            var profiles = movies.Select(m => m.QualityProfileId).Append(targetProfileId).Distinct().ToArray();
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\" IN @profiles", new { profiles }) != 0;
        }

        public List<Movie> UpdateProfiles(List<Movie> movies, int? retainChildProfileId = null, int? existingFileChildProfileId = null)
        {
            MovieProfileConversion.Require(movies.Select(m => m.Id).Distinct().Count() == movies.Count, "Specify each movie once.");
            using var locks = new MovieManagementLocks(movies.Select(m => m.Id));
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                var originals = movies.ToDictionary(m => m.Id, m => _movies.Get(m.Id));
                var changingIds = movies.Where(m => m.QualityProfileId != originals[m.Id].QualityProfileId).Select(m => m.Id).ToHashSet();
                MovieManagementLocks.CheckTracked(_tracked, changingIds);
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                connection.Execute("UPDATE \"QualityProfiles\" SET \"Name\"=\"Name\"", transaction: transaction);
                foreach (var movie in movies.OrderBy(m => m.Id))
                {
                    MovieProfileConversion.Require(connection.Execute("UPDATE \"Movies\" SET \"MovieFileId\"=\"MovieFileId\" WHERE \"Id\"=@Id", movie, transaction) == 1, "The movie no longer exists.");
                    var stored = connection.QuerySingle<Movie>("SELECT * FROM \"Movies\" WHERE \"Id\"=@Id", movie, transaction);
                    MovieProfileConversion.Require(string.Equals(stored.Path, movie.Path, StringComparison.OrdinalIgnoreCase), "Save profile changes separately from folder moves. No files have been changed.");
                    if (movie.QualityProfileId == stored.QualityProfileId)
                    {
                        movie.MovieFileId = stored.MovieFileId;
                        Update(connection, transaction, movie);
                        continue;
                    }

                    var source = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\"=@QualityProfileId", stored, transaction).ToList();
                    var destination = connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\"=@QualityProfileId", movie, transaction).ToList();
                    var target = connection.QuerySingleOrDefault<QualityProfile>("SELECT * FROM \"QualityProfiles\" WHERE \"Id\"=@QualityProfileId", movie, transaction);
                    MovieProfileConversion.Require(target != null, "The destination profile no longer exists.");
                    if (destination.Count > 0)
                    {
                        CompositeProfileActivation.ValidatePath(stored, target);
                    }

                    movie.MovieFileId = MovieProfileConversion.Apply(connection, transaction, stored, movie.QualityProfileId, source, destination, retainChildProfileId, existingFileChildProfileId);
                    Update(connection, transaction, movie);
                }

                transaction.Commit();
                if (changingIds.Count > 0)
                {
                    _events.PublishEvent(new PendingReleasesUpdatedEvent());
                }

                var result = movies.Select(m => _movies.Get(m.Id)).ToList();
                foreach (var movie in result)
                {
                    ModelUpdated(movie);
                    _events.PublishEvent(new MovieEditedEvent(movie, originals[movie.Id]));
                    foreach (var file in changingIds.Contains(movie.Id) ? connection.Query<MovieFile>("SELECT * FROM \"MovieFiles\" WHERE \"MovieId\"=@Id", movie) : Enumerable.Empty<MovieFile>())
                    {
                        _events.PublishEvent(new MovieFileUpdatedEvent(file));
                    }
                }

                return result;
            }
        }
    }
}
