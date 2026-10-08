using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jularr.Tests;

/// <summary>
/// The Movie and TV monitoring that lived in the request payloads becomes canonical decisions that say the same about every episode known at the time
/// of the migration, and the payload no longer carries the state.
/// </summary>
[TestClass]
public sealed class CanonicalMonitoringMigrationTests
{
    private const string PreviousMigration = "20261008130000_DiscoverySnapshots";

    private sealed record Seeded(Guid WorkId, Guid[] SeasonIds, Guid[][] EpisodeIds, Guid RequestId);

    private static async Task<AppDbContext> CreateBeforeAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source=media-core-{Guid.NewGuid():N}.db;Foreign Keys=True").Options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        return db;
    }

    /// <summary>A Series with the given episodes per season, aired by the offsets in days from now, and one request with the payload.</summary>
    private static async Task<Seeded> SeedSeriesAsync(AppDbContext db, string kind, int[][] airedDays, Func<Guid, Guid[], Guid[][], object> payload)
    {
        var workId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Works" ("Id", "MediaType", "CanonicalTitle", "CreatedAt", "UpdatedAt") VALUES ({workId}, {(kind == "movie" ? 0 : 1)}, 'Harbor', {now}, {now})""");
        var seasonIds = new List<Guid>();
        var episodeIds = new List<Guid[]>();
        for (var season = 0; season < airedDays.Length; season++)
        {
            var seasonId = Guid.NewGuid();
            seasonIds.Add(seasonId);
            await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WorkSeasons" ("Id", "WorkId", "SeasonNumber", "IsSpecial", "CreatedAt") VALUES ({seasonId}, {workId}, {season + 1}, FALSE, {now})""");
            var ids = new List<Guid>();
            for (var number = 0; number < airedDays[season].Length; number++)
            {
                var episodeId = Guid.NewGuid();
                ids.Add(episodeId);
                var aired = now.AddDays(airedDays[season][number]);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""INSERT INTO "WorkEpisodes" ("Id", "WorkId", "SeasonId", "SeasonNumber", "EpisodeNumber", "IsSpecial", "AiredAt", "CreatedAt") VALUES ({episodeId}, {workId}, {seasonId}, {season + 1}, {number + 1}, FALSE, {aired}, {now})""");
            }

            episodeIds.Add([.. ids]);
        }

        var requestId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(payload(workId, [.. seasonIds], [.. episodeIds]), JsonSerializerOptions.Web);
        var created = now.AddDays(-20).ToString("o");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "AcquisitionRequests" ("Id", "Kind", "Provider", "ExternalId", "Title", "PayloadJson", "RequestedByProfileId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({requestId.ToString()}, {kind}, 'tmdb', {workId.ToString()}, 'Harbor', {json}, 'owner', 'approved', {created}, {created})
            """);
        return new Seeded(workId, [.. seasonIds], [.. episodeIds], requestId);
    }

    private static async Task<WorkMonitoringView> ViewAfterAsync(AppDbContext db, Guid workId)
    {
        await db.Database.MigrateAsync();
        return await new MonitoringResolver(db).LoadAsync(workId, CancellationToken.None);
    }

    [TestMethod]
    public async Task AllWithAnExcludedEpisodeAndAnExcludedSeasonKeepsTheExclusionsAndAnExplicitlySelectedEpisodeStaysOn()
    {
        await using var db = await CreateBeforeAsync();
        var series = await SeedSeriesAsync(db, "tv", [[-9, -8], [-7, -6]], (work, seasons, episodes) => new
        {
            workId = work,
            title = "Harbor",
            scope = 1,
            selectedEpisodeIds = new[] { episodes[1][1] },
            monitorFuture = true,
            excludedEpisodeIds = new[] { episodes[0][1] },
            excludedSeasonIds = new[] { seasons[1] }
        });

        var view = await ViewAfterAsync(db, series.WorkId);

        Assert.IsTrue(view.IsMonitored(series.EpisodeIds[0][0], series.SeasonIds[0]));
        Assert.IsFalse(view.IsMonitored(series.EpisodeIds[0][1], series.SeasonIds[0]), "An excluded episode stays off.");
        Assert.IsFalse(view.IsMonitored(series.EpisodeIds[1][0], series.SeasonIds[1]), "An episode of an excluded season stays off.");
        Assert.IsTrue(view.IsMonitored(series.EpisodeIds[1][1], series.SeasonIds[1]), "An episode that was selected on its own beats its season's exclusion.");
    }

    [TestMethod]
    public async Task FutureOnlyKeepsTodaysEpisodesThatAiredBeforeTheFutureStartedOffAndMonitorsWhatAiresAfter()
    {
        await using var db = await CreateBeforeAsync();
        var series = await SeedSeriesAsync(db, "tv", [[-30, -2, 5]], (work, seasons, episodes) => new
        {
            workId = work,
            scope = 2,
            monitorFuture = true,
            monitorFutureFromUtc = DateTime.UtcNow.AddDays(-10),
            selectedEpisodeIds = Array.Empty<Guid>()
        });

        var view = await ViewAfterAsync(db, series.WorkId);
        var laterEpisode = Guid.NewGuid();

        Assert.IsFalse(view.IsMonitored(series.EpisodeIds[0][0], series.SeasonIds[0]), "It aired before the future started.");
        Assert.IsTrue(view.IsMonitored(series.EpisodeIds[0][1], series.SeasonIds[0]));
        Assert.IsTrue(view.IsMonitored(series.EpisodeIds[0][2], series.SeasonIds[0]));
        Assert.IsTrue(view.IsMonitored(laterEpisode, series.SeasonIds[0]), "An episode added later inherits the monitored Work.");
    }

    [TestMethod]
    public async Task ACustomSelectionWithoutFutureMonitorsOnlyTheSelectedSeasonAndEpisodeAndWhatIsAddedToTheSelectedSeason()
    {
        await using var db = await CreateBeforeAsync();
        var series = await SeedSeriesAsync(db, "tv", [[-9, -8], [-7, -6]], (work, seasons, episodes) => new
        {
            workId = work,
            scope = 3,
            monitorFuture = false,
            selectedSeasonIds = new[] { seasons[0] },
            selectedEpisodeIds = new[] { episodes[1][0] }
        });

        var view = await ViewAfterAsync(db, series.WorkId);

        Assert.IsTrue(series.EpisodeIds[0].All(id => view.IsMonitored(id, series.SeasonIds[0])));
        Assert.IsTrue(view.IsMonitored(series.EpisodeIds[1][0], series.SeasonIds[1]));
        Assert.IsFalse(view.IsMonitored(series.EpisodeIds[1][1], series.SeasonIds[1]));
        Assert.IsTrue(view.IsMonitored(Guid.NewGuid(), series.SeasonIds[0]), "A new episode of a selected season is monitored.");
        Assert.IsFalse(view.IsMonitored(Guid.NewGuid(), series.SeasonIds[1]), "A new episode of another season is not.");
    }

    [TestMethod]
    public async Task AnUnmonitoredRequestStaysUnmonitoredAndItsPayloadKeepsOnlyRequestState()
    {
        await using var db = await CreateBeforeAsync();
        var series = await SeedSeriesAsync(db, "tv", [[-9, -8]], (work, _, _) => new { workId = work, scope = 1, monitorFuture = true, monitored = false, scopeRevision = 4 });

        var view = await ViewAfterAsync(db, series.WorkId);
        var payload = await db.Database.SqlQuery<string>($"""SELECT "PayloadJson" AS "Value" FROM "AcquisitionRequests" WHERE "Id" = {series.RequestId.ToString()}""").SingleAsync();
        using var document = JsonDocument.Parse(payload);

        Assert.IsFalse(view.IsAnyMonitored);
        Assert.IsFalse(document.RootElement.TryGetProperty("scope", out _), "The scope is gone from the payload.");
        Assert.IsFalse(document.RootElement.TryGetProperty("monitored", out _));
        Assert.AreEqual(4, document.RootElement.GetProperty("monitoringRevision").GetInt32());
        Assert.IsTrue(document.RootElement.GetProperty("endedByMonitoring").GetBoolean(), "Turning monitoring on again reopens this request.");
    }

    [TestMethod]
    public async Task AMovieRequestMonitorsItsWorkAndAMonitoredMusicArtistBecomesASourceWhoseSwitchedOffAlbumKeepsItsDecision()
    {
        await using var db = await CreateBeforeAsync();
        var movie = await SeedSeriesAsync(db, "movie", [], (work, _, _) => new { workId = work, scope = 0, monitorFuture = false });
        var now = DateTime.UtcNow;
        var artistId = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var off = Guid.NewGuid();
        foreach (var (id, title) in new[] { (kept, "Kept"), (off, "Off") })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "Works" ("Id", "MediaType", "CanonicalTitle", "CreatedAt", "UpdatedAt") VALUES ({id}, 6, {title}, {now}, {now})""");
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MusicArtists" ("Id", "Name", "SortName", "Monitor", "MonitorFromUtc", "AddedAt") VALUES ({artistId}, 'Waves', 'Waves', 1, {now}, {now})""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MusicAlbums" ("WorkId", "ArtistId", "Type", "Monitored", "CreatedAt") VALUES ({kept}, {artistId}, 0, TRUE, {now}), ({off}, {artistId}, 0, FALSE, {now})""");

        await db.Database.MigrateAsync();
        var resolver = new MonitoringResolver(db);

        Assert.IsTrue((await resolver.LoadAsync(movie.WorkId, CancellationToken.None)).IsWorkMonitored);
        Assert.IsTrue((await resolver.LoadAsync(kept, CancellationToken.None)).IsWorkMonitored, "The artist reaches the album that was monitored.");
        Assert.IsFalse((await resolver.LoadAsync(off, CancellationToken.None)).IsWorkMonitored, "The album that was switched off keeps its decision.");
    }
}
