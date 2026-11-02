using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ExtraFileVersionServiceFixture : CoreTest<ExtraFileVersionService>
    {
        private Movie _movie;
        private List<ResolvedMovieVersion> _versions;
        private List<MovieFile> _files;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, Path = @"C:\Movies\300 (2006)".AsOsAgnostic(), MovieFileId = 11 };
            _versions = new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 7, MovieFileId = 11, Label = "SDR", IsPrimary = true },
                new ResolvedMovieVersion { Id = 8, MovieFileId = 12, Label = "HDR" }
            };
            _files = new List<MovieFile>
            {
                new MovieFile { Id = 11, MovieId = 1, RelativePath = "300 (2006) - SDR.mkv" },
                new MovieFile { Id = 12, MovieId = 1, RelativePath = "300 (2006) - HDR.mkv" }
            };
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_movie)).Returns(_versions);
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesByMovie(1)).Returns(_files);
        }

        [TestCase("300 (2006) - SDR.en.srt", 7)]
        [TestCase("300 (2006) - HDR.en.forced.srt", 8)]
        [TestCase("300 (2006) - HDR.nfo", 8)]
        public void should_resolve_extra_to_registered_video(string filename, int expected)
        {
            var result = Subject.Resolve(_movie, Path.Combine(_movie.Path, filename));
            Assert.That(result.ManagedVersion.Id, Is.EqualTo(expected));
            Assert.That(result.MovieFile.Id, Is.EqualTo(result.MovieFileId));
            Assert.That(_movie.MovieFileId, Is.EqualTo(11));
        }

        [TestCase("300 (2006).en.srt")]
        [TestCase("300 (2006) - HDR10.en.srt")]
        [TestCase("movie.nfo")]
        [TestCase("poster.jpg")]
        [TestCase("sub.srt")]
        public void ambiguous_extra_should_not_default_to_primary(string filename)
        {
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, filename)), Is.Null);
        }

        [Test]
        public void should_not_guess_from_label_when_actual_filename_differs()
        {
            _files[1].RelativePath = "original-release.mkv";
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, "300 (2006) - HDR.en.srt")), Is.Null);
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, "original-release.en.srt")).ManagedVersion.Id, Is.EqualTo(8));
        }

        [Test]
        public void two_registered_videos_with_same_stem_are_ambiguous()
        {
            _files[1].RelativePath = "300 (2006) - SDR.mp4";
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, "300 (2006) - SDR.en.srt")), Is.Null);
        }

        [Test]
        public void missing_version_file_should_not_claim_a_subtitle()
        {
            _versions[1].MovieFileId = null;
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, "300 (2006) - HDR.en.srt")), Is.Null);
        }

        [Test]
        public void file_specific_artwork_should_resolve_to_its_version()
        {
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, "300 (2006) - HDR-poster.jpg"), true).ManagedVersion.Id, Is.EqualTo(8));
        }

        [Test]
        public void matching_name_in_different_folder_should_not_claim_a_version()
        {
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, "extras", "300 (2006) - HDR.en.srt")), Is.Null);
        }

        [Test]
        public void ordinary_movie_should_retain_existing_context()
        {
            _versions.Clear();
            _versions.Add(new ResolvedMovieVersion());
            Assert.That(Subject.Resolve(_movie, Path.Combine(_movie.Path, "sub.srt")), Is.SameAs(_movie));
        }
    }
}
