using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Profiles.Qualities;
using Radarr.Http;

namespace Radarr.Api.V3.Profiles.Quality
{
    [V3ApiController("qualityprofile/{profileId:int}/composite")]
    public class CompositeProfileController : Controller
    {
        private readonly ICompositeProfileService _composites;

        public CompositeProfileController(ICompositeProfileService composites)
        {
            _composites = composites;
        }

        [HttpGet]
        public CompositeProfileResource Get(int profileId)
        {
            return new CompositeProfileResource { ProfileId = profileId, Versions = _composites.Get(profileId) };
        }

        [HttpPut]
        public CompositeProfileResource Save(int profileId, [FromBody] CompositeProfileResource resource)
        {
            return new CompositeProfileResource { ProfileId = profileId, Versions = _composites.Save(profileId, resource?.Versions) };
        }

        [HttpDelete]
        public IActionResult Delete(int profileId)
        {
            _composites.Delete(profileId);
            return NoContent();
        }
    }

    public class CompositeProfileResource
    {
        public int ProfileId { get; set; }
        public bool IsDraft => true;
        public List<CompositeProfileVersion> Versions { get; set; }
    }
}
