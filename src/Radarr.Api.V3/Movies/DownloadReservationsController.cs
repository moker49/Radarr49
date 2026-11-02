using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Download;
using Radarr.Http;

namespace Radarr.Api.V3.Movies
{
    [V3ApiController("movie/{movieId:int}/downloadreservations")]
    public class DownloadReservationsController : Controller
    {
        private readonly IDownloadReservationService _reservations;

        public DownloadReservationsController(IDownloadReservationService reservations)
        {
            _reservations = reservations;
        }

        [HttpGet]
        public List<DownloadReservationResource> Get(int movieId)
        {
            return _reservations.ForMovie(movieId).Select(r => new DownloadReservationResource
            {
                Id = r.Id,
                MovieId = r.MovieId,
                MovieVersionId = r.MovieVersionId,
                DownloadClientId = r.DownloadClientId,
                DownloadId = r.DownloadId,
                SourceTitle = r.SourceTitle,
                State = r.State,
                ImportedMovieFileId = r.ImportedMovieFileId,
                Created = r.Created,
                Updated = r.Updated,
                Note = r.Note
            }).ToList();
        }

        [HttpPost("{reservationId:int}/confirm-not-submitted")]
        public IActionResult ConfirmNotSubmitted(int movieId, int reservationId, [FromBody] ReservationResolutionResource resource)
        {
            _reservations.ConfirmNotSubmitted(movieId, reservationId, resource?.Note);
            return NoContent();
        }

        [HttpPost("{reservationId:int}/bind-download")]
        public IActionResult BindDownload(int movieId, int reservationId, [FromBody] ReservationResolutionResource resource)
        {
            _reservations.BindDownload(movieId, reservationId, resource?.DownloadId, resource?.Note);
            return NoContent();
        }
    }

    public class ReservationResolutionResource
    {
        public string DownloadId { get; set; }
        public string Note { get; set; }
    }

    public class DownloadReservationResource
    {
        public int Id { get; set; }
        public int MovieId { get; set; }
        public int MovieVersionId { get; set; }
        public int DownloadClientId { get; set; }
        public string DownloadId { get; set; }
        public string SourceTitle { get; set; }
        public DownloadReservationState State { get; set; }
        public int? ImportedMovieFileId { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public string Note { get; set; }
    }
}
