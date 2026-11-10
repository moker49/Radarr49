using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    public interface IManagedMovieDeletionService
    {
        void Delete(List<int> movieIds, bool deleteFiles, bool addImportExclusion);
    }

    public class ManagedMovieDeletionService : IManagedMovieDeletionService
    {
        private readonly IMovieRepository _movies;
        private readonly IVersionFileImportRepository _imports;
        private readonly IEventAggregator _events;

        public ManagedMovieDeletionService(IMovieRepository movies, IVersionFileImportRepository imports, IEventAggregator events)
        {
            _movies = movies;
            _imports = imports;
            _events = events;
        }

        public void Delete(List<int> movieIds, bool deleteFiles, bool addImportExclusion)
        {
            var ids = movieIds.Distinct().OrderBy(id => id).ToList();
            var locks = new List<object>();
            var leases = new List<VersionImportOperationLease>();
            lock (CompositeProfileSynchronization.SyncRoot)
            {
                try
                {
                    foreach (var id in ids)
                    {
                        var sync = MovieVersionImportSynchronization.ForMovie(id);
                        if (!Monitor.TryEnter(sync))
                        {
                            throw new NzbDroneClientException(HttpStatusCode.Conflict, "A file operation is running for movie {0}. Wait for its current phase, then retry deletion.", id);
                        }

                        locks.Add(sync);
                        foreach (var operation in _imports.ForMovie(id).Where(i => i.IsActive).OrderBy(i => i.Id))
                        {
                            leases.Add(new VersionImportOperationLease(operation.Id));
                        }
                    }

                    var movies = _movies.Get(ids).ToList();
                    if (movies.Count != ids.Count)
                    {
                        throw new NzbDroneClientException(HttpStatusCode.NotFound, "One or more movies no longer exist. No movies were deleted.");
                    }

                    _movies.DeleteWithVersions(ids);
                    _events.PublishEvent(new MoviesDeletedEvent(movies, deleteFiles, addImportExclusion));
                }
                finally
                {
                    for (var i = leases.Count - 1; i >= 0; i--)
                    {
                        leases[i].Dispose();
                    }

                    for (var i = locks.Count - 1; i >= 0; i--)
                    {
                        Monitor.Exit(locks[i]);
                    }
                }
            }
        }
    }
}
