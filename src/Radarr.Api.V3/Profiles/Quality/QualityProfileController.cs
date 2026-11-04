using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;
using Radarr.Http;
using Radarr.Http.REST;
using Radarr.Http.REST.Attributes;

namespace Radarr.Api.V3.Profiles.Quality
{
    [V3ApiController]
    public class QualityProfileController : RestController<QualityProfileResource>
    {
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ICompositeProfilePolicy _compositeProfilePolicy;
        private readonly ICompositeProfileVersionRepository _compositeVersions;
        private readonly ICompositeProfileEditorRepository _editor;

        public QualityProfileController(IQualityProfileService qualityProfileService, ICustomFormatService formatService, ICompositeProfilePolicy compositeProfilePolicy, ICompositeProfileVersionRepository compositeVersions, ICompositeProfileEditorRepository editor)
        {
            _qualityProfileService = qualityProfileService;
            _compositeProfilePolicy = compositeProfilePolicy;
            _compositeVersions = compositeVersions;
            _editor = editor;

            SharedValidator.RuleFor(c => c.Name).NotEmpty();

            // TODO: Need to validate the cutoff is allowed and the ID/quality ID exists
            // TODO: Need to validate the Items to ensure groups have names and at no item has no name, no items and no quality
            SharedValidator.RuleFor(c => c.MinUpgradeFormatScore).GreaterThanOrEqualTo(1);
            SharedValidator.RuleFor(c => c.Cutoff).ValidCutoff();
            SharedValidator.RuleFor(c => c.Items).ValidItems();
            SharedValidator.RuleFor(c => c.SizeDefinitions)
                .SetValidator(new ProfileSizeDefinitionsValidator())
                .When(c => c.SizeDefinitions != null);

            SharedValidator.RuleFor(c => c.FormatItems).Must(items =>
            {
                var all = formatService.All().Select(f => f.Id).ToList();
                var ids = items.Select(i => i.Format);

                return all.Except(ids).Empty();
            }).WithMessage("All Custom Formats and no extra ones need to be present inside your Profile! Try refreshing your browser.");

            SharedValidator.RuleFor(c => c).Custom((profile, context) =>
            {
                if (profile.FormatItems.Where(x => x.Score > 0).Sum(x => x.Score) < profile.MinFormatScore &&
                    profile.FormatItems.Max(x => x.Score) < profile.MinFormatScore)
                {
                    context.AddFailure("Minimum Custom Format Score can never be satisfied");
                }
            });
        }

        [RestPostById]
        [Consumes("application/json")]
        public ActionResult<QualityProfileResource> Create([FromBody] QualityProfileResource resource)
        {
            var model = resource.ToModel();
            model = _qualityProfileService.Add(model);
            return Created(model.Id);
        }

        [RestDeleteById]
        public void DeleteProfile(int id)
        {
            _qualityProfileService.Delete(id);
        }

        [RestPutById]
        [Consumes("application/json")]
        public ActionResult<QualityProfileResource> Update([FromBody] QualityProfileResource resource)
        {
            _compositeProfilePolicy.EnsureLegacyProfileEditAllowed(resource.Id);
            var model = resource.ToModel();

            _qualityProfileService.Update(model);

            return Accepted(model.Id);
        }

        protected override QualityProfileResource GetResourceById(int id)
        {
            var resource = _qualityProfileService.Get(id).ToResource();
            resource.CompositeVersions = _compositeVersions.ForProfile(id).OrderBy(v => v.Priority).ToList();
            resource.IsComposite = resource.CompositeVersions.Any();
            return resource;
        }

        [HttpGet]
        public List<QualityProfileResource> GetAll()
        {
            var versions = _compositeVersions.All().ToLookup(v => v.CompositeProfileId);
            var resources = _qualityProfileService.All().ToResource();
            resources.ForEach(p =>
            {
                p.CompositeVersions = versions[p.Id].OrderBy(v => v.Priority).ToList();
                p.IsComposite = p.CompositeVersions.Any();
            });
            return resources;
        }

        [HttpPost("editor")]
        public QualityProfileResource CreateEditor([FromBody] QualityProfileResource resource)
        {
            resource.Id = 0;
            return SaveEditor(resource);
        }

        [HttpPut("editor/{id:int}")]
        public QualityProfileResource UpdateEditor(int id, [FromBody] QualityProfileResource resource)
        {
            if (resource.Id != id)
            {
                throw new BadRequestException("The profile ID does not match the requested profile.");
            }

            return SaveEditor(resource);
        }

        private QualityProfileResource SaveEditor(QualityProfileResource resource)
        {
            var id = _editor.Save(resource.ToModel(), resource.IsComposite, resource.CompositeVersions, resource.CompositeRootFolder);
            return GetResourceById(id);
        }
    }
}
