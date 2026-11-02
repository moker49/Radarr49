using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class CleanupDuplicateMetadataFilesFixture : DbTest<CleanupDuplicateMetadataFiles, MetadataFile>
    {
        [Test]
        public void should_not_delete_metadata_files_when_they_are_for_the_same_movie_but_different_consumers()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieMetadata)
                                             .With(m => m.MovieId = 1)
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(files.Count);
        }

        [TestCase(null)]
        [TestCase(12)]
        public void should_remove_all_true_duplicates_and_preserve_other_version_paths(int? movieFileId)
        {
            var duplicates = Builder<MetadataFile>.CreateListOfSize(3).All()
                .With(f => f.MovieId = 1)
                .With(f => f.MovieFileId = movieFileId)
                .With(f => f.Type = MetadataType.MovieMetadata)
                .With(f => f.Consumer = "XbmcMetadata")
                .With(f => f.RelativePath = "SDR.nfo").BuildListOfNew();
            Db.InsertMany(duplicates);
            var hdr = new MetadataFile { MovieId = 1, MovieFileId = 13, Type = MetadataType.MovieMetadata, Consumer = "XbmcMetadata", RelativePath = "HDR.nfo", Extension = ".nfo" };
            Db.Insert(hdr);

            Subject.Clean();

            AllStoredModels.Should().HaveCount(2);
            AllStoredModels.Should().Contain(f => f.Id == hdr.Id);
            AllStoredModels.Should().Contain(f => f.Id == duplicates[2].Id);
        }

        [Test]
        public void should_not_delete_metadata_files_for_different_movie()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieMetadata)
                                             .With(m => m.Consumer = "XbmcMetadata")
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(files.Count);
        }

        [Test]
        public void should_preserve_different_version_metadata_for_the_same_movie_and_consumer()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieMetadata)
                                             .With(m => m.MovieId = 1)
                                             .With(m => m.Consumer = "XbmcMetadata")
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(2);
        }

        [Test]
        public void should_not_delete_metadata_files_when_there_is_only_one_for_that_movie_and_consumer()
        {
            var file = Builder<MetadataFile>.CreateNew()
                                         .BuildNew();

            Db.Insert(file);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(1);
        }

        [Test]
        public void should_not_delete_metadata_files_when_they_are_for_the_same_movie_file_but_different_consumers()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieMetadata)
                                             .With(m => m.MovieFileId = 1)
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(files.Count);
        }

        [Test]
        public void should_not_delete_metadata_files_for_different_movie_file()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieMetadata)
                                             .With(m => m.Consumer = "XbmcMetadata")
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(files.Count);
        }

        [Test]
        public void should_preserve_different_paths_for_the_same_movie_file_and_consumer()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieMetadata)
                                             .With(m => m.MovieFileId = 1)
                                             .With(m => m.Consumer = "XbmcMetadata")
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(2);
        }

        [Test]
        public void should_not_delete_metadata_files_when_there_is_only_one_for_that_movie_file_and_consumer()
        {
            var file = Builder<MetadataFile>.CreateNew()
                                            .BuildNew();

            Db.Insert(file);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(1);
        }

        [Test]
        public void should_not_delete_image_when_they_are_for_the_same_movie_file_but_different_consumers()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieImage)
                                             .With(m => m.MovieFileId = 1)
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(files.Count);
        }

        [Test]
        public void should_not_delete_image_for_different_movie_file()
        {
            var files = Builder<MetadataFile>.CreateListOfSize(2)
                                             .All()
                                             .With(m => m.Type = MetadataType.MovieImage)
                                             .With(m => m.Consumer = "XbmcMetadata")
                                             .BuildListOfNew();

            Db.InsertMany(files);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(files.Count);
        }

        [Test]
        public void should_not_delete_image_when_there_is_only_one_for_that_movie_file_and_consumer()
        {
            var file = Builder<MetadataFile>.CreateNew()
                                            .BuildNew();

            Db.Insert(file);
            Subject.Clean();
            AllStoredModels.Count.Should().Be(1);
        }
    }
}
