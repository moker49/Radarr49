using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Profiles.Qualities
{
    public class ProfileSizeDefinition : IEmbeddedDocument
    {
        public int QualityId { get; set; }
        public double? MinSize { get; set; }
        public double? PreferredSize { get; set; }
        public double? MaxSize { get; set; }
    }
}
