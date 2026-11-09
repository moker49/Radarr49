using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.MediaFiles
{
    // Reject concurrent recovery rather than waiting while another caller holds a movie lock.
    public sealed class VersionImportOperationLease : IDisposable
    {
        private static readonly ConcurrentDictionary<int, object> Operations = new();
        private static readonly ConcurrentDictionary<int, int> Active = new();
        private readonly object _sync;
        private readonly int _operationId;

        public static bool IsRunning(int operationId) => Active.TryGetValue(operationId, out var count) && count > 0;

        public VersionImportOperationLease(int operationId)
        {
            _operationId = operationId;
            _sync = Operations.GetOrAdd(operationId, _ => new object());
            if (!Monitor.TryEnter(_sync))
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "This import operation is already running. Wait for its current phase before requesting recovery.");
            }

            Active.AddOrUpdate(operationId, 1, (_, count) => count + 1);
        }

        public void Dispose()
        {
            Monitor.Exit(_sync);
            Active.AddOrUpdate(_operationId, 0, (_, count) => count - 1);
        }
    }
}
