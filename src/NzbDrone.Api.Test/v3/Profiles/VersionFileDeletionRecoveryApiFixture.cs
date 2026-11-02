using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class VersionFileDeletionRecoveryApiFixture : TestBase<VersionFileDeletionsController>
    {
        [Test]
        public void inspection_should_expose_checkpoint_and_original_source_attributes()
        {
            Mocker.GetMock<IVersionFileDeletionRepository>().Setup(v => v.ForMovie(1)).Returns(new List<VersionFileDeletion>
            {
                new VersionFileDeletion
                {
                    Id = 42, MovieId = 1, MovieVersionId = 7, MovieFileId = 11, State = VersionFileDeletionState.Recycling,
                    Snapshot = new VersionFileDeletionSnapshot { File = new MovieFile { RelativePath = "SDR.mkv" }, SourceSize = 100 }
                }
            });

            var resource = Subject.Get(1).Single();

            Assert.That(resource.MovieVersionId, Is.EqualTo(7));
            Assert.That(resource.MovieFileId, Is.EqualTo(11));
            Assert.That(resource.State, Is.EqualTo(VersionFileDeletionState.Recycling));
            Assert.That(resource.IsActive, Is.True);
            Assert.That(resource.SourceSize, Is.EqualTo(100));
        }

        [Test]
        public void normal_resume_should_not_authorize_an_ambiguous_physical_retry()
        {
            Subject.Resume(1, 42, new VersionFileDeletionResumeResource());
            Mocker.GetMock<IVersionFileDeletionRecoveryService>().Verify(v => v.Resume(1, 42, false, false, null), Times.Once());
        }

        [Test]
        public void verified_retry_and_preservation_choices_should_be_forwarded_exactly()
        {
            Subject.Resume(1, 42, new VersionFileDeletionResumeResource { RetryOriginalSource = true, PreserveChangedExtras = true, Note = "Verified source and edited subtitle" });
            Mocker.GetMock<IVersionFileDeletionRecoveryService>().Verify(v => v.Resume(1, 42, true, true, "Verified source and edited subtitle"), Times.Once());
        }

        [Test]
        public void cancellation_should_forward_ownership_and_verification_note()
        {
            Subject.Cancel(1, 42, new VersionFileDeletionResumeResource { Note = "Cancel unstarted request" });
            Mocker.GetMock<IVersionFileDeletionRecoveryService>().Verify(v => v.Cancel(1, 42, "Cancel unstarted request"), Times.Once());
        }
    }
}
