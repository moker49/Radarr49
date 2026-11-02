using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles.Qualities
{
    [TestFixture]
    public class CompositeProfileServiceFixture : CoreTest<CompositeProfileService>
    {
        private List<CompositeProfileVersion> _stored;
        private List<Movie> _movies;
        private List<MovieVersion> _versions;

        [SetUp]
        public void Setup()
        {
            _stored = new List<CompositeProfileVersion>();
            _movies = new List<Movie>();
            _versions = new List<MovieVersion>();
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.All()).Returns(_stored);
            Mocker.GetMock<ICompositeProfileVersionRepository>().Setup(s => s.ForProfile(It.IsAny<int>()))
                .Returns((int id) => _stored.Where(v => v.CompositeProfileId == id).ToList());
            Mocker.GetMock<IQualityProfileRepository>().Setup(s => s.Get(It.IsAny<int>())).Returns((int id) => new QualityProfile { Id = id });
            Mocker.GetMock<IQualityProfileRepository>().Setup(s => s.Exists(It.IsAny<int>())).Returns((int id) => id is 1 or 2 or 10);
            Mocker.GetMock<IMovieRepository>().Setup(s => s.All()).Returns(_movies);
            Mocker.GetMock<IMovieVersionRepository>().Setup(s => s.All()).Returns(_versions);
        }

        private static List<CompositeProfileVersion> Definitions()
        {
            return new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { QualityProfileId = 1, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { QualityProfileId = 2, Label = "HDR", Priority = 1 }
            };
        }

        [Test]
        public void should_save_two_ordinary_children_using_route_parent_id()
        {
            var definitions = Definitions();
            definitions[0].CompositeProfileId = 999;

            var result = Subject.Save(10, definitions);

            Assert.That(result.Select(v => v.CompositeProfileId), Is.EqualTo(new[] { 10, 10 }));
            Mocker.GetMock<ICompositeProfileVersionRepository>().Verify(s => s.SaveDefinitions(10, It.IsAny<List<CompositeProfileVersion>>(), 1), Times.Once());
        }

        [Test]
        public void should_preserve_ids_when_reordering_versions()
        {
            _stored.AddRange(Definitions());
            _stored[0].Id = 101;
            _stored[1].Id = 102;
            _stored.ForEach(v => v.CompositeProfileId = 10);
            var updated = Definitions();
            updated[0].Id = 101;
            updated[1].Id = 102;
            updated[0].Priority = 1;
            updated[1].Priority = 0;

            Assert.That(Subject.Save(10, updated).Select(v => v.Id), Is.EqualTo(new[] { 102, 101 }));
        }

        [Test]
        public void should_reject_edit_that_discards_existing_ids()
        {
            _stored.Add(new CompositeProfileVersion { Id = 101, CompositeProfileId = 10 });

            Assert.Throws<ValidationException>(() => Subject.Save(10, Definitions()));
            Mocker.GetMock<ICompositeProfileVersionRepository>().Verify(s => s.SaveDefinitions(It.IsAny<int>(), It.IsAny<List<CompositeProfileVersion>>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_reject_conversion_of_an_assigned_ordinary_profile()
        {
            _movies.Add(new Movie { QualityProfileId = 10 });

            Assert.Throws<NzbDroneClientException>(() => Subject.Save(10, Definitions()));
        }

        [Test]
        public void should_reject_edit_of_a_definition_with_persisted_assignments()
        {
            _stored.Add(new CompositeProfileVersion { Id = 101, CompositeProfileId = 10 });
            _versions.Add(new MovieVersion { CompositeProfileVersionId = 101 });

            Assert.Throws<NzbDroneClientException>(() => Subject.Save(10, Definitions()));
        }

        [Test]
        public void should_reject_nested_composites()
        {
            _stored.Add(new CompositeProfileVersion { Id = 101, CompositeProfileId = 1, QualityProfileId = 2 });

            Assert.Throws<ValidationException>(() => Subject.Save(10, Definitions()));
        }

        [Test]
        public void ordinary_child_of_existing_composite_cannot_become_composite()
        {
            _stored.Add(new CompositeProfileVersion { Id = 101, CompositeProfileId = 20, QualityProfileId = 10 });

            Assert.Throws<ValidationException>(() => Subject.Save(10, Definitions()));
        }

        [Test]
        public void should_reject_nonexistent_child_profile()
        {
            var definitions = Definitions();
            definitions[1].QualityProfileId = 404;

            Assert.Throws<ValidationException>(() => Subject.Save(10, definitions));
        }

        [TestCase("SDR", false)]
        [TestCase("../HDR", false)]
        [TestCase("HDR ", false)]
        [TestCase("HDR.", false)]
        [TestCase("HDR:10", false)]
        [TestCase("HDR", true)]
        public void should_validate_unique_filename_safe_labels(string label, bool valid)
        {
            var definitions = Definitions();
            definitions[1].Label = label;

            Assert.That(new CompositeProfileValidator().Validate(definitions).IsValid, Is.EqualTo(valid));
        }

        [Test]
        public void primary_version_is_independent_of_priority()
        {
            var definitions = Definitions();
            definitions[0].IsPrimary = false;
            definitions[1].IsPrimary = true;

            Assert.That(new CompositeProfileValidator().Validate(definitions).IsValid, Is.True);
        }

        [Test]
        public void should_reject_missing_body_as_validation_error()
        {
            Assert.Throws<ValidationException>(() => Subject.Save(10, null));
        }

        [Test]
        public void unused_draft_definitions_can_be_removed_without_deleting_profile()
        {
            Subject.Delete(10);

            Mocker.GetMock<ICompositeProfileVersionRepository>().Verify(s => s.DeleteDefinitions(10), Times.Once());
            Mocker.GetMock<IQualityProfileRepository>().Verify(s => s.Delete(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void assigned_definitions_cannot_be_removed()
        {
            _movies.Add(new Movie { QualityProfileId = 10 });

            Assert.Throws<NzbDroneClientException>(() => Subject.Delete(10));
            Mocker.GetMock<ICompositeProfileVersionRepository>().Verify(s => s.DeleteDefinitions(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_store_configured_concurrency_with_definitions()
        {
            Subject.Save(10, Definitions(), 2);

            Mocker.GetMock<ICompositeProfileVersionRepository>().Verify(s => s.SaveDefinitions(10, It.IsAny<List<CompositeProfileVersion>>(), 2), Times.Once());
        }

        [TestCase(0)]
        [TestCase(3)]
        public void should_reject_invalid_concurrency(int limit)
        {
            Assert.Throws<ValidationException>(() => Subject.Save(10, Definitions(), limit));
        }
    }
}
