using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class MovieFileAddedEvent : IEvent
    {
        public MovieFile MovieFile { get; private set; }
        public int? ImportOperationId { get; set; }

        public MovieFileAddedEvent(MovieFile movieFile)
        {
            MovieFile = movieFile;
        }
    }
}
