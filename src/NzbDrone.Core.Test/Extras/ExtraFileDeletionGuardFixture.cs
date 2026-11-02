using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ExtraFileDeletionGuardFixture : CoreTest<ExtraFileDeletionGuard>
    {
        private Movie _movie;
        private List<SubtitleFile> _subtitles;
        private List<MetadataFile> _metadata;
        private List<OtherExtraFile> _others;
        private List<MovieFile> _videos;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, Path = @"C:\Movies\300 (2006)".AsOsAgnostic() };
            _subtitles = new List<SubtitleFile>();
            _metadata = new List<MetadataFile>();
            _others = new List<OtherExtraFile>();
            _videos = new List<MovieFile>();
            Mocker.GetMock<IMovieService>().Setup(v => v.AllMoviePaths()).Returns(new Dictionary<int, string> { { 1, _movie.Path } });
            Mocker.GetMock<IMediaFileRepository>().Setup(v => v.GetFilesByMovie(1)).Returns(_videos);
            Mocker.GetMock<IExtraFileRepository<SubtitleFile>>().Setup(v => v.GetFilesByMovie(1)).Returns(_subtitles);
            Mocker.GetMock<IExtraFileRepository<MetadataFile>>().Setup(v => v.GetFilesByMovie(1)).Returns(_metadata);
            Mocker.GetMock<IExtraFileRepository<OtherExtraFile>>().Setup(v => v.GetFilesByMovie(1)).Returns(_others);
        }

        [Test]
        public void unclaimed_path_belonging_to_removed_file_can_be_recycled()
        {
            _subtitles.Add(new SubtitleFile { MovieFileId = 11, RelativePath = "SDR.en.srt" });
            Assert.That(Subject.CanRecycle(_movie, 11, "SDR.en.srt"), Is.True);
        }

        [Test]
        public void replacement_subtitle_claim_should_protect_its_path()
        {
            _subtitles.Add(new SubtitleFile { MovieFileId = 12, RelativePath = "SDR.en.srt" });
            Assert.That(Subject.CanRecycle(_movie, 11, "SDR.en.srt"), Is.False);
        }

        [Test]
        public void metadata_claim_in_another_category_should_protect_its_path()
        {
            _metadata.Add(new MetadataFile { MovieFileId = 12, RelativePath = "HDR.nfo", Type = MetadataType.MovieMetadata });
            Assert.That(Subject.CanRecycle(_movie, 11, "HDR.nfo"), Is.False);
        }

        [Test]
        public void other_extra_claim_should_protect_its_path()
        {
            _others.Add(new OtherExtraFile { MovieFileId = 12, RelativePath = "HDR.jpg" });
            Assert.That(Subject.CanRecycle(_movie, 11, "HDR.jpg"), Is.False);
        }

        [TestCase("movie.nfo", MetadataType.MovieMetadata)]
        [TestCase("300 (2006).nfo", MetadataType.MovieMetadata)]
        [TestCase("poster.jpg", MetadataType.MovieImage)]
        public void recognized_shared_metadata_should_survive_even_old_file_owned_registration(string path, MetadataType type)
        {
            _metadata.Add(new MetadataFile { MovieFileId = 11, RelativePath = path, Type = type });
            Assert.That(Subject.CanRecycle(_movie, 11, path), Is.False);
        }

        [Test]
        public void file_specific_metadata_owned_only_by_removed_file_can_be_recycled()
        {
            _metadata.Add(new MetadataFile { MovieFileId = 11, RelativePath = "300 (2006) - SDR.nfo", Type = MetadataType.MovieMetadata });
            Assert.That(Subject.CanRecycle(_movie, 11, "300 (2006) - SDR.nfo"), Is.True);
        }

        [Test]
        public void shared_metadata_with_no_file_owner_should_protect_the_path()
        {
            _metadata.Add(new MetadataFile { MovieFileId = null, RelativePath = "shared.xml", Type = MetadataType.MovieMetadata });
            Assert.That(Subject.CanRecycle(_movie, 11, "shared.xml"), Is.False);
        }

        [Test]
        public void registered_video_should_never_be_recycled_as_an_extra()
        {
            _videos.Add(new MovieFile { Id = 12, RelativePath = "HDR.mkv" });
            Assert.That(Subject.CanRecycle(_movie, 11, "HDR.mkv"), Is.False);
        }

        [TestCase("../outside.nfo")]
        [TestCase("C:\\outside.nfo")]
        public void path_outside_the_movie_folder_should_be_protected(string path)
        {
            Assert.That(Subject.CanRecycle(_movie, 11, path.AsOsAgnostic()), Is.False);
        }

        [Test]
        public void another_movies_folder_should_be_protected()
        {
            Mocker.GetMock<IMovieService>().Setup(v => v.AllMoviePaths()).Returns(new Dictionary<int, string> { { 2, _movie.Path } });
            Assert.That(Subject.CanRecycle(_movie, 11, "SDR.en.srt"), Is.False);
        }
    }
}
