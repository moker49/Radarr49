using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Extras.Files
{
    public interface IExtraFileDeletionGuard
    {
        bool CanRecycle(Movie movie, int removedMovieFileId, string relativePath);
    }

    public class ExtraFileDeletionGuard : IExtraFileDeletionGuard
    {
        private readonly IMediaFileRepository _videos;
        private readonly IExtraFileRepository<SubtitleFile> _subtitles;
        private readonly IExtraFileRepository<MetadataFile> _metadata;
        private readonly IExtraFileRepository<OtherExtraFile> _others;
        private readonly IMovieService _movies;

        public ExtraFileDeletionGuard(IMediaFileRepository videos,
                                      IExtraFileRepository<SubtitleFile> subtitles,
                                      IExtraFileRepository<MetadataFile> metadata,
                                      IExtraFileRepository<OtherExtraFile> others,
                                      IMovieService movies)
        {
            _videos = videos;
            _subtitles = subtitles;
            _metadata = metadata;
            _others = others;
            _movies = movies;
        }

        public bool CanRecycle(Movie movie, int removedMovieFileId, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                return false;
            }

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(movie.Path));
            var path = Path.GetFullPath(Path.Combine(folder, relativePath));
            if (!folder.IsParentPath(path) || _movies.AllMoviePaths().Any(m => m.Key != movie.Id &&
                (Path.GetFullPath(m.Value).PathEquals(folder) || Path.GetFullPath(m.Value).PathEquals(path) || Path.GetFullPath(m.Value).IsParentPath(path))))
            {
                return false;
            }

            if (_videos.GetFilesByMovie(movie.Id).Any(f => Path.GetFullPath(Path.Combine(folder, f.RelativePath)).PathEquals(path)))
            {
                return false;
            }

            var metadata = _metadata.GetFilesByMovie(movie.Id);
            var sharedNames = new[] { "movie", Path.GetFileName(folder), "poster", "fanart", "folder", "banner", "landscape", "clearart", "discart", "logo" };
            var stem = Path.GetFileNameWithoutExtension(path);
            if (metadata.Any(f => Path.GetFullPath(Path.Combine(folder, f.RelativePath)).PathEquals(path) &&
                (f.Type == MetadataType.MovieMetadata || f.Type == MetadataType.MovieImage) && sharedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Claims in every extras category matter: a provider may have reclassified
            // the path or a replacement import may have registered it since the delete event.
            return !_subtitles.GetFilesByMovie(movie.Id).Cast<ExtraFile>().Concat(metadata).Concat(_others.GetFilesByMovie(movie.Id))
                .Any(f => f.MovieFileId != removedMovieFileId && Path.GetFullPath(Path.Combine(folder, f.RelativePath)).PathEquals(path));
        }
    }
}
