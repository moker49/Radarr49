using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;

namespace NzbDrone.Core.Profiles.Qualities
{
    public class CompositeProfileValidator : AbstractValidator<List<CompositeProfileVersion>>
    {
        public CompositeProfileValidator()
        {
            RuleFor(x => x).Cascade(CascadeMode.Stop)
                .Must(x => x.Count == 2 && x.All(v => v != null))
                .WithMessage("A composite requires exactly two versions.")
                .Must(x => x.All(v => v.Id >= 0 && v.QualityProfileId > 0))
                .WithMessage("Version and child profile IDs must be valid.")
                .Must(x => x.Count(v => v.IsPrimary) == 1)
                .WithMessage("Select exactly one primary version.")
                .Must(x => x.All(v => ValidLabel(v.Label)) && x.Select(v => v.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2)
                .WithMessage("Version labels must be unique filename-safe names of 1 to 64 characters without surrounding whitespace.")
                .Must(x => x.Select(v => v.Priority).OrderBy(p => p).SequenceEqual(new[] { 0, 1 }))
                .WithMessage("Version priorities must be 0 and 1.")
                .Must(x => x.Select(v => v.QualityProfileId).Distinct().Count() == 2)
                .WithMessage("Select two different ordinary profiles.");
        }

        internal static bool ValidLabel(string label)
        {
            return !string.IsNullOrWhiteSpace(label) && label.Length <= 64 && label == label.Trim() &&
                !label.EndsWith(".", StringComparison.Ordinal) && !label.Any(char.IsControl) &&
                label.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) < 0;
        }
    }
}
