using System.Collections.Concurrent;

namespace NzbDrone.Core.Movies
{
    internal static class MovieVersionImportSynchronization
    {
        private static readonly ConcurrentDictionary<int, object> Locks = new ConcurrentDictionary<int, object>();

        internal static object ForMovie(int movieId)
        {
            return Locks.GetOrAdd(movieId, _ => new object());
        }
    }
}
