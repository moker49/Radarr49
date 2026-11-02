using System.Net;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.Download
{
    public class DownloadReservationConflictException : NzbDroneClientException
    {
        public DownloadReservationConflictException()
            : base(HttpStatusCode.Conflict, "The managed version, release, or download slot is already reserved. Review active downloads and unresolved reservations.")
        {
        }
    }
}
