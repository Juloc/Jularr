using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jularr.Tests;

[TestClass]
public sealed class RequestTargetsMigrationTests
{
    private const string PreviousMigration = "20261008190000_RequestTargets";

    private static async Task<AppDbContext> CreateBeforeAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source=request-targets-{Guid.NewGuid():N}.db;Foreign Keys=True").Options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        return db;
    }

    private static async Task<Guid> AddWorkAsync(AppDbContext db, int mediaType)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Works" ("Id", "MediaType", "CanonicalTitle", "CreatedAt", "UpdatedAt") VALUES ({id}, {mediaType}, 'Harbor', {now}, {now})""");
        return id;
    }

    private static async Task<Guid> AddEpisodeAsync(AppDbContext db, Guid workId, Guid? seasonId, int season, int number)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "WorkEpisodes" ("Id", "WorkId", "SeasonId", "SeasonNumber", "EpisodeNumber", "IsSpecial", "AiredAt", "CreatedAt") VALUES ({id}, {workId}, {seasonId}, {season}, {number}, FALSE, {now.AddDays(-30)}, {now})""");
        return id;
    }

    private static async Task<Guid> AddSeasonAsync(AppDbContext db, Guid workId, int number)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WorkSeasons" ("Id", "WorkId", "SeasonNumber", "IsSpecial", "CreatedAt") VALUES ({id}, {workId}, {number}, FALSE, {DateTime.UtcNow})""");
        return id;
    }

    private static async Task<string> AddRequestAsync(AppDbContext db, string kind, string externalId, string? payload, Guid? workId = null)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.AddDays(-5).ToString("o");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "AcquisitionRequests" ("Id", "Kind", "Provider", "ExternalId", "Title", "PayloadJson", "RequestedByProfileId", "Status", "CreatedAt", "UpdatedAt", "WorkId")
            VALUES ({id}, {kind}, 'tmdb', {externalId}, 'Harbor', {payload}, 'owner', 'approved', {now}, {now}, {workId?.ToString()})
            """);
        return id;
    }

    private static async Task<List<(short Kind, Guid Target)>> TargetsOfAsync(AppDbContext db, string requestId) =>
        (await db.Database.SqlQuery<string>($"""SELECT "TargetKind"::text || ':' || "TargetId"::text AS "Value" FROM "RequestTargets" WHERE "RequestId" = {requestId}""").ToListAsync())
        .Select(row => (short.Parse(row.Split(':')[0]), Guid.Parse(row.Split(':')[1])))
        .ToList();

    [TestMethod]
    public async Task OpenVideoRequestsKeepWhatTheyProvablyAskedForAndNothingIsInvented()
    {
        await using var db = await CreateBeforeAsync();
        var series = await AddWorkAsync(db, 1);
        var seasonOne = await AddSeasonAsync(db, series, 1);
        var seasonTwo = await AddSeasonAsync(db, series, 2);
        var first = await AddEpisodeAsync(db, series, seasonOne, 1, 1);
        await AddEpisodeAsync(db, series, seasonOne, 1, 2);
        var third = await AddEpisodeAsync(db, series, seasonTwo, 2, 1);
        var fourth = await AddEpisodeAsync(db, series, seasonTwo, 2, 2);
        var movie = await AddWorkAsync(db, 0);

        var custom = await AddRequestAsync(
            db,
            "tv",
            "1",
            new VideoRequestPayload(series, "Harbor", 2024) { Requested = new VideoRequestScopeChoice(VideoRequestScope.Custom, [seasonTwo], [first], MonitorFuture: false) }.Serialize());
        var whole = await AddRequestAsync(db, "tv", "2", new VideoRequestPayload(series, "Harbor", 2024) { Requested = new VideoRequestScopeChoice(VideoRequestScope.AllCurrentAndFuture, [], [], MonitorFuture: true) }.Serialize());
        var applied = await AddRequestAsync(db, "tv", "3", new VideoRequestPayload(series, "Harbor", 2024).Serialize());
        var plainMovie = await AddRequestAsync(db, "movie", "4", null, movie);
        var appliedMovie = await AddRequestAsync(db, "movie", "5", new VideoRequestPayload(movie, "Harbor", 2024).Serialize(), movie);
        var book = await AddRequestAsync(db, "book", "6", null);
        var recordedBook = await AddRequestAsync(db, "book", "8", null);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt") VALUES ({appliedMovie}, {movie}, 0, {movie}, now()), ({book}, {movie}, 0, {movie}, now())""");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt") VALUES ({recordedBook}, {movie}, 0, {movie}, {DateTime.UtcNow.AddMinutes(3)})""");

        await db.Database.MigrateAsync();

        CollectionAssert.AreEquivalent(new[] { first, third, fourth }, (await TargetsOfAsync(db, custom)).Select(target => target.Target).ToArray(), "A custom choice keeps its named episodes and the episodes of its named seasons.");
        Assert.IsTrue((await TargetsOfAsync(db, custom)).All(target => target.Kind == 1));
        Assert.AreEqual((short)0, Assert.ContainsSingle(await TargetsOfAsync(db, whole)).Kind, "A whole-title choice keeps the whole title.");
        Assert.IsEmpty(await TargetsOfAsync(db, applied), "A choice that was applied earlier cannot be recovered, so no scope is invented.");
        Assert.AreEqual(movie, Assert.ContainsSingle(await TargetsOfAsync(db, plainMovie)).Target, "A request without a payload is a request for the title.");
        Assert.IsEmpty(await TargetsOfAsync(db, appliedMovie), "A Movie request that Monitoring may have opened proves nothing, so the earlier assumption is dropped.");
        Assert.IsEmpty(await TargetsOfAsync(db, book), "A Book request that the Wanted pass may have opened proves nothing, so the earlier assumption is dropped.");
        Assert.AreEqual(movie, Assert.ContainsSingle(await TargetsOfAsync(db, recordedBook)).Target, "What somebody recorded when submitting a request is kept.");
    }
}
