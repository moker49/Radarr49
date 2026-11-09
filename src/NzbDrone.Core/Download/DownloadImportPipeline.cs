using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public interface IDownloadImportPipeline
    {
        bool QueueCopy(string key, Action work);
        bool QueueVerification(string key, Action work);
    }

    public class DownloadImportPipeline : IDownloadImportPipeline, IHandle<ApplicationShutdownRequested>
    {
        private readonly BlockingCollection<(string Key, Action Work)> _copies = new();
        private readonly BlockingCollection<(string Key, Action Work)> _verifications = new();
        private readonly ConcurrentDictionary<string, byte> _copyKeys = new();
        private readonly ConcurrentDictionary<string, byte> _verificationKeys = new();
        private readonly object _lifecycle = new();
        private readonly Logger _logger;
        private volatile bool _stopping;
        private bool _started;

        public DownloadImportPipeline(Logger logger)
        {
            _logger = logger;
        }

        public bool QueueCopy(string key, Action work) => Add(_copies, _copyKeys, key, work);
        public bool QueueVerification(string key, Action work) => Add(_verifications, _verificationKeys, key, work);

        private bool Add(BlockingCollection<(string Key, Action Work)> queue, ConcurrentDictionary<string, byte> keys, string key, Action work)
        {
            lock (_lifecycle)
            {
                if (_stopping || !keys.TryAdd(key, 0))
                {
                    return false;
                }

                if (!_started)
                {
                    _started = true;
                    Task.Factory.StartNew(() => Run(_copies, _copyKeys, true), TaskCreationOptions.LongRunning);
                    Task.Factory.StartNew(() => Run(_verifications, _verificationKeys, false), TaskCreationOptions.LongRunning);
                }

                queue.Add((key, work));
                return true;
            }
        }

        private void Run(BlockingCollection<(string Key, Action Work)> queue, ConcurrentDictionary<string, byte> keys, bool copying)
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (!_stopping)
                    {
                        using var scope = copying ? new VersionImportCopyScope() : null;
                        _logger.Debug("Starting {0} worker for download {1}", copying ? "copy" : "verification", job.Key);
                        job.Work();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Download import worker failed for {0}; persisted import checkpoints are retained", job.Key);
                }
                finally
                {
                    keys.TryRemove(job.Key, out _);
                }
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            lock (_lifecycle)
            {
                _stopping = true;
                _copies.CompleteAdding();
                _verifications.CompleteAdding();
            }
        }
    }
}
