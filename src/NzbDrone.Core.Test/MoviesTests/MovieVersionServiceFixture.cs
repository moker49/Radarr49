using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MoviesTests
{
    [TestFixture]
    public class MovieVersionServiceFixture : CoreTest<MovieVersionService>
    {
        private Movie _movie;
        private List<CompositeProfileVersion> _definitions;
        private List<MovieVersion> _versions;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 1, QualityProfileId = 10, MovieFileId = 50, Monitored = true };
            _definitions = new List<CompositeProfileVersion>();
            _versions = new List<MovieVersion>();

            Mocker.GetMock<ICompositeProfileVersionRepository>()
                .Setup(s => s.ForProfile(It.IsAny<int>()))
                .Returns((int id) => _definitions.Where(v => v.CompositeProfileId == id).ToList());
            Mocker.GetMock<IMovieVersionRepository>()
                .Setup(s => s.ForMovie(_movie.Id)).Returns(_versions);
            Mocker.GetMock<IQualityProfileService>()
                .Setup(s => s.Get(It.IsAny<int>()))
                .Returns((int id) => new QualityProfile { Id = id });
        }

        private void WithComposite()
        {
            // Deliberately store definitions out of priority order.
            _definitions.Add(new CompositeProfileVersion
            {
                Id = 102, CompositeProfileId = 10, QualityProfileId = 12, Label = "HDR", Priority = 1
            });
            _definitions.Add(new CompositeProfileVersion
            {
                Id = 101, CompositeProfileId = 10, QualityProfileId = 11, Label = "SDR", Priority = 0, IsPrimary = true
            });
            _versions.Add(new MovieVersion
            {
                Id = 201, MovieId = 1, CompositeProfileVersionId = 101, MovieFileId = 50, Monitored = true
            });
            _versions.Add(new MovieVersion
            {
                Id = 202, MovieId = 1, CompositeProfileVersionId = 102, Monitored = true
            });
        }

        [Test]
        public void ordinary_movie_should_resolve_without_creating_assignments()
        {
            var version = Subject.Resolve(_movie).Single();

            Assert.That(version.Id, Is.Null);
            Assert.That(version.CompositeProfileVersionId, Is.Null);
            Assert.That(version.MovieFileId, Is.EqualTo(50));
            Assert.That(version.QualityProfile.Id, Is.EqualTo(10));
            Assert.That(version.IsPrimary, Is.True);
            Mocker.GetMock<IMovieVersionRepository>().VerifyNoOtherCalls();
        }

        [Test]
        public void ordinary_movie_without_a_file_should_have_no_file_assignment()
        {
            _movie.MovieFileId = 0;

            Assert.That(Subject.Resolve(_movie).Single().MovieFileId, Is.Null);
        }

        [Test]
        public void composite_should_resolve_in_priority_order_with_independent_profiles_and_files()
        {
            WithComposite();

            var versions = Subject.Resolve(_movie);

            Assert.That(versions.Select(v => v.Label), Is.EqualTo(new[] { "SDR", "HDR" }));
            Assert.That(versions.Select(v => v.QualityProfile.Id), Is.EqualTo(new[] { 11, 12 }));
            Assert.That(versions.Select(v => v.Id), Is.EqualTo(new int?[] { 201, 202 }));
            Assert.That(versions[0].MovieFileId, Is.EqualTo(50));
            Assert.That(versions[1].MovieFileId, Is.Null);
        }

        [Test]
        public void primary_version_should_be_independent_of_priority()
        {
            WithComposite();
            _definitions[0].IsPrimary = true;
            _definitions[1].IsPrimary = false;

            Assert.That(Subject.Resolve(_movie).Single(v => v.IsPrimary).Label, Is.EqualTo("HDR"));
        }

        [Test]
        public void movie_monitoring_should_gate_all_versions()
        {
            WithComposite();
            _movie.Monitored = false;

            Assert.That(Subject.Resolve(_movie).All(v => !v.Monitored), Is.True);
        }

        [Test]
        public void individual_version_monitoring_should_be_preserved()
        {
            WithComposite();
            _versions[0].Monitored = false;

            Assert.That(Subject.Resolve(_movie).Select(v => v.Monitored), Is.EqualTo(new[] { false, true }));
        }

        [Test]
        public void missing_assignment_should_not_guess_from_legacy_file()
        {
            WithComposite();
            _versions.RemoveAt(0);

            Assert.Throws<InvalidOperationException>(() => Subject.Resolve(_movie));
        }

        [Test]
        public void same_file_cannot_satisfy_both_versions()
        {
            WithComposite();
            _versions[1].MovieFileId = 50;

            Assert.Throws<InvalidOperationException>(() => Subject.Resolve(_movie));
        }

        [Test]
        public void labels_should_be_unique_ignoring_case_and_whitespace()
        {
            WithComposite();
            _definitions[0].Label = " sdr ";

            Assert.Throws<InvalidOperationException>(() => Subject.Resolve(_movie));
        }

        [Test]
        public void nested_composites_should_be_rejected()
        {
            WithComposite();
            _definitions.Add(new CompositeProfileVersion { Id = 103, CompositeProfileId = 11 });

            Assert.Throws<InvalidOperationException>(() => Subject.Resolve(_movie));
        }

        [Test]
        public void composite_should_require_exactly_one_primary_version()
        {
            WithComposite();
            _definitions[0].IsPrimary = true;

            Assert.Throws<InvalidOperationException>(() => Subject.Resolve(_movie));
        }
    }
}
