using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public static class DownloadReleaseIdentity
    {
        public static List<string> GetClientKeys(DownloadClientItem item)
        {
            var release = new TorrentInfo { Title = item.Title, Size = item.TotalSize };
            if (item.DownloadClientInfo?.Protocol == DownloadProtocol.Torrent && item.DownloadId?.Length is 40 or 64 && item.DownloadId.All(Uri.IsHexDigit))
            {
                release.InfoHash = item.DownloadId;
            }

            return GetKeys(release);
        }

        public static List<string> GetKeys(ReleaseInfo release)
        {
            var keys = new List<string>();
            if (release == null)
            {
                return keys;
            }

            if (release is TorrentInfo torrent && !string.IsNullOrWhiteSpace(torrent.InfoHash))
            {
                keys.Add(Hash("torrent", torrent.InfoHash.ToUpperInvariant()));
            }

            if (!string.IsNullOrWhiteSpace(release.Guid))
            {
                keys.Add(Hash("guid", release.IndexerId.ToString(CultureInfo.InvariantCulture) + ":" + release.Guid));
            }

            if (!string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                keys.Add(Hash("url", release.DownloadUrl));
            }

            if (!string.IsNullOrWhiteSpace(release.Title) && release.Size > 0)
            {
                keys.Add(Hash("title-size", release.Size.ToString(CultureInfo.InvariantCulture) + ":" + release.Title.Trim().ToUpperInvariant()));
            }

            return keys;
        }

        private static string Hash(string kind, string value)
        {
            return kind + ":" + System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}
