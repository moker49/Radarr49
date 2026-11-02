using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.Download
{
    public interface ICompositeDownloadScheduler
    {
        bool CanGrab(RemoteMovie movie, List<DownloadDecision> grabbed);
    }

    public class CompositeDownloadScheduler : ICompositeDownloadScheduler
    {
        private readonly IQueueService _queue;
        private readonly IQualityProfileService _profiles;

        public CompositeDownloadScheduler(IQueueService queue, IQualityProfileService profiles)
        {
            _queue = queue;
            _profiles = profiles;
        }

        public bool CanGrab(RemoteMovie movie, List<DownloadDecision> grabbed)
        {
            var active = _queue.GetQueue().Where(q => q.RemoteMovie?.Movie?.Id == movie.Movie.Id &&
                q.TrackedDownloadState is not (TrackedDownloadState.Imported or TrackedDownloadState.Ignored or TrackedDownloadState.Failed or TrackedDownloadState.FailedPending))
                .Select(q => q.RemoteMovie).ToList();
            foreach (var decision in grabbed.Where(d => d.RemoteMovie.Movie.Id == movie.Movie.Id))
            {
                if (!active.Any(q => SameRelease(q.Release, decision.RemoteMovie.Release)))
                {
                    active.Add(decision.RemoteMovie);
                }
            }

            if (active.Any(q => q.MovieVersionId == movie.MovieVersionId || SameRelease(q.Release, movie.Release)))
            {
                return false;
            }

            var limit = _profiles.Get(movie.Movie.QualityProfileId).MaximumConcurrentDownloads ?? 1;
            return active.Count < Math.Clamp(limit, 1, 2);
        }

        internal static bool SameRelease(ReleaseInfo left, ReleaseInfo right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is TorrentInfo a && right is TorrentInfo b && !string.IsNullOrWhiteSpace(a.InfoHash) &&
                string.Equals(a.InfoHash, b.InfoHash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (!string.IsNullOrWhiteSpace(left.DownloadUrl) && left.DownloadUrl == right.DownloadUrl) ||
                (!string.IsNullOrWhiteSpace(left.Guid) && left.Guid == right.Guid && left.IndexerId == right.IndexerId) ||
                (!string.IsNullOrWhiteSpace(left.Title) && left.Title == right.Title && left.Size > 0 && left.Size == right.Size);
        }
    }
}
