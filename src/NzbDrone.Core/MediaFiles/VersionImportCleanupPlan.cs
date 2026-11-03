using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaFiles
{
    internal static class VersionImportCleanupPlan
    {
        public static List<VersionImportCleanupItem> Create(VersionFileImport operation)
        {
            var snapshot = operation.Snapshot;
            var result = new List<VersionImportCleanupItem>();
            foreach (var extra in snapshot.PreviousExtras)
            {
                var replaced = snapshot.IncomingExtras.Any(i => Full(operation, i.DestinationRelativePath).PathEquals(Full(operation, extra.RelativePath)));
                result.Add(new VersionImportCleanupItem
                {
                    Kind = VersionImportCleanupKind.OldExtra, Path = extra.RelativePath, Existed = extra.SourceExisted,
                    Size = extra.Size, ContentHash = extra.ContentHash, Table = extra.Table, ExtraId = extra.Id,
                    Preserve = extra.Preserve || replaced
                });
            }

            if (operation.BackupRelativePath != null)
            {
                result.Add(new VersionImportCleanupItem
                {
                    Kind = VersionImportCleanupKind.Backup, Path = operation.BackupRelativePath,
                    Existed = snapshot.PreviousExisted, Size = snapshot.Previous?.Size ?? 0, ContentHash = snapshot.PreviousHash
                });
            }

            foreach (var extra in snapshot.IncomingExtras)
            {
                var old = snapshot.PreviousExtras.FirstOrDefault(e => Full(operation, e.RelativePath).PathEquals(Full(operation, extra.DestinationRelativePath)) && e.SourceExisted);
                result.Add(new VersionImportCleanupItem
                {
                    Kind = VersionImportCleanupKind.Backup, Path = extra.BackupRelativePath, Existed = old != null,
                    Size = old?.Size ?? 0, ContentHash = old?.ContentHash
                });
            }

            if (!operation.CopyOnly)
            {
                result.Add(new VersionImportCleanupItem { Kind = VersionImportCleanupKind.Source, Path = operation.SourcePath, Existed = true, Size = snapshot.Incoming.Size, ContentHash = snapshot.SourceHash });
                result.AddRange(snapshot.IncomingExtras.Select(e => new VersionImportCleanupItem { Kind = VersionImportCleanupKind.Source, Path = e.SourcePath, Existed = true, Size = e.Size, ContentHash = e.ContentHash }));
            }

            foreach (var item in result.Where(i => !i.Preserve && i.Existed))
            {
                if (item.ContentHash?.Length != 64 || !item.ContentHash.All(Uri.IsHexDigit))
                {
                    throw new InvalidOperationException("A cleanup entry lacks its original SHA-256 identity.");
                }
            }

            return result;
        }

        private static string Full(VersionFileImport operation, string relative) => Path.GetFullPath(Path.Combine(operation.MoviePath, relative));

        public static VersionFileImportState Phase(VersionImportCleanupKind kind) => kind switch
        {
            VersionImportCleanupKind.OldExtra => VersionFileImportState.CleaningOldExtras,
            VersionImportCleanupKind.Backup => VersionFileImportState.RecyclingBackup,
            VersionImportCleanupKind.Source => VersionFileImportState.RemovingSource,
            _ => throw new InvalidOperationException("Unknown import cleanup category.")
        };
    }
}
