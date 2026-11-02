using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MovieImport.Manual;
using NzbDrone.Core.Qualities;
using Radarr.Api.V3.CustomFormats;
using Radarr.Api.V3.Movies;
using Radarr.Http;
using Radarr.Http.REST;

namespace Radarr.Api.V3.ManualImport
{
    [V3ApiController]
    public class ManualImportController : Controller
    {
        private readonly IManualImportService _manualImportService;

        public ManualImportController(IManualImportService manualImportService)
        {
            _manualImportService = manualImportService;
        }

        [HttpGet]
        [Produces("application/json")]
        public List<ManualImportResource> GetMediaFiles(string folder, string downloadId, int? movieId, bool filterExistingFiles = true, int? movieVersionId = null, int? downloadClientId = null, int? downloadReservationId = null)
        {
            if (movieId.HasValue && downloadId.IsNullOrWhiteSpace() && (folder.IsNullOrWhiteSpace() || !movieVersionId.HasValue))
            {
                return _manualImportService.GetMediaFiles(movieId.Value)
                    .Where(i => !movieVersionId.HasValue || i.MovieVersionId == movieVersionId).ToResource().Select(AddQualityWeight).ToList();
            }

            var selection = new ManualImportVersionSelection { MovieVersionId = movieVersionId, DownloadClientId = downloadClientId, DownloadReservationId = downloadReservationId };
            return _manualImportService.GetMediaFiles(folder, downloadId, movieId, filterExistingFiles, selection).ToResource().Select(AddQualityWeight).ToList();
        }

        [HttpPost]
        [Consumes("application/json")]
        public object ReprocessItems([FromBody] List<ManualImportReprocessResource> items)
        {
            if (items is { Count: 0 })
            {
                throw new BadRequestException("items must be provided");
            }

            foreach (var item in items)
            {
                var selection = new ManualImportVersionSelection { MovieVersionId = item.MovieVersionId, DownloadClientId = item.DownloadClientId, DownloadReservationId = item.DownloadReservationId };
                var processedItem = _manualImportService.ReprocessItem(item.Path, item.DownloadId, item.MovieId, item.ReleaseGroup, item.Quality, item.Languages, item.IndexerFlags, selection);

                item.Movie = processedItem.Movie.ToResource(0);
                item.IndexerFlags = processedItem.IndexerFlags;
                item.Rejections = processedItem.Rejections.Select(r => r.ToResource());
                item.CustomFormats = processedItem.CustomFormats.ToResource(false);
                item.CustomFormatScore = processedItem.CustomFormatScore;
                item.MovieVersionId = processedItem.MovieVersionId;
                item.DownloadClientId = processedItem.DownloadClientId;
                item.DownloadReservationId = processedItem.DownloadReservationId;

                // Only set the language/quality if they're unknown and languages were returned.
                if (item.Languages?.Count <= 1 && (item.Languages?.SingleOrDefault() ?? Language.Unknown) == Language.Unknown && processedItem.Languages.Any())
                {
                    item.Languages = processedItem.Languages;
                }

                if (item.Quality?.Quality == Quality.Unknown)
                {
                    item.Quality = processedItem.Quality;
                }

                if (item.ReleaseGroup.IsNullOrWhiteSpace())
                {
                    item.ReleaseGroup = processedItem.ReleaseGroup;
                }
            }

            return items;
        }

        private ManualImportResource AddQualityWeight(ManualImportResource item)
        {
            if (item.Quality != null)
            {
                item.QualityWeight = Quality.DefaultQualityDefinitions.Single(q => q.Quality == item.Quality.Quality).Weight;
                item.QualityWeight += item.Quality.Revision.Real * 10;
                item.QualityWeight += item.Quality.Revision.Version;
            }

            return item;
        }
    }
}
