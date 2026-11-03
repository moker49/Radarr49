using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Commands;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class CompositeLegacyMovieEditorFixture : TestBase<MovieEditorController>
    {
        private List<Movie> _movies;

        [SetUp]
        public void Setup()
        {
            _movies = new List<Movie>
            {
                new Movie { Id = 1, QualityProfileId = 1, Monitored = true },
                new Movie { Id = 2, QualityProfileId = 10, Monitored = true }
            };
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>())).Returns(_movies);
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.ForProfile(It.IsAny<int>()))
                .Returns((int id) => id == 10 ? new List<CompositeProfileVersion> { new CompositeProfileVersion() } : new List<CompositeProfileVersion>());
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.ForMovie(It.IsAny<int>())).Returns(new List<MovieVersion>());
            Mocker.SetConstant<ICompositeProfilePolicy>(Mocker.Resolve<CompositeProfilePolicy>());
        }

        [Test]
        public void mixed_movie_bulk_delete_must_not_delete_any_movie()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.DeleteMovies(new MovieEditorResource
            {
                MovieIds = new List<int> { 1, 2 }, DeleteFiles = true
            }));

            Mocker.GetMock<IMovieService>().Verify(s => s.DeleteMovies(It.IsAny<List<int>>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void mixed_movie_bulk_edit_must_not_mutate_or_schedule_moves()
        {
            Assert.Throws<NzbDroneClientException>(() => Subject.SaveAll(new MovieEditorResource
            {
                MovieIds = new List<int> { 1, 2 }, Monitored = false, RootFolderPath = "C:/Changed", MoveFiles = true
            }));

            Assert.That(_movies.All(m => m.Monitored && m.RootFolderPath == null), Is.True);
            Mocker.GetMock<IMovieService>().Verify(s => s.UpdateMovie(It.IsAny<List<Movie>>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IManageCommandQueue>().Verify(s => s.Push(It.IsAny<BulkMoveMovieCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
        }
    }
}
