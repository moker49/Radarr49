using NzbDrone.Core.Download;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Aggregation.Aggregators
{
    public class AggregateMovie : IAggregateLocalMovie
    {
        public int Order => 1;

        private readonly IMovieService _movieService;
        private readonly IMovieVersionService _versions;

        public AggregateMovie(IMovieService movieService, IMovieVersionService versions)
        {
            _movieService = movieService;
            _versions = versions;
        }

        public LocalMovie Aggregate(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var versionId = localMovie.MovieVersionId ?? localMovie.Movie.ManagedVersion?.Id;
            var movie = _movieService.GetMovie(localMovie.Movie.Id);
            localMovie.Movie = versionId.HasValue ? _versions.ForVersion(movie, versionId.Value) : movie;
            localMovie.MovieVersionId = versionId;

            return localMovie;
        }
    }
}
