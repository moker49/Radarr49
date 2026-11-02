using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface ICompositeProfileVersionRepository : IBasicRepository<CompositeProfileVersion>
    {
        List<CompositeProfileVersion> ForProfile(int profileId);
    }

    public class CompositeProfileVersionRepository : BasicRepository<CompositeProfileVersion>, ICompositeProfileVersionRepository
    {
        public CompositeProfileVersionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<CompositeProfileVersion> ForProfile(int profileId)
        {
            return Query(v => v.CompositeProfileId == profileId);
        }
    }
}
