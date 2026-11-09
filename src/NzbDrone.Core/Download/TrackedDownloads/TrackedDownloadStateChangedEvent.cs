using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public class TrackedDownloadStateChangedEvent : IEvent
    {
        public TrackedDownload Download { get; }

        public TrackedDownloadStateChangedEvent(TrackedDownload download)
        {
            Download = download;
        }
    }
}
