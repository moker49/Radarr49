using System;
using System.IO;
using System.Linq;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IManagedMovieImportService
    {
        MovieFile Import(LocalMovie local, MovieFile incoming, bool copyOnly);
    }

    public class ManagedMovieImportService : IManagedMovieImportService
    {
        private readonly IVersionImportHandoffService _handoff;
        private readonly IVersionImportRecoveryService _recovery;
        private readonly IVersionFileImportRepository _operations;
        private readonly IMovieService _movies;
        private readonly IMovieVersionService _versions;
        private readonly IMediaFileService _files;

        public ManagedMovieImportService(IVersionImportHandoffService handoff,
                                         IVersionImportRecoveryService recovery,
                                         IVersionFileImportRepository operations,
                                         IMovieService movies,
                                         IMovieVersionService versions,
                                         IMediaFileService files)
        {
            _handoff = handoff;
            _recovery = recovery;
            _operations = operations;
            _movies = movies;
            _versions = versions;
            _files = files;
        }

        public MovieFile Import(LocalMovie local, MovieFile incoming, bool copyOnly)
        {
            lock (MovieVersionImportSynchronization.ForMovie(local.Movie.Id))
            {
                VersionFileImport operation = null;
                try
                {
                    if (!local.MovieVersionId.HasValue || local.ScriptImported)
                    {
                        throw new MovieVersionImportException("A managed import requires an explicit version and a supported source import.");
                    }

                    using var evidence = new VersionImportVerificationScope();
                    operation = _handoff.Prepare(local, incoming, copyOnly, deferSourceHash: true);
                    if (VersionImportCopyScope.IsActive)
                    {
                        _handoff.Publish(operation.MovieId, operation.Id);
                        local.PendingImport = new PendingVersionImport { MovieId = operation.MovieId, OperationId = operation.Id, Evidence = evidence.Capture() };
                        return incoming;
                    }

                    _recovery.Resume(operation.MovieId, operation.Id);
                    var finished = _operations.Get(operation.Id);
                    if (finished.State != VersionFileImportState.Completed || !finished.NewMovieFileId.HasValue)
                    {
                        throw new InvalidOperationException("The managed import is not fully finalized.");
                    }

                    var root = _movies.GetMovie(operation.MovieId);
                    var version = _versions.Get(root, operation.MovieVersionId);
                    var file = _files.GetFilesByMovie(operation.MovieId).SingleOrDefault(f => f.Id == finished.NewMovieFileId);
                    if (version.MovieFileId != finished.NewMovieFileId || file?.MovieId != operation.MovieId)
                    {
                        throw new InvalidOperationException("The completed import is no longer assigned to its managed version.");
                    }

                    file.MovieVersionId = operation.MovieVersionId;
                    file.Path = Path.Combine(root.Path, file.RelativePath);
                    local.Movie = root.WithVersion(version, file);
                    file.Movie = local.Movie;
                    return file;
                }
                catch (Exception ex) when (operation != null)
                {
                    throw new MovieVersionImportException($"Managed import operation {operation.Id} requires inspection through the movie's version-file import recovery API: {ex.Message}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or NzbDroneClientException)
                {
                    throw new MovieVersionImportException(ex.Message);
                }
            }
        }
    }
}
