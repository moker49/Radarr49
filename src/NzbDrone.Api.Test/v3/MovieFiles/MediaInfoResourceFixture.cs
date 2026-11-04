using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Movies;
using Radarr.Api.V3.MovieFiles;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.MovieFiles
{
    [TestFixture]
    public class MediaInfoResourceFixture
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void missing_language_or_subtitle_lists_should_not_break_movie_listing(bool hasAudioLanguages, bool hasSubtitles)
        {
            var mediaInfo = new MediaInfoModel
            {
                AudioLanguages = hasAudioLanguages ? new List<string> { "English", "French" } : null,
                Subtitles = hasSubtitles ? new List<string> { "English", "Spanish" } : null,
                VideoBitrate = 12000000,
                Width = 1920,
                Height = 1080,
                RunTime = TimeSpan.FromMinutes(117)
            };
            var movie = new Movie
            {
                Id = 1,
                MovieMetadata = new MovieMetadata { Title = "Movie", TmdbId = 1, Year = 2006 },
                Path = @"C:\Movies\Movie (2006)",
                MovieFileId = 7,
                MovieFile = new MovieFile { Id = 7, MovieId = 1, RelativePath = "Movie (2006).mkv", MediaInfo = mediaInfo }
            };

            var resource = new[] { movie }.ToResource(0)[0];

            Assert.That(resource.MovieFile.Id, Is.EqualTo(7));
            Assert.That(resource.MovieFile.MediaInfo.AudioLanguages, Is.EqualTo(hasAudioLanguages ? "English/French" : string.Empty));
            Assert.That(resource.MovieFile.MediaInfo.Subtitles, Is.EqualTo(hasSubtitles ? "English/Spanish" : string.Empty));
            Assert.That(resource.MovieFile.MediaInfo.VideoBitrate, Is.EqualTo(12000000));
            Assert.That(resource.MovieFile.MediaInfo.Resolution, Is.EqualTo("1920x1080"));
            Assert.That(resource.MovieFile.MediaInfo.RunTime, Is.EqualTo("1:57:00"));
            Assert.That(mediaInfo.AudioLanguages == null, Is.EqualTo(!hasAudioLanguages));
            Assert.That(mediaInfo.Subtitles == null, Is.EqualTo(!hasSubtitles));
        }

        [Test]
        public void missing_media_info_should_remain_null()
        {
            Assert.That(((MediaInfoModel)null).ToResource(null), Is.Null);
        }

        [Test]
        public void empty_collections_should_remain_empty_strings()
        {
            var mediaInfo = new MediaInfoModel { AudioLanguages = new List<string>(), Subtitles = new List<string>() };

            var resource = mediaInfo.ToResource(null);

            Assert.That(resource.AudioLanguages, Is.Empty);
            Assert.That(resource.Subtitles, Is.Empty);
        }
    }
}
