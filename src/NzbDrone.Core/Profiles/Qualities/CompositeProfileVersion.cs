using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Profiles.Qualities
{
    public class CompositeProfileVersion : ModelBase
    {
        public int CompositeProfileId { get; set; }
        public int QualityProfileId { get; set; }
        public string Label { get; set; }
        public int Priority { get; set; }
        public bool IsPrimary { get; set; }
    }
}
