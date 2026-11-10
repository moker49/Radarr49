using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;

namespace NzbDrone.Core.Test.MediaFiles
{
    public partial class VersionImportHandoffFixture
    {
        private ManagedMovieDeletionService WholeMovieDeletion()
        {
            var repository = Mocker.Resolve<MovieRepository>();
            Mocker.GetMock<IMovieRepository>().Setup(r => r.Get(It.IsAny<IEnumerable<int>>())).Returns(new List<Movie> { Root() });
            Mocker.GetMock<IMovieRepository>().Setup(r => r.DeleteWithVersions(It.IsAny<IList<int>>())).Callback<IList<int>>(ids => repository.DeleteWithVersions(ids));
            return Mocker.Resolve<ManagedMovieDeletionService>();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void whole_movie_delete_should_retire_abandoned_publication_and_its_versions_atomically(bool deleteFiles)
        {
            _reservation = Db.Insert(new DownloadReservation { MovieId = _movie.Id, MovieVersionId = _sdr.Id, State = DownloadReservationState.Submitted, DownloadId = "download-1", DownloadClientId = 7, SourceTitle = "300 SDR" });
            var operation = BeforeBackup();
            _operations.Advance(operation.Id, VersionFileImportState.BackingUp, VersionFileImportState.Installing);
            Assert.That(_operations.Get(operation.Id).State, Is.EqualTo(VersionFileImportState.Installing));
            var filesBefore = _disk.Keys.ToList();
            var service = WholeMovieDeletion();
            var unrelated = Db.Insert(new Movie { Path = "unrelated", MovieMetadataId = 999 });
            var otherVersion = Db.Insert(new MovieVersion { MovieId = unrelated.Id, CompositeProfileVersionId = _hdr.CompositeProfileVersionId });

            service.Delete(new List<int> { _movie.Id }, deleteFiles, true);

            Assert.That(Db.All<Movie>().Select(m => m.Id), Is.EqualTo(new[] { unrelated.Id }));
            Assert.That(Db.All<MovieVersion>().Select(v => v.Id), Is.EqualTo(new[] { otherVersion.Id }));
            Assert.That(Db.All<DownloadReservation>(), Is.Empty);
            Assert.That(_operations.Get(operation.Id).State, Is.EqualTo(VersionFileImportState.Cancelled));
            Assert.That(_operations.Pending(), Is.Empty);
            Assert.That(_disk.Keys, Is.EquivalentTo(filesBefore), "Only the normal deletion event may remove movie files, according to the user's choice; downloads must be preserved.");
            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.Is<MoviesDeletedEvent>(m => m.DeleteFiles == deleteFiles && m.AddImportListExclusion && m.Movies.Single().Id == _movie.Id)), Times.Once());
        }

        [Test]
        public void whole_movie_delete_must_not_race_an_active_verifier()
        {
            var operation = Prepare();
            var service = WholeMovieDeletion();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var worker = Task.Run(() =>
            {
                using var lease = new VersionImportOperationLease(operation.Id);
                entered.Set();
                release.Wait();
            });
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                Assert.Throws<NzbDroneClientException>(() => service.Delete(new List<int> { _movie.Id }, true, false));
                Assert.That(Db.All<Movie>().Single().Id, Is.EqualTo(_movie.Id));
                Assert.That(_operations.Get(operation.Id).IsActive, Is.True);
                Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<MoviesDeletedEvent>()), Times.Never());
            }
            finally
            {
                release.Set();
                Assert.That(worker.Wait(5000), Is.True);
            }
        }

        [Test]
        public void bulk_delete_must_preflight_all_movies_before_retiring_any_import()
        {
            var operation = Prepare();
            var service = WholeMovieDeletion();

            Assert.Throws<NzbDroneClientException>(() => service.Delete(new List<int> { _movie.Id, 99999 }, true, false));

            Assert.That(_operations.Get(operation.Id).IsActive, Is.True);
            Assert.That(Db.All<Movie>().Single().Id, Is.EqualTo(_movie.Id));
        }

        [Test]
        public void database_delete_must_roll_back_retired_operations_when_a_later_movie_is_missing()
        {
            var operation = Prepare();
            var repository = Mocker.Resolve<MovieRepository>();

            Assert.Throws<InvalidOperationException>(() => repository.DeleteWithVersions(new List<int> { _movie.Id, 99999 }));

            Assert.That(_operations.Get(operation.Id).IsActive, Is.True);
            Assert.That(Db.All<Movie>().Single().Id, Is.EqualTo(_movie.Id));
            Assert.That(Db.All<MovieVersion>(), Has.Count.EqualTo(2));
        }

        [Test]
        public void whole_movie_delete_must_not_race_an_active_copy()
        {
            var service = WholeMovieDeletion();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var worker = Task.Run(() =>
            {
                lock (MovieVersionImportSynchronization.ForMovie(_movie.Id))
                {
                    entered.Set();
                    release.Wait();
                }
            });
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                Assert.Throws<NzbDroneClientException>(() => service.Delete(new List<int> { _movie.Id }, true, false));
                Assert.That(Db.All<Movie>().Single().Id, Is.EqualTo(_movie.Id));
                Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<MoviesDeletedEvent>()), Times.Never());
            }
            finally
            {
                release.Set();
                Assert.That(worker.Wait(5000), Is.True);
            }
        }
    }
}
