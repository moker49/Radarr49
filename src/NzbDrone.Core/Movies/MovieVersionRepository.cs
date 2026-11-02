using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies
{
    public interface IMovieVersionRepository : IBasicRepository<MovieVersion>
    {
        List<MovieVersion> ForMovie(int movieId);
    }

    public class MovieVersionRepository : BasicRepository<MovieVersion>, IMovieVersionRepository
    {
        public MovieVersionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieVersion> ForMovie(int movieId)
        {
            return Query(v => v.MovieId == movieId);
        }
    }
}
