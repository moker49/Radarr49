using System.Collections.Generic;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles
{
    [TestFixture]
    public class CompositeProfileEditorRepositoryFixture : DbTest<CompositeProfileEditorRepository, QualityProfile>
    {
        private QualityProfile _sdr;
        private QualityProfile _hdr;

        private static QualityProfile Profile(string name)
        {
            return new QualityProfile { Name = name, Items = new List<QualityProfileQualityItem>() };
        }

        [SetUp]
        public void Setup()
        {
            _sdr = Db.Insert(Profile("SDR"));
            _hdr = Db.Insert(Profile("HDR"));
        }

        private List<CompositeProfileVersion> Definitions()
        {
            return new List<CompositeProfileVersion>
            {
                new CompositeProfileVersion { QualityProfileId = _sdr.Id, Label = "SDR", Priority = 0, IsPrimary = true },
                new CompositeProfileVersion { QualityProfileId = _hdr.Id, Label = "HDR", Priority = 1 }
            };
        }

        private List<CompositeProfileVersion> Read(int id)
        {
            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            return connection.Query<CompositeProfileVersion>("SELECT * FROM \"CompositeProfileVersions\" WHERE \"CompositeProfileId\"=@id ORDER BY \"Priority\"", new { id }).ToList();
        }

        [Test]
        public void should_create_parent_and_definitions_together()
        {
            var id = Subject.Save(Profile("SDR+HDR"), true, Definitions(), null);

            Assert.That(Db.All<QualityProfile>().Single(p => p.Id == id).Name, Is.EqualTo("SDR+HDR"));
            Assert.That(Read(id).Select(v => v.QualityProfileId), Is.EqualTo(new[] { _sdr.Id, _hdr.Id }));
        }

        [Test]
        public void invalid_child_should_leave_no_partial_parent()
        {
            var versions = Definitions();
            versions[1].QualityProfileId = 9999;

            Assert.Throws<NzbDroneClientException>(() => Subject.Save(Profile("Rejected parent"), true, versions, null));

            Assert.That(Db.All<QualityProfile>().Count, Is.EqualTo(2));
        }

        [Test]
        public void failure_after_parent_and_first_version_insert_should_roll_back_everything()
        {
            if (Db.DatabaseType != DatabaseType.SQLite)
            {
                Assert.Ignore("SQLite fault injection.");
            }

            using var connection = Mocker.Resolve<IMainDatabase>().OpenConnection();
            connection.Execute("CREATE TRIGGER RejectSecondVersion BEFORE INSERT ON \"CompositeProfileVersions\" WHEN NEW.\"Label\"='HDR' BEGIN SELECT RAISE(ABORT, 'Injected second-version failure'); END");

            Assert.That(() => Subject.Save(Profile("Rollback parent"), true, Definitions(), null), Throws.Exception);

            Assert.That(Db.All<QualityProfile>().Count, Is.EqualTo(2));
            Assert.That(Db.All<CompositeProfileVersion>(), Is.Empty);
        }

        [Test]
        public void assigned_composite_reordering_should_preserve_primary_and_file_assignments()
        {
            var parent = Profile("SDR+HDR");
            Subject.Save(parent, true, Definitions(), null);
            var definitions = Read(parent.Id);
            var movie = Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = parent.Id).Build();
            Db.Insert(movie);
            var assignment = Db.Insert(new MovieVersion { MovieId = movie.Id, CompositeProfileVersionId = definitions[0].Id, MovieFileId = 88, Monitored = true });
            var originalIds = definitions.Select(v => v.Id).ToArray();
            definitions[0].Priority = 1;
            definitions[1].Priority = 0;

            Subject.Save(parent, true, definitions, null);

            Assert.That(Read(parent.Id).Select(v => v.Id), Is.EqualTo(originalIds.Reverse()));
            Assert.That(Read(parent.Id).Single(v => v.IsPrimary).QualityProfileId, Is.EqualTo(_sdr.Id));
            Assert.That(Db.All<MovieVersion>().Single(v => v.Id == assignment.Id).MovieFileId, Is.EqualTo(88));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void assigned_profile_must_reject_disabling_or_rebinding_without_partial_name_change(bool rebind)
        {
            var parent = Profile("Original name");
            Subject.Save(parent, true, Definitions(), null);
            var definitions = Read(parent.Id);
            Db.Insert(Builder<Movie>.CreateNew().With(m => m.Id = 0).With(m => m.QualityProfileId = parent.Id).Build());
            parent.Name = "Must roll back";
            if (rebind)
            {
                definitions[0].Label = "Changed label";
            }

            Assert.Throws<NzbDroneClientException>(() => Subject.Save(parent, rebind, definitions, null));

            Assert.That(Db.All<QualityProfile>().Single(p => p.Id == parent.Id).Name, Is.EqualTo("Original name"));
            Assert.That(Read(parent.Id).Count, Is.EqualTo(2));
        }

        [Test]
        public void unused_composite_can_return_to_ordinary_profile()
        {
            var parent = Profile("Draft");
            Subject.Save(parent, true, Definitions(), null);

            Subject.Save(parent, false, null, null);

            Assert.That(Read(parent.Id), Is.Empty);
            Assert.That(Db.All<QualityProfile>().Single(p => p.Id == parent.Id).CompositeTestRoot, Is.Null);
        }

        [Test]
        public void referenced_child_cannot_become_composite()
        {
            Subject.Save(Profile("Parent"), true, Definitions(), null);

            Assert.Throws<NzbDroneClientException>(() => Subject.Save(_sdr, true, Definitions(), null));
        }

        [Test]
        public void repeated_definition_ids_must_not_collapse_two_versions_into_one()
        {
            var parent = Profile("Parent");
            Subject.Save(parent, true, Definitions(), null);
            var definitions = Read(parent.Id);
            definitions[1].Id = definitions[0].Id;

            Assert.Throws<NzbDroneClientException>(() => Subject.Save(parent, true, definitions, null));
            Assert.That(Read(parent.Id).Count, Is.EqualTo(2));
        }
    }
}
