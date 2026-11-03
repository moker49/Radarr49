using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Movies
{
    public interface IMovieVersionStatusService
    {
        List<MovieVersionStatus> Get(Movie movie);
    }

    public class MovieVersionStatus
    {
        public ResolvedMovieVersion Version { get; set; }
        public MovieFile File { get; set; }
        public bool HasFile => File != null;
        public bool QualityCutoffNotMet { get; set; }
        public bool CutoffNotMet { get; set; }
        public int CustomFormatScore { get; set; }
        public string Error { get; set; }
    }

    public class MovieVersionStatusService : IMovieVersionStatusService
    {
        private readonly IMovieVersionService _versions;
        private readonly IMediaFileService _files;
        private readonly IUpgradableSpecification _upgradable;
        private readonly ICustomFormatCalculationService _formats;

        public MovieVersionStatusService(IMovieVersionService versions, IMediaFileService files, IUpgradableSpecification upgradable, ICustomFormatCalculationService formats)
        {
            _versions = versions;
            _files = files;
            _upgradable = upgradable;
            _formats = formats;
        }

        public List<MovieVersionStatus> Get(Movie movie)
        {
            var versions = _versions.Resolve(movie);
            var files = _files.GetFilesByMovie(movie.Id).ToDictionary(f => f.Id);
            return versions.Select(version =>
            {
                var status = new MovieVersionStatus { Version = version };
                if (!version.MovieFileId.HasValue)
                {
                    return status;
                }

                if (!files.TryGetValue(version.MovieFileId.Value, out var file) || file.MovieId != movie.Id)
                {
                    status.Error = "The registered version file is missing or belongs to another movie.";
                    return status;
                }

                status.File = file;
                var context = movie.WithVersion(version, file);
                var formats = _formats.ParseCustomFormat(file, context);
                status.CustomFormatScore = version.QualityProfile.CalculateCustomFormatScore(formats);
                status.QualityCutoffNotMet = _upgradable.QualityCutoffNotMet(version.QualityProfile, file.Quality);
                status.CutoffNotMet = _upgradable.CutoffNotMet(version.QualityProfile, file.Quality, formats);
                return status;
            }).ToList();
        }
    }
}
