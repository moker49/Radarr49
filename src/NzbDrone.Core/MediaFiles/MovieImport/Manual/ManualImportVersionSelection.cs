namespace NzbDrone.Core.MediaFiles.MovieImport.Manual
{
    public class ManualImportVersionSelection
    {
        public int? MovieVersionId { get; set; }
        public int? DownloadClientId { get; set; }
        public int? DownloadReservationId { get; set; }
    }
}
