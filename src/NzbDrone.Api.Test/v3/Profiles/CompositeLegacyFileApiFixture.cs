using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.MovieFiles;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class CompositeLegacyFileApiFixture : TestBase<MovieFileController>
    {
        private List<Movie> _movies;
        private List<MovieFile> _files;

        [SetUp]
        public void Setup()
        {
            _movies = new List<Movie>
            {
                new Movie { Id = 1, QualityProfileId = 1 },
                new Movie { Id = 2, QualityProfileId = 10 }
            };
            _files = new List<MovieFile>
            {
                new MovieFile { Id = 101, MovieId = 1 },
                new MovieFile { Id = 102, MovieId = 2 }
            };
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns((IEnumerable<int> ids) => _movies.Where(m => ids.Contains(m.Id)).ToList());
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(It.IsAny<int>()))
                .Returns((int id) => _movies.Single(m => m.Id == id));
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns((IEnumerable<int> ids) => _files.Where(f => ids.Contains(f.Id)).ToList());
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovie(It.IsAny<int>()))
                .Returns((int id) => _files.Single(f => f.Id == id));
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.ForProfile(It.IsAny<int>()))
                .Returns((int id) => id == 10 ? new List<CompositeProfileVersion> { new CompositeProfileVersion() } : new List<CompositeProfileVersion>());
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.ForMovie(It.IsAny<int>())).Returns(new List<MovieVersion>());
            Mocker.SetConstant<ICompositeProfilePolicy>(Mocker.Resolve<CompositeProfilePolicy>());
        }

        [Test]
        public void retained_unmanaged_file_must_not_be_deleted_through_legacy_routes()
        {
            _movies[1].QualityProfileId = 1;
            _files[1].IsUnmanaged = true;

            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovieFile(102));
            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovieFiles(new MovieFileListResource { MovieFileIds = new List<int> { 101, 102 } }));

            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never());
        }

        [Test]
        public void mixed_bulk_delete_must_not_delete_any_file()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovieFiles(new MovieFileListResource { MovieFileIds = new List<int> { 101, 102 } }));

            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never());
        }

        [Test]
        public void ordinary_bulk_delete_should_use_each_files_own_movie()
        {
            _movies[1].QualityProfileId = 1;

            Subject.DeleteMovieFiles(new MovieFileListResource { MovieFileIds = new List<int> { 101, 102 } });

            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(_movies[0], _files[0]), Times.Once());
            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(_movies[1], _files[1]), Times.Once());
        }

        [Test]
        public void single_composite_file_delete_should_be_denied()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovieFile(102));

            Mocker.GetMock<IDeleteMediaFiles>().Verify(s => s.DeleteMovieFile(It.IsAny<Movie>(), It.IsAny<MovieFile>()), Times.Never());
        }

        [Test]
        public void bulk_edit_must_be_denied_before_mutating_any_file()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.SetPropertiesBulk(new List<MovieFileResource>
            {
                new MovieFileResource { Id = 101, Edition = "Changed" },
                new MovieFileResource { Id = 102, Edition = "Changed" }
            }));

            Assert.That(_files.All(f => f.Edition == null), Is.True);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Update(It.IsAny<List<MovieFile>>()), Times.Never());
        }
    }
}
