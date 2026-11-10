using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Blocklisting
{
    public interface IBlocklistService
    {
        bool Blocklisted(int movieId, ReleaseInfo release);
        bool Blocklisted(int movieId, ReleaseInfo release, int? movieVersionId);
        bool BlocklistedTorrentHash(int movieId, string hash);
        bool BlocklistedTorrentHash(int movieId, string hash, int? movieVersionId);
        PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec);
        List<Blocklist> GetByMovieId(int movieId);
        void Block(RemoteMovie remoteMovie, string message);
        void Delete(int id);
        void Delete(List<int> ids);
    }

    public class BlocklistService : IBlocklistService,

                                    IExecute<ClearBlocklistCommand>,
                                    IHandle<DownloadFailedEvent>,
                                    IHandleAsync<MoviesDeletedEvent>
    {
        private readonly IBlocklistRepository _blocklistRepository;

        public BlocklistService(IBlocklistRepository blocklistRepository)
        {
            _blocklistRepository = blocklistRepository;
        }

        public bool Blocklisted(int movieId, ReleaseInfo release) => Blocklisted(movieId, release, null);

        public bool Blocklisted(int movieId, ReleaseInfo release, int? movieVersionId)
        {
            if (release.DownloadProtocol == DownloadProtocol.Torrent)
            {
                if (release is not TorrentInfo torrentInfo)
                {
                    return false;
                }

                if (torrentInfo.InfoHash.IsNotNullOrWhiteSpace())
                {
                    var blocklistedByTorrentInfohash = _blocklistRepository.BlocklistedByTorrentInfoHash(movieId, torrentInfo.InfoHash);

                    return blocklistedByTorrentInfohash.Where(b => b.MovieVersionId == null || b.MovieVersionId == movieVersionId).Any(b => SameTorrent(b, torrentInfo));
                }

                return _blocklistRepository.BlocklistedByTitle(movieId, release.Title)
                    .Where(b => b.Protocol == DownloadProtocol.Torrent && (b.MovieVersionId == null || b.MovieVersionId == movieVersionId))
                    .Any(b => SameTorrent(b, torrentInfo));
            }

            return _blocklistRepository.BlocklistedByTitle(movieId, release.Title)
                .Where(b => b.Protocol == DownloadProtocol.Usenet && (b.MovieVersionId == null || b.MovieVersionId == movieVersionId))
                .Any(b => SameNzb(b, release));
        }

        public bool BlocklistedTorrentHash(int movieId, string hash) => BlocklistedTorrentHash(movieId, hash, null);

        public bool BlocklistedTorrentHash(int movieId, string hash, int? movieVersionId)
        {
            return _blocklistRepository.BlocklistedByTorrentInfoHash(movieId, hash).Where(b => b.MovieVersionId == null || b.MovieVersionId == movieVersionId).Any(b =>
                b.TorrentInfoHash.Equals(hash, StringComparison.InvariantCultureIgnoreCase));
        }

        public PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec)
        {
            return _blocklistRepository.GetPaged(pagingSpec);
        }

        public List<Blocklist> GetByMovieId(int movieId)
        {
            return _blocklistRepository.BlocklistedByMovie(movieId);
        }

        public void Block(RemoteMovie remoteMovie, string message)
        {
            var blocklist = new Blocklist
                            {
                                MovieId = remoteMovie.Movie.Id,
                                SourceTitle =  remoteMovie.Release.Title,
                                Quality = remoteMovie.ParsedMovieInfo.Quality,
                                Date = DateTime.UtcNow,
                                PublishedDate = remoteMovie.Release.PublishDate,
                                Size = remoteMovie.Release.Size,
                                Indexer = remoteMovie.Release.Indexer,
                                Protocol = remoteMovie.Release.DownloadProtocol,
                                Message = message,
                                Languages = remoteMovie.ParsedMovieInfo.Languages
                            };

            if (remoteMovie.Release is TorrentInfo torrentRelease)
            {
                blocklist.TorrentInfoHash = torrentRelease.InfoHash;
            }

            _blocklistRepository.Insert(blocklist);
        }

        public void Delete(int id)
        {
            _blocklistRepository.Delete(id);
        }

        public void Delete(List<int> ids)
        {
            _blocklistRepository.DeleteMany(ids);
        }

        private bool SameNzb(Blocklist item, ReleaseInfo release)
        {
            return ReleaseComparer.SameNzb(new ReleaseComparerModel(item), release);
        }

        private bool SameTorrent(Blocklist item, TorrentInfo release)
        {
            return ReleaseComparer.SameTorrent(new ReleaseComparerModel(item), release);
        }

        public void Execute(ClearBlocklistCommand message)
        {
            _blocklistRepository.Purge();
        }

        public void Handle(DownloadFailedEvent message)
        {
            var blocklist = new Blocklist
            {
                MovieId = message.MovieId,
                MovieVersionId = message.MovieVersionId,
                SourceTitle = message.SourceTitle,
                Quality = message.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = DateTime.Parse(message.Data.GetValueOrDefault("publishedDate")),
                Size = long.Parse(message.Data.GetValueOrDefault("size", "0")),
                Indexer = message.Data.GetValueOrDefault("indexer"),
                Protocol = (DownloadProtocol)Convert.ToInt32(message.Data.GetValueOrDefault("protocol")),
                Message = message.Message,
                Languages = message.Languages,
                TorrentInfoHash = message.TrackedDownload?.Protocol == DownloadProtocol.Torrent
                    ? message.TrackedDownload.DownloadItem.DownloadId
                    : message.Data.GetValueOrDefault("torrentInfoHash", null)
            };

            if (Enum.TryParse(message.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
            {
                blocklist.IndexerFlags = flags;
            }

            _blocklistRepository.Insert(blocklist);
        }

        public void HandleAsync(MoviesDeletedEvent message)
        {
            _blocklistRepository.DeleteForMovies(message.Movies.Select(m => m.Id).ToList());
        }
    }
}
