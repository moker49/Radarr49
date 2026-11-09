using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly IDownloadImportPipeline _pipeline;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IEventAggregator eventAggregator,
                                         Logger logger,
                                         IDownloadImportPipeline pipeline)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _pipeline = pipeline;
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                lock (trackedDownload)
                {
                    if (!trackedDownload.DownloadItem.Removed && trackedDownload.DownloadItem.CanBeRemoved && trackedDownload.State == TrackedDownloadState.Imported)
                    {
                        _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
                    }
                }
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                              .Where(t => t.IsTrackable)
                                              .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                try
                {
                    // Process completed items followed by failed, this allows failed imports to have
                    // their state changed and be processed immediately instead of the next execution.

                    if (enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending)
                    {
                        var key = $"{trackedDownload.DownloadClient}:{trackedDownload.DownloadItem.DownloadId}";
                        _pipeline.QueueCopy(key, () =>
                        {
                            try
                            {
                                if (!trackedDownload.DownloadItem.Removed && trackedDownload.State == TrackedDownloadState.ImportPending &&
                                    _trackedDownloadService.GetTrackedDownloads().Any(t => ReferenceEquals(t, trackedDownload) && t.IsTrackable))
                                {
                                    _completedDownloadService.Import(trackedDownload);
                                }
                            }
                            catch (Exception ex)
                            {
                                trackedDownload.State = TrackedDownloadState.ImportBlocked;
                                trackedDownload.Warn("Import failed: {0}", ex.Message);
                                _logger.Debug(ex, "Failed to import download: {0}", trackedDownload.DownloadItem.Title);
                            }
                            finally
                            {
                                RemoveCompletedDownloads();
                                _eventAggregator.PublishEvent(new TrackedDownloadStateChangedEvent(trackedDownload));
                                _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
                            }
                        });
                    }

                    if (trackedDownload.State == TrackedDownloadState.FailedPending)
                    {
                        _failedDownloadService.ProcessFailed(trackedDownload);
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }
    }
}
