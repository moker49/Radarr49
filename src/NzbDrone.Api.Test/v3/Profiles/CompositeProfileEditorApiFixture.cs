using System.Collections.Generic;
using System.Reflection;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Profiles.Quality;
using Radarr.Http.REST;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class CompositeProfileEditorApiFixture : TestBase<QualityProfileController>
    {
        [Test]
        public void editor_update_must_reject_mismatched_route_and_resource_ids()
        {
            Assert.Throws<BadRequestException>(() => Subject.UpdateEditor(7, new QualityProfileResource { Id = 8 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void composite_does_not_require_parent_quality_settings(bool omitted)
        {
            var resource = new QualityProfileResource
            {
                Name = "SDR + HDR",
                IsComposite = true,
                Items = omitted ? null : new List<QualityProfileQualityItemResource>(),
                FormatItems = omitted ? null : new List<ProfileFormatItemResource>()
            };

            Assert.That(Validator.Validate(resource).IsValid, Is.True);
            Assert.That(resource.ToModel().Items, Is.Empty);
            Assert.That(resource.ToModel().FormatItems, Is.Empty);
        }

        [Test]
        public void ordinary_profile_still_requires_allowed_quality_and_cutoff()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            var result = Validator.Validate(new QualityProfileResource
            {
                Name = "SDR",
                MinUpgradeFormatScore = 1,
                Items = new List<QualityProfileQualityItemResource>(),
                FormatItems = new List<ProfileFormatItemResource>()
            });

            Assert.That(result.Errors, Has.Some.Property("PropertyName").EqualTo("Cutoff"));
            Assert.That(result.Errors, Has.Some.Property("PropertyName").EqualTo("Items"));
        }

        [Test]
        public void composite_still_requires_name()
        {
            Assert.That(Validator.Validate(new QualityProfileResource { IsComposite = true }).Errors,
                Has.Some.Property("PropertyName").EqualTo("Name"));
        }

        [Test]
        public void legacy_create_cannot_skip_validation_by_claiming_to_be_composite()
        {
            Assert.Throws<BadRequestException>(() => Subject.Create(new QualityProfileResource { IsComposite = true }));
            Mocker.GetMock<IQualityProfileService>().Verify(s => s.Add(It.IsAny<QualityProfile>()), Times.Never());
        }

        [Test]
        public void legacy_update_cannot_skip_validation_by_claiming_to_be_composite()
        {
            Assert.Throws<BadRequestException>(() => Subject.Update(new QualityProfileResource { Id = 7, IsComposite = true }));
            Mocker.GetMock<IQualityProfileService>().Verify(s => s.Update(It.IsAny<QualityProfile>()), Times.Never());
        }

        private IValidator<QualityProfileResource> Validator =>
            (IValidator<QualityProfileResource>)typeof(RestController<QualityProfileResource>)
                .GetProperty("SharedValidator", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Subject);
    }
}
