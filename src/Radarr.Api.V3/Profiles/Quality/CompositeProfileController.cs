using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Exceptions;
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
            return new CompositeProfileResource { ProfileId = profileId, Versions = _composites.Get(profileId), MaximumConcurrentDownloads = _composites.GetMaximumConcurrentDownloads(profileId), TestRootFolderPath = _composites.GetTestRoot(profileId) };
        }

        [HttpPut]
        public CompositeProfileResource Save(int profileId, [FromBody] CompositeProfileResource resource)
        {
            var limit = resource?.MaximumConcurrentDownloads ?? 1;
            return new CompositeProfileResource { ProfileId = profileId, Versions = _composites.Save(profileId, resource?.Versions, limit), MaximumConcurrentDownloads = limit };
        }

        [HttpPost("activate")]
        public CompositeProfileResource Activate(int profileId, [FromBody] CompositeActivationResource resource)
        {
            if (string.IsNullOrWhiteSpace(resource?.RootFolderPath))
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, "A configured test root is required.");
            }

            _composites.SetTestRoot(profileId, resource.RootFolderPath);
            return Get(profileId);
        }

        [HttpPost("deactivate")]
        public CompositeProfileResource Deactivate(int profileId)
        {
            _composites.SetTestRoot(profileId, null);
            return Get(profileId);
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
        public bool IsDraft => string.IsNullOrWhiteSpace(TestRootFolderPath);
        public string TestRootFolderPath { get; set; }
        public List<CompositeProfileVersion> Versions { get; set; }
        public int MaximumConcurrentDownloads { get; set; } = 1;
    }

    public class CompositeActivationResource
    {
        public string RootFolderPath { get; set; }
    }
}
