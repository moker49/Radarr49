using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Movies
{
    public partial class MovieRepository
    {
        // The caller holds the movie locks and import leases. Retire recovery intents
        // atomically with their owner so startup cannot resurrect a deleted movie.
        public void DeleteWithVersions(IList<int> movieIds)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            foreach (var movieId in movieIds)
            {
                var values = new { movieId, note = "Cancelled by explicit version-aware whole-movie deletion.", updated = DateTime.UtcNow, cancelImport = VersionFileImportState.Cancelled, cancelDeletion = VersionFileDeletionState.Cancelled };
                if (connection.Execute("UPDATE \"Movies\" SET \"Monitored\" = \"Monitored\" WHERE \"Id\" = @movieId", values, transaction) != 1)
                {
                    throw new InvalidOperationException("A selected movie disappeared during deletion. The transaction was rolled back.");
                }

                connection.Execute("UPDATE \"VersionFileImports\" SET \"State\" = @cancelImport, \"Note\" = @note, \"Updated\" = @updated WHERE \"MovieId\" = @movieId AND \"State\" BETWEEN 0 AND 10", values, transaction);
                connection.Execute("UPDATE \"VersionFileDeletions\" SET \"State\" = @cancelDeletion, \"Note\" = @note, \"Updated\" = @updated WHERE \"MovieId\" = @movieId AND \"State\" BETWEEN 0 AND 4", values, transaction);
                connection.Execute("DELETE FROM \"DownloadReservations\" WHERE \"MovieId\" = @movieId", values, transaction);
                connection.Execute("DELETE FROM \"MovieVersions\" WHERE \"MovieId\" = @movieId", values, transaction);
                connection.Execute("DELETE FROM \"Movies\" WHERE \"Id\" = @movieId", values, transaction);
            }

            transaction.Commit();
        }
    }
}
