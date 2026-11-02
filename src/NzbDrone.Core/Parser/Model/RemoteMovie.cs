using System.Collections.Generic;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Parser.Model
{
    public class RemoteMovie
    {
        public ReleaseInfo Release { get; set; }
        public ParsedMovieInfo ParsedMovieInfo { get; set; }
        public List<CustomFormat> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public MovieMatchType MovieMatchType { get; set; }
        public Movie Movie { get; set; }
        public int? MovieVersionId { get; set; }
        public int MovieVersionPriority { get; set; }
        public string VersionError { get; set; }

        public bool MovieRequested { get; set; }
        public bool DownloadAllowed { get; set; }
        public TorrentSeedConfiguration SeedConfiguration { get; set; }
        public List<Language> Languages { get; set; }
        public ReleaseSourceType ReleaseSource { get; set; }

        public RemoteMovie()
        {
            CustomFormats = new List<CustomFormat>();
            Languages = new List<Language>();
        }

        public RemoteMovie WithVersion(Movie movie, int versionId, int priority)
        {
            var context = (RemoteMovie)MemberwiseClone();
            context.Movie = movie;
            context.MovieVersionId = versionId;
            context.MovieVersionPriority = priority;
            context.CustomFormats = new List<CustomFormat>();
            context.Languages = new List<Language>(Languages ?? new List<Language>());
            context.ParsedMovieInfo = ParsedMovieInfo?.JsonClone();
            context.Release = Release?.CopyForVersion();
            return context;
        }

        public override string ToString()
        {
            return Release.Title;
        }
    }

    public enum ReleaseSourceType
    {
        Unknown = 0,
        Rss = 1,
        Search = 2,
        UserInvokedSearch = 3,
        InteractiveSearch = 4,
        ReleasePush = 5
    }
}
