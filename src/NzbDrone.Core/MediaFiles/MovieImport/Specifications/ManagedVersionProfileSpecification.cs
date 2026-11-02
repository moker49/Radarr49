using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    public class ManagedVersionProfileSpecification : IImportDecisionEngineSpecification
    {
        private readonly IQualityDefinitionService _qualities;

        public ManagedVersionProfileSpecification(IQualityDefinitionService qualities)
        {
            _qualities = qualities;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            if (!localMovie.MovieVersionId.HasValue)
            {
                return ImportSpecDecision.Accept();
            }

            var profile = localMovie.Movie.QualityProfile;
            var quality = localMovie.Quality?.Quality;
            var allowed = quality != null && profile.Items?.Any(i => i.Allowed &&
                (i.Quality?.Id == quality.Id || i.Items?.Any(c => c.Quality?.Id == quality.Id) == true)) == true;
            if (!allowed || localMovie.CustomFormatScore < profile.MinFormatScore)
            {
                return ImportSpecDecision.Reject(ImportRejectionReason.VersionProfileNotSatisfied, "File quality or Custom Format score does not satisfy the selected version's profile.");
            }

            var language = profile.Language;
            if (language != null && language != Language.Any)
            {
                language = language == Language.Original ? localMovie.Movie.MovieMetadata.Value.OriginalLanguage : language;
                if (localMovie.Languages?.Contains(language) != true)
                {
                    return ImportSpecDecision.Reject(ImportRejectionReason.VersionProfileNotSatisfied, "File language does not satisfy the selected version's profile.");
                }
            }

            var size = profile.GetSizeDefinition(quality, _qualities.Get(quality));
            var runtime = localMovie.Movie.MovieMetadata.Value.Runtime;
            runtime = runtime > 0 ? runtime : 110;
            if ((size.MinSize.HasValue && localMovie.Size < size.MinSize.Value.Megabytes() * runtime) ||
                (size.MaxSize.HasValue && size.MaxSize.Value > 0 && localMovie.Size > size.MaxSize.Value.Megabytes() * runtime))
            {
                return ImportSpecDecision.Reject(ImportRejectionReason.VersionProfileNotSatisfied, "File size does not satisfy the selected version's profile.");
            }

            return ImportSpecDecision.Accept();
        }
    }
}
