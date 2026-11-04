using System.Linq;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Profiles.Qualities
{
    public static class QualityProfileSizeExtensions
    {
        public static QualityDefinition GetSizeDefinition(this QualityProfile profile, Quality quality, QualityDefinition globalDefinition)
        {
            var definition = profile?.UseGlobalSizeDefinitions == true ? null : profile?.SizeDefinitions?.SingleOrDefault(x => x.QualityId == quality.Id);
            if (definition == null)
            {
                return globalDefinition;
            }

            // Never modify the globally cached definition when resolving profile settings.
            return new QualityDefinition(quality)
            {
                Id = globalDefinition.Id,
                Title = globalDefinition.Title,
                GroupName = globalDefinition.GroupName,
                Weight = globalDefinition.Weight,
                MinSize = definition.MinSize,
                PreferredSize = definition.PreferredSize,
                MaxSize = definition.MaxSize
            };
        }
    }
}
