using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jularr.Tests;

/// <summary>
/// The canonical Work identity moved from a GUID to a BIGINT identity: every Work keeps its identities, structure, requests, monitoring, wanted rows and
/// links through the migration, the Work itself is a target without a node id, and a new Work continues the numbering.
/// </summary>
[TestClass]
public sealed class WorkIdMigrationTests
{
    private const string PreviousMigration = "20261009170000_WorkAnimeClassification";

    [TestMethod]
    public async Task EveryReferenceToAWorkFollowsItToItsNumberAndNothingIsLost()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source=work-id-{Guid.NewGuid():N}.db;Foreign Keys=True").Options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var movie = Guid.NewGuid();
        var series = Guid.NewGuid();
        var season = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var request = Guid.NewGuid().ToString();
        var operation = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Works" ("Id", "MediaType", "CanonicalTitle", "Year", "CreatedAt", "UpdatedAt") VALUES ({movie}, 0, 'Moon', 2009, {now}, {now}), ({series}, 1, 'Harbor', 2024, {now.AddSeconds(1)}, {now})""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WorkExternalIdentities" ("Id", "WorkId", "MediaType", "Provider", "ExternalId", "IsPrimary", "Confidence", "Evidence", "IsManualOverride", "ReviewState", "CreatedAt", "UpdatedAt") VALUES ({Guid.NewGuid()}, {movie}, 0, 'tmdb', '17431', TRUE, 1, 'test', FALSE, 1, {now}, {now}), ({Guid.NewGuid()}, {series}, 1, 'tmdb', '99', TRUE, 1, 'test', FALSE, 1, {now}, {now})""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WorkSeasons" ("Id", "WorkId", "SeasonNumber", "IsSpecial", "CreatedAt") VALUES ({season}, {series}, 1, FALSE, {now})""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WorkEpisodes" ("Id", "WorkId", "SeasonId", "SeasonNumber", "EpisodeNumber", "IsSpecial", "AiredAt", "CreatedAt") VALUES ({episode}, {series}, {season}, 1, 1, FALSE, {now.AddDays(-5)}, {now})""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt") VALUES ({movie}, 0, {movie}, TRUE, now()), ({series}, 0, {series}, TRUE, now()), ({series}, 2, {episode}, FALSE, now())""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WantedItems" ("WorkId", "TargetKind", "TargetId", "CreatedAt") VALUES ({movie}, 0, {movie}, now()), ({series}, 1, {episode}, now())""");
        var payloadJson = $$"""{"workId":"{{movie}}","title":"Moon","year":2009,"activeWorkEpisodeId":"{{episode}}"}""";
        var resultUrl = $"/Library/Movie/{movie}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "AcquisitionRequests" ("Id", "Kind", "Provider", "ExternalId", "Title", "PayloadJson", "ResultUrl", "RequestedByProfileId", "Status", "CreatedAt", "UpdatedAt", "WorkId")
            VALUES ({request}, 'movie', 'tmdb', '17431', 'Moon', {payloadJson}, {resultUrl}, 'owner', 'approved', {now.ToString("o")}, {now.ToString("o")}, {movie.ToString()})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt") VALUES ({request}, {movie}, 0, {movie}, now())""");
        var details = $$"""{"mediaKind":"movie","targetKey":"work:{{movie}}"}""";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Operations" ("Id", "Kind", "Category", "Lane", "Status", "Title", "CreatedAtUtc", "UpdatedAtUtc", "Details") VALUES ({operation}, 'video-usenet-download', 'x', 1, 1, 'Moon', {now.ToString("o")}, {now.ToString("o")}, {details})""");
        var movieScope = "work:" + movie.ToString("N");
        var otherScope = "work:" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "ReaderPreferences" ("Id", "ProfileId", "ScopeKey", "UpdatedAt") VALUES ({Guid.NewGuid()}, 'reader', {movieScope}, now()), ({Guid.NewGuid()}, 'reader', {otherScope}, now())""");
        var foreignKeysBefore = await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE contype = 'f' AND confrelid = '"Works"'::regclass""").SingleAsync();

        await db.Database.MigrateAsync();

        var movieNumber = await WorkNumberMap.OfAsync(db, movie);
        var seriesNumber = await WorkNumberMap.OfAsync(db, series);
        CollectionAssert.AreEqual(new[] { 1L, 2L }, new[] { movieNumber, seriesNumber }, "The numbers follow the creation order.");
        CollectionAssert.AreEquivalent(new[] { movieNumber, seriesNumber }, await db.Works.AsNoTracking().Select(work => work.Id).ToArrayAsync());
        Assert.AreEqual(foreignKeysBefore, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE contype = 'f' AND confrelid = '"Works"'::regclass""").SingleAsync(), "Every foreign key to Works is back.");

        Assert.AreEqual(movieNumber, (await new WorkService(db).EnsureWorkByExternalIdentityAsync(WorkMediaType.Movie, "tmdb", "17431", "Moon", 2009, CancellationToken.None)).Id, "The provider identity still resolves to the same Work, with no duplicate.");
        Assert.AreEqual(2, await db.Works.CountAsync());
        Assert.AreEqual(seriesNumber, await db.WorkEpisodes.AsNoTracking().Where(item => item.Id == episode).Select(item => item.WorkId).SingleAsync());
        Assert.AreEqual(season, await db.WorkEpisodes.AsNoTracking().Where(item => item.Id == episode).Select(item => item.SeasonId!.Value).SingleAsync(), "Nodes below the Work keep their own ids.");

        var monitoring = await db.Database.SqlQuery<MonitoringRow>($"""SELECT "WorkId", "Kind", "TargetId" FROM "WorkMonitoring" ORDER BY "WorkId", "Kind" """).ToListAsync();
        CollectionAssert.AreEqual(
            new[] { (movieNumber, (short)0, (Guid?)null), (seriesNumber, (short)0, null), (seriesNumber, (short)2, episode) },
            monitoring.Select(row => (row.WorkId, row.Kind, row.TargetId)).ToArray(),
            "A Work decision names its Work and no node.");
        var wanted = await db.WantedItems.AsNoTracking().OrderBy(item => item.WorkId).ToListAsync();
        CollectionAssert.AreEqual(new[] { (movieNumber, WantedTargetKind.Work, (Guid?)null), (seriesNumber, WantedTargetKind.Episode, episode) }, wanted.Select(item => (item.WorkId, item.TargetKind, item.TargetId)).ToArray());
        Assert.AreEqual(0, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "RequestTargets" WHERE "TargetKind" = 0 AND "TargetId" IS NOT NULL""").SingleAsync());

        var stored = await new AcquisitionAccessStore(db).GetAsync(Guid.Parse(request), CancellationToken.None);
        Assert.AreEqual(movieNumber, stored!.WorkId, "The request stays bound to its Work.");
        Assert.AreEqual($"/Library/Movie/{movieNumber}", stored.ResultUrl);
        var payload = VideoRequestPayload.Parse(stored.PayloadJson)!;
        Assert.AreEqual((movieNumber, episode), (payload.WorkId, payload.ActiveWorkEpisodeId), "The payload names the Work by its number and keeps the episode GUID.");
        Assert.AreEqual($$"""{"mediaKind":"movie","targetKey":"work:{{movieNumber}}"}""", await db.Database.SqlQuery<string>($"""SELECT "Details" AS "Value" FROM "Operations" WHERE "Id" = {operation}""").SingleAsync(), "An operation in flight keeps owning its Work.");
        var migratedScope = $"work:{movieNumber}";
        Assert.AreEqual(1, await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "ReaderPreferences" WHERE "ScopeKey" = {migratedScope}""").SingleAsync(), "The reader scope of a Work follows its number; another record's scope is untouched.");

        var added = new Work { MediaType = WorkMediaType.Movie, CanonicalTitle = "New" };
        db.Works.Add(added);
        await db.SaveChangesAsync();
        Assert.AreEqual(3L, added.Id, "A new Work continues the numbering.");
    }

    [TestMethod]
    public async Task ThePerWorkQualityProfileAssignmentsOfTheOldGuidsAreConvertedOnce()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"work-id-profiles-{Guid.NewGuid():N}"));
        try
        {
            var registry = new MediaAcquisitionRegistry([new MovieAcquisitionRegistration()]);
            var profile = registry.DefaultProfileFor(MediaAcquisitionKind.Movie) with { Id = "strict", Name = "Strict" };
            var store = new QualityProfileStore(directory, registry);
            await store.UpsertAsync(profile);
            var oldWork = Guid.NewGuid();
            var anime = Guid.NewGuid();
            await store.AssignAnimeAsync(oldWork, "strict");
            await store.AssignAnimeAsync(anime, "strict");

            var converted = await store.ConvertWorkAssignmentsAsync(new Dictionary<Guid, long> { [oldWork] = 7 });

            Assert.AreEqual(1, converted);
            Assert.AreEqual("strict", (await store.ResolveAsync(MediaAcquisitionKind.Movie, 7L)).Id, "The assignment follows the Work number.");
            Assert.AreEqual("strict", (await store.ResolveAsync(anime)).Id, "An Anime record keeps its GUID key.");
            Assert.AreEqual(0, await store.ConvertWorkAssignmentsAsync(new Dictionary<Guid, long> { [oldWork] = 7 }), "A second pass has nothing to convert.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed record MonitoringRow(long WorkId, short Kind, Guid? TargetId);
}
