using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class CleanupOrphanedMovieFilesFixture : DbTest<CleanupOrphanedMovieFiles, MovieFile>
    {
        [Test]
        public void single_file_audit_should_keep_retained_copies_but_remove_real_orphans()
        {
            var movie = Db.Insert(Builder<Movie>.CreateNew().BuildNew());
            var files = Builder<MovieFile>.CreateListOfSize(4).All()
                .With(f => f.MovieId = movie.Id)
                .With(f => f.IsUnmanaged = false)
                .With(f => f.Quality = new QualityModel())
                .With(f => f.Languages = new List<Language> { Language.English }).BuildListOfNew();
            files[2].IsUnmanaged = true;
            files[3].IsUnmanaged = true;
            files[3].MovieId = movie.Id + 100;
            Db.InsertMany(files);
            movie.MovieFileId = files[0].Id;
            Db.Update(movie);
            Db.Insert(new MovieVersion { MovieId = movie.Id, CompositeProfileVersionId = 1, MovieFileId = files[1].Id });

            Subject.Clean();

            AllStoredModels.Select(f => f.Id).Should().BeEquivalentTo(files.Take(3).Select(f => f.Id));
        }

        [Test]
        public void should_delete_orphaned_episode_files()
        {
            var movieFile = Builder<MovieFile>.CreateNew()
                                                  .With(h => h.Quality = new QualityModel())
                                                  .With(h => h.Languages = new List<Language> { Language.English })
                                                  .BuildNew();

            Db.Insert(movieFile);
            Subject.Clean();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void should_not_delete_unorphaned_movie_files()
        {
            var movieFiles = Builder<MovieFile>.CreateListOfSize(2)
                                                   .All()
                                                   .With(h => h.IsUnmanaged = false)
                                                   .With(h => h.Quality = new QualityModel())
                                                   .With(h => h.Languages = new List<Language> { Language.English })
                                                   .BuildListOfNew();

            Db.InsertMany(movieFiles);

            var movie = Builder<Movie>.CreateNew()
                                          .With(e => e.MovieFileId = movieFiles.First().Id)
                                          .BuildNew();

            Db.Insert(movie);

            Subject.Clean();
            AllStoredModels.Should().HaveCount(1);
            Db.All<Movie>().Should().Contain(e => e.MovieFileId == AllStoredModels.First().Id);
        }

        [Test]
        public void should_preserve_secondary_version_file_while_removing_unowned_files()
        {
            var movie = Builder<Movie>.CreateNew().BuildNew();
            Db.Insert(movie);
            var files = Builder<MovieFile>.CreateListOfSize(3).All()
                .With(f => f.IsUnmanaged = false)
                .With(f => f.MovieId = movie.Id)
                .With(f => f.Quality = new QualityModel())
                .With(f => f.Languages = new List<Language> { Language.English }).BuildListOfNew();
            Db.InsertMany(files);
            movie.MovieFileId = files[0].Id;
            Db.Update(movie);
            Db.Insert(new MovieVersion { MovieId = movie.Id, CompositeProfileVersionId = 1, MovieFileId = files[0].Id });
            Db.Insert(new MovieVersion { MovieId = movie.Id, CompositeProfileVersionId = 2, MovieFileId = files[1].Id });

            Subject.Clean();

            AllStoredModels.Select(f => f.Id).Should().BeEquivalentTo(new[] { files[0].Id, files[1].Id });
            Db.All<MovieVersion>().Select(v => v.MovieFileId).Should().BeEquivalentTo(new int?[] { files[0].Id, files[1].Id });
        }
    }
}
