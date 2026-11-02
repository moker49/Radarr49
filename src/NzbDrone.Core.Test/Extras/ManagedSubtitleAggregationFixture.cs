using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation.Aggregators;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Extras
{
    [TestFixture]
    public class ManagedSubtitleAggregationFixture : CoreTest<AggregateMovie>
    {
        [Test]
        public void movie_refresh_should_preserve_version_context_before_subtitle_aggregation()
        {
            var root = new Movie { Id = 1, MovieFileId = 11 };
            var hdr = new Movie { Id = 1, MovieFileId = 12, MovieFile = new MovieFile { Id = 12, RelativePath = "HDR.mkv" } };
            var local = new LocalMovie { Movie = root, MovieVersionId = 8 };
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(1)).Returns(root);
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.ForVersion(root, 8)).Returns(hdr);

            Subject.Aggregate(local, null);

            Assert.That(local.Movie, Is.SameAs(hdr));
            Assert.That(local.MovieVersionId, Is.EqualTo(8));
        }
    }
}
