using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NzbDrone.Core.MediaFiles
{
    public static class VersionImportRecoveryCheckpoint
    {
        public static string For(VersionFileImport operation)
        {
            var snapshot = operation.Snapshot;
            var json = JsonSerializer.Serialize(new
            {
                operation.Id, operation.MovieId, operation.MovieVersionId, operation.OldMovieFileId,
                operation.NewMovieFileId, operation.State, operation.Updated, operation.MoviePath,
                operation.SourcePath, operation.DestinationRelativePath, operation.CopyOnly,
                snapshot.SourceHash, snapshot.LastRecoveryError,
                Extras = snapshot.IncomingExtras.Select(e => new { e.State, e.RegisteredExtraId, e.SourcePath, e.DestinationRelativePath, e.ContentHash }),
                Cleanup = snapshot.Cleanup?.Select(c => new { c.Kind, c.Path, c.State, c.Preserve, c.ContentHash, c.Note }),
                Events = snapshot.Events?.Deliveries.Select(e => new { e.Kind, e.State })
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }

        public static int? CurrentCleanupIndex(VersionFileImport operation)
        {
            var index = operation.Snapshot.Cleanup?.FindIndex(c => c.State != VersionImportCleanupState.Finished);
            return index >= 0 ? index : null;
        }
    }
}
