using System;
using System.IO;
using System.Linq;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Extras
{
    public interface IExtraFileVersionService
    {
        bool IsManaged(Movie movie);
        Movie Resolve(Movie movie, string path, bool artwork = false);
    }

    public class ExtraFileVersionService : IExtraFileVersionService
    {
        private readonly IMovieVersionService _versions;
        private readonly IMediaFileService _files;

        public ExtraFileVersionService(IMovieVersionService versions, IMediaFileService files)
        {
            _versions = versions;
            _files = files;
        }

        public bool IsManaged(Movie movie)
        {
            return _versions.Resolve(movie).Any(v => v.Id.HasValue);
        }

        public Movie Resolve(Movie movie, string path, bool artwork = false)
        {
            var versions = _versions.Resolve(movie);
            var files = _files.GetFilesByMovie(movie.Id);
            if (files?.Any(f => f.IsUnmanaged && Matches(Path.Combine(movie.Path, f.RelativePath), path, artwork)) == true)
            {
                return null;
            }

            if (!versions.Any(v => v.Id.HasValue))
            {
                return movie;
            }

            // Match actual registered video paths, never labels or the primary summary.
            var matches = versions.Where(v => v.Id.HasValue && v.MovieFileId.HasValue)
                .Select(v => new { Version = v, File = files.SingleOrDefault(f => f.Id == v.MovieFileId) })
                .Where(v => v.File != null && v.File.MovieId == movie.Id && Matches(Path.Combine(movie.Path, v.File.RelativePath), path, artwork))
                .ToList();
            if (matches.Count != 1)
            {
                return null;
            }

            var match = matches[0];
            return movie.WithVersion(match.Version, match.File);
        }

        internal static bool Matches(string videoPath, string extraPath, bool artwork = false)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(videoPath)), Path.GetDirectoryName(Path.GetFullPath(extraPath)), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var video = Path.GetFileNameWithoutExtension(videoPath);
            var extra = Path.GetFileNameWithoutExtension(extraPath);
            if (string.IsNullOrEmpty(video))
            {
                return false;
            }

            if (extra.Equals(video, StringComparison.OrdinalIgnoreCase) || extra.StartsWith(video + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return artwork && new[] { "-poster", "-fanart", "-thumb", "-banner", "-landscape", "-clearart", "-discart", "-logo" }
                .Any(s => extra.Equals(video + s, StringComparison.OrdinalIgnoreCase));
        }
    }
}
