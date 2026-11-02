using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class MovieVersionsApiFixture : TestBase<MovieVersionsController>
    {
        [Test]
        public void should_return_an_ordinary_movies_implicit_version_without_creating_rows()
        {
            var movie = new Movie { Id = 1 };
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(1)).Returns(movie);
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.Resolve(movie)).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { MovieId = 1, QualityProfile = new QualityProfile { Id = 5 }, IsPrimary = true }
            });

            var version = Subject.Get(1).Single();

            Assert.That(version.IsImplicit, Is.True);
            Assert.That(version.QualityProfileId, Is.EqualTo(5));
        }

        [Test]
        public void should_expose_explicit_version_ids_and_independent_profiles()
        {
            var movie = new Movie { Id = 1 };
            Mocker.GetMock<IMovieService>().Setup(s => s.GetMovie(1)).Returns(movie);
            Mocker.GetMock<IMovieVersionService>().Setup(s => s.Resolve(movie)).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 201, MovieId = 1, CompositeProfileVersionId = 101, Label = "SDR", QualityProfile = new QualityProfile { Id = 5 }, IsPrimary = true },
                new ResolvedMovieVersion { Id = 202, MovieId = 1, CompositeProfileVersionId = 102, Label = "HDR", QualityProfile = new QualityProfile { Id = 6 }, Priority = 1 }
            });

            var versions = Subject.Get(1);

            Assert.That(versions.Select(v => v.Id), Is.EqualTo(new int?[] { 201, 202 }));
            Assert.That(versions.Select(v => v.QualityProfileId), Is.EqualTo(new[] { 5, 6 }));
            Assert.That(versions.All(v => !v.IsImplicit), Is.True);
        }
    }
}
