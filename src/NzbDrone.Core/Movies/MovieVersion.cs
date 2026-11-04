using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Movies
{
    public class MovieVersion : ModelBase
    {
        public int MovieId { get; set; }
        public int CompositeProfileVersionId { get; set; }
        public bool Monitored { get; set; }
        public bool Retired { get; set; }
        public int? MovieFileId { get; set; }
    }
}
