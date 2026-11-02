using System.Collections.Generic;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests
{
    [TestFixture]
    public class MovieVersionAssignmentServiceFixture : CoreTest<MovieVersionAssignmentService>
    {
        private Movie _movie;
        private List<MovieFile> _files;
        private List<MovieVersion> _versions;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, QualityProfileId = 1 };
            _files = new List<MovieFile>();
            _versions = new List<MovieVersion>();
            var definitions = new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { Id = 101, CompositeProfileId = 10, QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { Id = 102, CompositeProfileId = 10, QualityProfileId = 2, Label = "HDR", Priority = 1 }
            };
            Mocker.GetMock<IMovieRepository>().Setup(s => s.Get(1)).Returns(_movie);
            Mocker.GetMock<IQualityProfileRepository>().Setup(s => s.Get(10)).Returns(new QualityProfile { Id = 10 });
            Mocker.GetMock<IQualityProfileRepository>().Setup(s => s.Exists(It.IsAny<int>())).Returns(true);
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.ForProfile(It.IsAny<int>()))
                .Returns((int id) => id == 10 ? definitions : new List<CompositeProfileVersion>());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(1)).Returns(_files);
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.ForMovie(1)).Returns(_versions);
        }

        [Test]
        public void fileless_movie_should_get_two_empty_assignments()
        {
            var versions = Subject.Assign(1, 10);

            Assert.That(versions.Count, Is.EqualTo(2));
            Assert.That(versions[0].MovieFileId, Is.Null);
            Assert.That(versions[1].MovieFileId, Is.Null);
            Mocker.GetMock<IMovieVersionRepository>().Verify(s => s.AssignComposite(_movie, 10, versions, null), Times.Once());
        }

        [Test]
        public void existing_file_requires_explicit_assignment()
        {
            _files.Add(new MovieFile { Id = 50, MovieId = 1 });
            _movie.MovieFileId = 50;

            Assert.Throws<ValidationException>(() => Subject.Assign(1, 10));
            Mocker.GetMock<IMovieVersionRepository>().Verify(s => s.AssignComposite(It.IsAny<Movie>(), It.IsAny<int>(), It.IsAny<List<MovieVersion>>(), It.IsAny<int?>()), Times.Never());
        }

        [Test]
        public void explicit_assignment_should_preserve_existing_file_and_primary_summary()
        {
            _files.Add(new MovieFile { Id = 50, MovieId = 1 });
            _movie.MovieFileId = 50;

            var versions = Subject.Assign(1, 10, new Dictionary<int, int?> { [101] = 50, [102] = null });

            Assert.That(versions[0].MovieFileId, Is.EqualTo(50));
            Mocker.GetMock<IMovieVersionRepository>().Verify(s => s.AssignComposite(_movie, 10, versions, 50), Times.Once());
        }

        [Test]
        public void should_not_lose_an_unassigned_existing_file()
        {
            _files.Add(new MovieFile { Id = 50, MovieId = 1 });

            Assert.Throws<ValidationException>(() => Subject.Assign(1, 10, new Dictionary<int, int?> { [101] = null, [102] = null }));
        }

        [Test]
        public void should_reject_foreign_file_assignment()
        {
            Assert.Throws<ValidationException>(() => Subject.Assign(1, 10, new Dictionary<int, int?> { [101] = 500, [102] = null }));
        }

        [Test]
        public void repeated_identical_assignment_should_preserve_version_ids()
        {
            _movie.QualityProfileId = 10;
            _versions.AddRange(new[]
            {
                new MovieVersion { Id = 201, MovieId = 1, CompositeProfileVersionId = 101 },
                new MovieVersion { Id = 202, MovieId = 1, CompositeProfileVersionId = 102 }
            });

            Assert.That(Subject.Assign(1, 10), Is.SameAs(_versions));
            Mocker.GetMock<IMovieVersionRepository>().Verify(s => s.AssignComposite(It.IsAny<Movie>(), It.IsAny<int>(), It.IsAny<List<MovieVersion>>(), It.IsAny<int?>()), Times.Never());
        }
    }
}
