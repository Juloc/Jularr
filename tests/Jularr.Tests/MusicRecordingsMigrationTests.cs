using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jularr.Tests;

[TestClass]
public sealed class MusicRecordingsMigrationTests
{
    [TestMethod]
    public async Task TheRecordingIdsOfExistingTracksBecomeSharedRecordingsWithoutLosingAny()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source=music-recordings-{Guid.NewGuid():N}.db;Foreign Keys=True").Options);
        await db.GetService<IMigrator>().MigrateAsync("20261009140000_ReadingUnitIdentity");
        var now = DateTime.UtcNow;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Works" ("Id", "MediaType", "CanonicalTitle", "CreatedAt", "UpdatedAt") VALUES ({first}, 4, 'Homework', {now}, {now}), ({second}, 4, 'Live', {now}, {now})""");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "WorkTracks" ("Id", "WorkId", "Disc", "Number", "Title", "MusicBrainzRecordingId", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {first}, 1, 1, 'Da Funk', 'rec-1', {now}), ({Guid.NewGuid()}, {first}, 1, 2, 'Unknown', NULL, {now}), ({Guid.NewGuid()}, {second}, 1, 1, 'Da Funk (live)', 'rec-1', {now})
            """);

        await db.Database.MigrateAsync();

        Assert.AreEqual(1, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "MusicRecordings" WHERE "MusicBrainzId" = 'rec-1'""").SingleAsync());
        Assert.AreEqual(2, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkTracks" WHERE "MusicRecordingId" IS NOT NULL""").SingleAsync(), "Both placements point at the one recording.");
        Assert.AreEqual(1, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkTracks" WHERE "MusicRecordingId" IS NULL""").SingleAsync(), "A track without an id stays unresolved.");
    }
}
