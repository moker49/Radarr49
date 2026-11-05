using System;
using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class UnmanagedFileDateFixture : CoreTest<UpdateMovieFileService>
    {
        [Test]
        public void single_file_audit_scanning_must_not_rewrite_retained_file_dates()
        {
            var movie = new Movie { Id = 1, Path = "C:/Movies/Movie", MovieMetadata = new MovieMetadata { InCinemas = new DateTime(2020, 1, 1) } };
            var files = new List<MovieFile>
            {
                new MovieFile { Id = 1, MovieId = 1, RelativePath = "Managed.mkv" },
                new MovieFile { Id = 2, MovieId = 1, RelativePath = "Retained.mkv", IsUnmanaged = true }
            };
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(movie.Id)).Returns(files);
            Mocker.GetMock<IConfigService>().Setup(s => s.FileDate).Returns(FileDateType.Cinemas);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileGetLastWrite(It.IsAny<string>())).Returns(new DateTime(2021, 1, 1));

            Subject.Handle(new MovieScannedEvent(movie, new List<string>()));

            Mocker.GetMock<IDiskProvider>().Verify(s => s.FileSetLastWriteTime(It.Is<string>(p => p.EndsWith("Retained.mkv")), It.IsAny<DateTime>()), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(s => s.FileSetLastWriteTime(It.Is<string>(p => p.EndsWith("Managed.mkv")), new DateTime(2020, 1, 1)), Times.Once());
        }
    }
}
