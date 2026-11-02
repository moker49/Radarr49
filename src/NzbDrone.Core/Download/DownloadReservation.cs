using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Download
{
    public enum DownloadReservationState
    {
        Prepared = 0,
        Dispatching = 1,
        Submitted = 2,
        Imported = 3,
        Failed = 4,
        Uncertain = 5
    }

    public class DownloadReservation : ModelBase
    {
        public int MovieId { get; set; }
        public int MovieVersionId { get; set; }
        public int DownloadClientId { get; set; }
        public string DownloadId { get; set; }
        public string SourceTitle { get; set; }
        public List<string> ReleaseKeys { get; set; } = new List<string>();
        public DownloadReservationState State { get; set; }
        public int? ImportedMovieFileId { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public string Note { get; set; }

        public bool IsActive => State is DownloadReservationState.Prepared or DownloadReservationState.Dispatching or DownloadReservationState.Submitted or DownloadReservationState.Uncertain;
    }

    public class ActiveVersionDownload
    {
        public int? MovieVersionId { get; set; }
        public int DownloadClientId { get; set; }
        public string DownloadId { get; set; }
        public List<string> ReleaseKeys { get; set; } = new List<string>();
    }
}
