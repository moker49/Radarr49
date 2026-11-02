using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Subtitles;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ExistingManagedSubtitleFixture : CoreTest<ExistingSubtitleImporter>
    {
        [Test]
        public void existing_hdr_subtitle_should_use_hdr_context_and_file_id()
        {
            var root = new Movie { Id = 1, MovieFileId = 11, Path = @"C:\Movies\300 (2006)".AsOsAgnostic() };
            var hdr = new Movie { Id = 1, MovieFileId = 12, ManagedVersion = new ResolvedMovieVersion { Id = 8 } };
            var path = Path.Combine(root.Path, "300 (2006) - HDR.en.srt");
            Mocker.GetMock<IExtraFileService<SubtitleFile>>().Setup(v => v.GetFilesByMovie(1)).Returns(new List<SubtitleFile>());
            Mocker.GetMock<IExtraFileVersionService>().Setup(v => v.Resolve(root, path, false)).Returns(hdr);
            Mocker.GetMock<IParsingService>().Setup(v => v.ParseMinimalPathMovieInfo(path)).Returns(new ParsedMovieInfo());
            Mocker.GetMock<IAggregationService>().Setup(v => v.Augment(It.IsAny<LocalMovie>(), null))
                .Callback<LocalMovie, Core.Download.DownloadClientItem>((local, download) =>
                {
                    Assert.That(local.Movie, Is.SameAs(hdr));
                    Assert.That(local.MovieVersionId, Is.EqualTo(8));
                    local.SubtitleInfo = new SubtitleTitleInfo { Language = Language.English, LanguageTags = new List<string>() };
                });

            var result = Subject.ProcessFiles(root, new List<string> { path }, new List<string>(), null).Single();

            Assert.That(result.MovieFileId, Is.EqualTo(12));
            Assert.That(root.MovieFileId, Is.EqualTo(11));
        }
    }
}
