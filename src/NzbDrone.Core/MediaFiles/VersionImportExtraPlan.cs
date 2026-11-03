using System;
using System.IO;
using System.Linq;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.MediaFiles
{
    internal static class VersionImportExtraPlan
    {
        public static void Create(VersionFileImport operation)
        {
            var stem = Path.GetFileNameWithoutExtension(operation.SourcePath);
            var destination = Path.Combine(Path.GetDirectoryName(operation.DestinationRelativePath) ?? string.Empty, Path.GetFileNameWithoutExtension(operation.DestinationRelativePath));
            var area = Path.GetDirectoryName(operation.StagingRelativePath);
            var extras = operation.Snapshot.IncomingExtras;
            for (var i = 0; i < extras.Count; i++)
            {
                var extra = extras[i];
                if (!ExtraFileVersionService.Matches(operation.SourcePath, extra.SourcePath) ||
                    extra.ContentHash?.Length != 64 || !extra.ContentHash.All(Uri.IsHexDigit) || extra.Size < 0)
                {
                    throw new InvalidOperationException("An incoming sidecar does not match its captured video source and content identity.");
                }

                var extension = Path.GetExtension(extra.SourcePath);
                if (MediaFileExtensions.Extensions.Contains(extension.ToLowerInvariant()))
                {
                    throw new InvalidOperationException("A video cannot be installed as an import sidecar.");
                }

                extra.DestinationRelativePath = string.Concat(destination, Path.GetFileName(extra.SourcePath).AsSpan(stem.Length));
                extra.StagingRelativePath = Path.Combine(area, $"extra-{i:D6}.incoming{extension}");
                extra.BackupRelativePath = Path.Combine(area, $"extra-{i:D6}.previous{extension}");
                extra.State = VersionImportExtraState.Prepared;
                extra.RegisteredExtraId = null;
                extra.Subtitle = SubtitleFileExtensions.Extensions.Contains(extension.ToLowerInvariant())
                    ? new SubtitleFile { Language = LanguageParser.ParseSubtitleLanguage(extra.SourcePath), LanguageTags = LanguageParser.ParseLanguageTags(extra.SourcePath), Extension = extension }
                    : null;
            }

            if (extras.Select(e => e.DestinationRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != extras.Count)
            {
                throw new InvalidOperationException("Incoming sidecars resolve to the same destination.");
            }
        }
    }
}
