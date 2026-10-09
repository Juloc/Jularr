using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class AnimeClassificationTests
{
    private static async Task<InstanceModuleStore> ModulesAsync(bool animeEnabled)
    {
        var store = new InstanceModuleStore(Directory.CreateTempSubdirectory("jularr-modules-").FullName);
        await store.SetAsync(InstanceModule.Anime, animeEnabled);
        return store;
    }

    [TestMethod]
    public async Task AnAnimeClassificationNeverChangesTheTechnicalTypeAndAnOwnerDecisionSurvivesTheProvider()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var movie = await works.CreateWorkAsync(WorkMediaType.Movie, "Your Name", 2016, CancellationToken.None);

        Assert.IsTrue(await works.SetAnimeClassificationAsync(movie.Id, true, "anilist", "21519", isManualOverride: false, CancellationToken.None));
        Assert.IsTrue(await works.SetAnimeClassificationAsync(movie.Id, false, "owner", null, isManualOverride: true, CancellationToken.None));
        Assert.IsFalse(await works.SetAnimeClassificationAsync(movie.Id, true, "anilist", "21519", isManualOverride: false, CancellationToken.None), "A provider mapping never overrides the owner.");

        var stored = await db.Works.AsNoTracking().SingleAsync(work => work.Id == movie.Id);
        Assert.AreEqual((WorkMediaType.Movie, false), (stored.MediaType, stored.IsAnime));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await works.SetAnimeClassificationAsync((await works.CreateWorkAsync(WorkMediaType.Book, "B", null, CancellationToken.None)).Id, true, "owner", null, true, CancellationToken.None));
    }

    [TestMethod]
    public async Task AnAnimeMovieAppearsUnderAnimeWhenEnabledAndUnderMoviesWithTheSameWorkIdWhenNot()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var movie = await works.CreateWorkAsync(WorkMediaType.Movie, "Your Name", 2016, CancellationToken.None);
        await works.SetAnimeClassificationAsync(movie.Id, true, "owner", null, isManualOverride: true, CancellationToken.None);
        db.WorkSourceLinks.Add(new WorkSourceLink { WorkId = movie.Id, SourceKind = WorkSourceKind.Movie, SourceId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var query = new LibraryMediaCardQuery(db);

        var enabled = await query.GetEntriesAsync("reader", [WorkMediaType.Anime, WorkMediaType.Movie, WorkMediaType.Series], CancellationToken.None);
        var disabled = await query.GetEntriesAsync("reader", [WorkMediaType.Movie, WorkMediaType.Series], CancellationToken.None);

        var shown = enabled.Entries.Single();
        Assert.AreEqual((movie.Id, WorkMediaType.Anime), (shown.WorkId, shown.MediaType), "One entry, in the Anime section.");
        StringAssert.StartsWith(shown.Card.Href, "/Library/Movie/", "It is movie-shaped: no artificial series, no legacy Anime record.");
        Assert.AreEqual((movie.Id, WorkMediaType.Movie), (disabled.Entries.Single().WorkId, disabled.Entries.Single().MediaType));
    }

    [TestMethod]
    public async Task AClassifiedSeriesHasOneRequestOwnerAndTheSeriesKindTakesItOverWhenAnimeIsOff()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var series = await works.CreateWorkAsync(WorkMediaType.Series, "Frieren", 2023, CancellationToken.None);
        await works.SetAnimeClassificationAsync(series.Id, true, "anilist", "154587", isManualOverride: false, CancellationToken.None);
        db.WorkSourceLinks.Add(new WorkSourceLink { WorkId = series.Id, SourceKind = WorkSourceKind.Anime, SourceId = Guid.NewGuid() });
        db.WorkEpisodes.Add(new WorkEpisode { WorkId = series.Id, SeasonNumber = 1, EpisodeNumber = 1 });
        await db.SaveChangesAsync();
        await MonitoringTestSupport.Commands(db).SetAsync(MonitoringTargetKind.Work, series.Id, true, CancellationToken.None);

        var on = new WantedReconciler(db, TimeProvider.System, modules: await ModulesAsync(true));
        await on.ReconcileAsync(series.Id, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { series.Id }, (await on.WorksWithoutOpenRequestAsync(MediaAcquisitionKind.Anime, Guid.Empty, 10, CancellationToken.None)).ToArray());
        Assert.IsEmpty(await on.WorksWithoutOpenRequestAsync(MediaAcquisitionKind.Tv, Guid.Empty, 10, CancellationToken.None), "The Series kind leaves it to Anime while Anime runs.");

        var off = new WantedReconciler(db, TimeProvider.System, modules: await ModulesAsync(false));
        CollectionAssert.AreEqual(new[] { series.Id }, (await off.WorksWithoutOpenRequestAsync(MediaAcquisitionKind.Tv, Guid.Empty, 10, CancellationToken.None)).ToArray(), "With Anime off the same Work is carried as a Series; its wanted episodes were kept.");
    }

    [TestMethod]
    public async Task TheOldAnimeWorksBecomeClassifiedSeriesWithTheirIdsAndIdentities()
    {
        await using var db = new Jularr.Web.Data.AppDbContext(new DbContextOptionsBuilder<Jularr.Web.Data.AppDbContext>().UseSqlite($"Data Source=anime-classification-{Guid.NewGuid():N}.db;Foreign Keys=True").Options);
        await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).MigrateAsync("20261009160000_AudiobookEditionMetadata");
        var work = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Works" ("Id", "MediaType", "CanonicalTitle", "CreatedAt", "UpdatedAt") VALUES ({work}, 2, 'Frieren', {now}, {now})""");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "WorkExternalIdentities" ("Id", "WorkId", "MediaType", "Provider", "ExternalId", "IsPrimary", "Confidence", "Evidence", "IsManualOverride", "ReviewState", "CreatedAt", "UpdatedAt") VALUES ({Guid.NewGuid()}, {work}, 2, 'anilist', '154587', TRUE, 1, 'test', FALSE, 1, {now}, {now})""");

        await db.Database.MigrateAsync();

        var row = await db.Works.AsNoTracking().SingleAsync(item => item.Id == work);
        Assert.AreEqual((WorkMediaType.Series, true), (row.MediaType, row.IsAnime));
        Assert.AreEqual(1, await db.WorkExternalIdentities.CountAsync(identity => identity.WorkId == work && identity.Provider == "anilist"), "The AniList identity keeps its own namespace.");
        Assert.IsTrue(await db.WorkFieldProvenance.AnyAsync(item => item.WorkId == work && item.FieldKey == "classification.anime"));
    }
}
