using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class RenameMovieFileServiceFixture : CoreTest<RenameMovieFileService>
    {
        private Movie _movie;
        private List<MovieFile> _movieFiles;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                                     .Build();

            _movieFiles = Builder<MovieFile>.CreateListOfSize(2)
                .All().With(f => f.IsUnmanaged = false)
                                                .All()
                                                .With(e => e.MovieId = _movie.Id)
                                                .Build()
                                                .ToList();

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovie(_movie.Id))
                  .Returns(_movie);
        }

        private void GivenNoMovieFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<MovieFile>());
        }

        [Test]
        public void unfinished_deletion_should_prevent_physical_rename()
        {
            GivenMovieFiles();
            Mocker.GetMock<IVersionFileDeletionRepository>().Setup(v => v.CheckReady(_movie.Id, null)).Throws(new VersionFileDeletionPendingException());

            Assert.Throws<VersionFileDeletionPendingException>(() => Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1, 2 })));

            Mocker.GetMock<IMoveMovieFiles>().Verify(v => v.MoveMovieFile(It.IsAny<MovieFile>(), It.IsAny<Movie>()), Times.Never());
        }

        private void GivenMovieFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(_movieFiles);
        }

        [Test]
        public void single_file_audit_rename_must_read_ownership_inside_the_movie_lock()
        {
            GivenMovieFiles();
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Callback(() => Assert.That(System.Threading.Monitor.IsEntered(MovieVersionImportSynchronization.ForMovie(_movie.Id)), Is.True))
                .Returns(_movieFiles);
            _movieFiles[1].IsUnmanaged = true;

            Subject.Execute(new RenameFilesCommand(_movie.Id, _movieFiles.Select(f => f.Id).ToList()));

            Mocker.GetMock<IMoveMovieFiles>().Verify(v => v.MoveMovieFile(_movieFiles[0], It.IsAny<Movie>()), Times.Once());
            Mocker.GetMock<IMoveMovieFiles>().Verify(v => v.MoveMovieFile(_movieFiles[1], It.IsAny<Movie>()), Times.Never());
        }

        [Test]
        public void single_file_audit_rename_must_reject_files_from_another_movie_before_any_move()
        {
            GivenMovieFiles();
            _movieFiles[1].MovieId = _movie.Id + 100;

            Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(() => Subject.Execute(new RenameFilesCommand(_movie.Id, _movieFiles.Select(f => f.Id).ToList())));

            Mocker.GetMock<IMoveMovieFiles>().Verify(v => v.MoveMovieFile(It.IsAny<MovieFile>(), It.IsAny<Movie>()), Times.Never());
        }

        private void GivenMovedFiles()
        {
            Mocker.GetMock<IMoveMovieFiles>()
                  .Setup(s => s.MoveMovieFile(It.IsAny<MovieFile>(), _movie));
        }

        [Test]
        public void should_not_publish_event_if_no_files_to_rename()
        {
            GivenNoMovieFiles();

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<MovieRenamedEvent>()), Times.Never());
        }

        [Test]
        public void managed_rename_should_not_submit_a_legacy_movie_update()
        {
            GivenMovieFiles();
            GivenMovedFiles();
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(_movie)).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 1 },
                new ResolvedMovieVersion { Id = 2 }
            });

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1, 2 }));

            Mocker.GetMock<IMovieService>().Verify(v => v.UpdateMovie(It.IsAny<Movie>()), Times.Never());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Update(It.IsAny<MovieFile>()), Times.Exactly(2));
        }

        [Test]
        public void should_not_publish_event_if_no_files_are_renamed()
        {
            GivenMovieFiles();

            Mocker.GetMock<IMoveMovieFiles>()
                  .Setup(s => s.MoveMovieFile(It.IsAny<MovieFile>(), It.IsAny<Movie>()))
                  .Throws(new SameFilenameException("Same file name", "Filename"));

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<MovieRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_publish_event_if_files_are_renamed()
        {
            GivenMovieFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<MovieRenamedEvent>()), Times.Once());
        }

        [Test]
        public void should_update_moved_files()
        {
            GivenMovieFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Update(It.IsAny<MovieFile>()), Times.Exactly(2));
        }

        [Test]
        public void should_get_moviefiles_by_ids_only()
        {
            GivenMovieFiles();
            GivenMovedFiles();

            var files = new List<int> { 1 };

            Subject.Execute(new RenameFilesCommand(_movie.Id, files));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.GetMovies(files), Times.Once());
        }
    }
}
