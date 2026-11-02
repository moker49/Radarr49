using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Movies
{
    // Runtime view shared by ordinary and composite profiles. A null Id identifies
    // an ordinary movie's implicit version; resolving it never creates a database row.
    public class ResolvedMovieVersion
    {
        public int? Id { get; set; }
        public int MovieId { get; set; }
        public int? CompositeProfileVersionId { get; set; }
        public string Label { get; set; }
        public int Priority { get; set; }
        public bool IsPrimary { get; set; }
        public bool Monitored { get; set; }
        public int? MovieFileId { get; set; }
        public QualityProfile QualityProfile { get; set; }
    }
}
