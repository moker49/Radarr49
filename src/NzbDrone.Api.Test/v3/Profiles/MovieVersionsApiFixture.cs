using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Movies;

namespace NzbDrone.Api.Test.v3.Profiles
{
    [TestFixture]
    public class MovieVersionsApiFixture : TestBase<MovieVersionsController>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IMediaFileService>().Setup(v => v.GetFilesByMovie(It.IsAny<int>())).Returns(new List<MovieFile>());
            Mocker.SetConstant<IMovieVersionStatusService>(Mocker.Resolve<MovieVersionStatusService>());
        }

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

        [Test]
        public void version_file_delete_should_forward_exact_identity_and_default_unmonitor_choice()
        {
            Assert.That(Subject.DeleteFile(1, 7, 11), Is.InstanceOf<NoContentResult>());
            Mocker.GetMock<IMovieVersionFileDeletionService>().Verify(v => v.Delete(1, 7, 11, true), Times.Once());
        }

        [Test]
        public void version_file_delete_should_allow_preserving_monitoring_explicitly()
        {
            Subject.DeleteFile(1, 8, 12, false);
            Mocker.GetMock<IMovieVersionFileDeletionService>().Verify(v => v.Delete(1, 8, 12, false), Times.Once());
        }

        [Test]
        public void status_resource_should_expose_child_file_and_independent_cutoff_state()
        {
            var movie = new Movie { Id = 1 };
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(1)).Returns(movie);
            var statuses = new Mock<IMovieVersionStatusService>();
            statuses.Setup(v => v.Get(movie)).Returns(new List<MovieVersionStatus>
            {
                new MovieVersionStatus { Version = new ResolvedMovieVersion { Id = 201, MovieId = 1, MovieFileId = 11, Label = "SDR", QualityProfile = new QualityProfile { Id = 5 } }, File = new MovieFile { Id = 11, Size = 100, Quality = new QualityModel(Quality.Bluray1080p) }, CustomFormatScore = 25 },
                new MovieVersionStatus { Version = new ResolvedMovieVersion { Id = 202, MovieId = 1, MovieFileId = 12, Label = "HDR", QualityProfile = new QualityProfile { Id = 6 } }, File = new MovieFile { Id = 12, Size = 200, Quality = new QualityModel(Quality.Bluray1080p) }, QualityCutoffNotMet = true, CutoffNotMet = true, CustomFormatScore = 50 }
            });
            Mocker.SetConstant(statuses.Object);
            var resources = Subject.Get(1);
            Assert.That(resources.Select(r => r.HasFile), Is.EqualTo(new[] { true, true }));
            Assert.That(resources.Select(r => r.QualityCutoffNotMet), Is.EqualTo(new[] { false, true }));
            Assert.That(resources.Select(r => r.CustomFormatScore), Is.EqualTo(new[] { 25, 50 }));
            Assert.That(resources.Select(r => r.SizeOnDisk), Is.EqualTo(new long[] { 100, 200 }));
            Assert.That(resources[1].Quality.Quality, Is.EqualTo(Quality.Bluray1080p));
        }

        [Test]
        public void invalid_file_pointer_should_surface_error_without_using_primary_file()
        {
            var movie = new Movie { Id = 1, MovieFileId = 11 };
            Mocker.GetMock<IMovieService>().Setup(v => v.GetMovie(1)).Returns(movie);
            Mocker.GetMock<IMovieVersionService>().Setup(v => v.Resolve(movie)).Returns(new List<ResolvedMovieVersion>
            {
                new ResolvedMovieVersion { Id = 202, MovieId = 1, MovieFileId = 99, QualityProfile = new QualityProfile { Id = 6 } }
            });
            var resource = Subject.Get(1).Single();
            Assert.That(resource.MovieFileId, Is.EqualTo(99));
            Assert.That(resource.HasFile, Is.False);
            Assert.That(resource.Quality, Is.Null);
            Assert.That(resource.Error, Is.Not.Null);
        }
    }
}
