using System;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class ManagedMediaFileServiceFixture : CoreTest<MediaFileService>
    {
        private MovieFile _file;

        [SetUp]
        public void Setup()
        {
            _file = new MovieFile { Id = 11, MovieId = 1, RelativePath = "SDR.mkv", Movie = new Movie { Id = 1, Path = @"C:\Movies\300 (2006)".AsOsAgnostic() } };
            Mocker.GetMock<IMediaFileRepository>().Setup(v => v.DeleteManagedVersionFile(_file, 7, It.IsAny<bool>())).Returns(7);
        }

        [Test]
        public void ordinary_metadata_updates_must_not_clear_unmanaged_ownership()
        {
            Mocker.GetMock<IMediaFileRepository>().Setup(r => r.Find(_file.Id)).Returns(new MovieFile { Id = _file.Id, IsUnmanaged = true, ManagementProfileId = 5 });

            Subject.Update(_file);

            Mocker.GetMock<IMediaFileRepository>().Verify(r => r.Update(It.Is<MovieFile>(f => f.IsUnmanaged && f.ManagementProfileId == 5)), Times.Once());
        }

        [Test]
        public void explicit_monitor_choice_should_override_global_unmonitor_setting_and_publish_owned_identity()
        {
            Mocker.GetMock<IConfigService>().SetupGet(v => v.AutoUnmonitorPreviouslyDownloadedMovies).Returns(true);

            Subject.DeleteManagedVersionFile(_file, 7, false);

            Mocker.GetMock<IMediaFileRepository>().Verify(v => v.DeleteManagedVersionFile(_file, 7, false), Times.Once());
            Mocker.GetMock<IMediaFileRepository>().Verify(v => v.DeleteForVersion(It.IsAny<MovieFile>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.Is<MovieFileDeletedEvent>(e => e.MovieFile.MovieVersionId == 7 && e.Reason == DeleteMediaFileReason.Manual)), Times.Once());
        }

        [Test]
        public void failed_ownership_check_should_not_publish_deletion_events()
        {
            Mocker.GetMock<IMediaFileRepository>().Setup(v => v.DeleteManagedVersionFile(_file, 7, true)).Throws(new InvalidOperationException("Stale file"));

            Assert.Throws<InvalidOperationException>(() => Subject.DeleteManagedVersionFile(_file, 7, true));

            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<MovieFileDeletedEvent>()), Times.Never());
        }

        [Test]
        public void unfinished_deletion_should_block_metadata_update_before_repository_mutation()
        {
            Mocker.GetMock<IVersionFileDeletionRepository>().Setup(v => v.CheckReady(1, null)).Throws(new VersionFileDeletionPendingException());

            Assert.Throws<VersionFileDeletionPendingException>(() => Subject.Update(_file));

            Mocker.GetMock<IMediaFileRepository>().Verify(v => v.Update(It.IsAny<MovieFile>()), Times.Never());
        }
    }
}
