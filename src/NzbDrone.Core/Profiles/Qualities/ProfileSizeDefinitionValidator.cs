using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Profiles.Qualities
{
    public class ProfileSizeDefinitionValidator : AbstractValidator<ProfileSizeDefinition>
    {
        public ProfileSizeDefinitionValidator()
        {
            RuleFor(x => x.QualityId).Must(id => Quality.All.Any(q => q.Id == id));
            RuleFor(x => x.MinSize).Must(IsValidSize);
            RuleFor(x => x.PreferredSize).Must(IsValidSize);
            RuleFor(x => x.MaxSize).Must(IsValidSize);
            RuleFor(x => x).Must(x =>
            {
                var minimum = x.MinSize ?? 0;
                var maximum = x.MaxSize.HasValue && x.MaxSize.Value != 0 ? x.MaxSize.Value : double.PositiveInfinity;
                return minimum <= maximum &&
                    (!x.PreferredSize.HasValue || (x.PreferredSize.Value >= minimum && x.PreferredSize.Value <= maximum));
            }).WithMessage("Size settings must satisfy minimum <= preferred <= maximum; an empty or zero maximum is unlimited.");
        }

        private static bool IsValidSize(double? value)
        {
            return !value.HasValue ||
                (double.IsFinite(value.Value) && value.Value >= QualityDefinitionLimits.Min && value.Value <= QualityDefinitionLimits.Max);
        }
    }

    public class ProfileSizeDefinitionsValidator : AbstractValidator<List<ProfileSizeDefinition>>
    {
        public ProfileSizeDefinitionsValidator()
        {
            RuleFor(x => x).Must(items => items.All(x => x != null) &&
                items.Select(x => x.QualityId).Distinct().Count() == items.Count)
                .WithMessage("Each quality may have only one size definition and entries cannot be null.");
            RuleForEach(x => x).NotNull().SetValidator(new ProfileSizeDefinitionValidator());
        }
    }
}
