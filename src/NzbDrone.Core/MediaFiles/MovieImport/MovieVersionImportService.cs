using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IMovieVersionImportService
    {
        void Prepare(LocalMovie movie, DownloadClientItem download);
        void ValidateForWrite(LocalMovie movie, DownloadClientItem download, bool newDownload);
    }

    public class MovieVersionImportService : IMovieVersionImportService
    {
        private readonly IMovieRepository _movies;
        private readonly IMovieVersionService _versions;
        private readonly IDownloadReservationRepository _reservations;
        private readonly ICustomFormatCalculationService _formats;
        private readonly ManagedVersionProfileSpecification _profileSpecification;
        private readonly IDiskProvider _disk;
        private readonly IMediaFileService _files;
        private readonly IConfigService _config;

        public MovieVersionImportService(IMovieRepository movies,
                                         IMovieVersionService versions,
                                         IDownloadReservationRepository reservations,
                                         ICustomFormatCalculationService formats,
                                         ManagedVersionProfileSpecification profileSpecification,
                                         IDiskProvider disk,
                                         IMediaFileService files,
                                         IConfigService config)
        {
            _movies = movies;
            _versions = versions;
            _reservations = reservations;
            _formats = formats;
            _profileSpecification = profileSpecification;
            _disk = disk;
            _files = files;
            _config = config;
        }

        public void Prepare(LocalMovie movie, DownloadClientItem download)
        {
            try
            {
                PrepareCore(movie, download);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ModelNotFoundException)
            {
                throw new MovieVersionImportException(ex.Message);
            }
        }

        private void PrepareCore(LocalMovie local, DownloadClientItem download)
        {
            Require(local.Movie != null, "Select a movie before importing a managed version.");
            var versionId = local.MovieVersionId ?? local.Movie?.ManagedVersion?.Id;
            var movie = _movies.Get(local.Movie.Id);
            var versions = _versions.Resolve(movie);
            if (!versions.Any(v => v.Id.HasValue))
            {
                Require(!versionId.HasValue && !local.DownloadReservationId.HasValue, "The selected version no longer belongs to this movie.");
                if (download?.DownloadClientInfo?.Id > 0)
                {
                    Require((_reservations.FindForDownload(download.DownloadClientInfo.Id, download.DownloadId)?.Count ?? 0) == 0,
                        "This client download belongs to a managed version and cannot be redirected to an ordinary movie.");
                }

                local.Movie = movie;
                return;
            }

            if (download != null)
            {
                Require(download.DownloadClientInfo?.Id > 0 && !string.IsNullOrWhiteSpace(download.DownloadId), "A managed client import requires its bound client and download identity.");
                var reservations = _reservations.FindForDownload(download.DownloadClientInfo.Id, download.DownloadId);
                Require(reservations.Count == 1 && reservations[0].MovieId == movie.Id, "The client download has no unambiguous reservation for this movie.");
                var reservation = reservations[0];
                Require(!local.DownloadReservationId.HasValue || local.DownloadReservationId.Value == reservation.Id, "The download attempt changed. Re-evaluate the import.");
                Require(!versionId.HasValue || versionId.Value == reservation.MovieVersionId, "The selected version conflicts with the download reservation.");
                Require(reservation.State is DownloadReservationState.Submitted or DownloadReservationState.Imported, "The download reservation is not ready for import.");
                versionId = reservation.MovieVersionId;
                local.DownloadReservationId = reservation.Id;
            }

            Require(versionId.HasValue, "Select a destination version explicitly. Existing files are never assigned by filename alone.");
            local.Movie = _versions.ForVersion(movie, versionId.Value);
            local.MovieVersionId = versionId;
        }

        public void ValidateForWrite(LocalMovie local, DownloadClientItem download, bool newDownload)
        {
            Prepare(local, download);
            if (!local.MovieVersionId.HasValue)
            {
                return;
            }

            Require(!newDownload || !_config.UseScriptImport, "Script imports require version-aware support before they can manage composite movies.");
            if (local.DownloadReservationId.HasValue)
            {
                Require(_reservations.Get(local.DownloadReservationId.Value).State == DownloadReservationState.Submitted, "This reserved download has already been imported or its state changed.");
            }
            else
            {
                Require(!(_reservations.ForMovie(local.Movie.Id) ?? new List<DownloadReservation>()).Any(r => r.MovieVersionId == local.MovieVersionId && r.IsActive),
                    "This version has an active reserved download. Import using that download's bound identity or reconcile it first.");
            }

            local.Size = _disk.GetFileSize(local.Path);
            Require(local.Size > 0, "An empty file cannot replace a managed version.");
            local.CustomFormats = _formats.ParseCustomFormat(local);
            local.CustomFormatScore = local.Movie.QualityProfile.CalculateCustomFormatScore(local.CustomFormats);
            var decision = _profileSpecification.IsSatisfiedBy(local, download);
            Require(decision.Accepted, decision.Message);

            var current = local.Movie.MovieFile;
            var currentPath = current == null ? null : Path.GetFullPath(Path.Combine(local.Movie.Path, current.RelativePath));
            var sourcePath = Path.GetFullPath(local.Path);
            if (!newDownload)
            {
                Require(current == null || string.Equals(currentPath, sourcePath, StringComparison.OrdinalIgnoreCase), "This version already owns another file. Use an explicit replacement import.");
                var relative = Path.GetRelativePath(local.Movie.Path, sourcePath);
                Require(relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !relative.StartsWith("..\\", StringComparison.Ordinal) && !Path.IsPathRooted(relative), "Existing-file registration must stay inside the movie folder.");
                Require(_files.GetFilesWithRelativePath(local.Movie.Id, relative).All(f => f.Id == current?.Id), "This path already belongs to another file.");
                return;
            }

            var fileName = VersionFileName(local.Movie);
            var destination = Path.GetFullPath(Path.Combine(local.Movie.Path, fileName + Path.GetExtension(local.Path)));
            Require(!string.Equals(sourcePath, destination, StringComparison.OrdinalIgnoreCase), "Source is already at the managed destination. Register it explicitly instead of replacing it.");
            Require(!_disk.FileExists(destination) || string.Equals(currentPath, destination, StringComparison.OrdinalIgnoreCase), "The destination contains an unrelated file. No existing version was removed.");
            var destinationRelative = Path.GetRelativePath(local.Movie.Path, destination);
            Require(_files.GetFilesWithRelativePath(local.Movie.Id, destinationRelative).All(f => f.Id == current?.Id), "The destination belongs to another movie file. No existing version was removed.");
        }

        internal static string VersionFileName(Movie movie)
        {
            var folder = Path.GetFileName(movie.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Require(!string.IsNullOrWhiteSpace(folder) && CompositeProfileValidator.ValidLabel(movie.ManagedVersion?.Label), "Movie folder and a filename-safe version label are required for naming.");
            return folder + " - " + movie.ManagedVersion.Label;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new MovieVersionImportException(message);
            }
        }
    }
}
