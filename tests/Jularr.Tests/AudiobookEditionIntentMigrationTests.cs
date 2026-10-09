using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jularr.Tests;

[TestClass]
public sealed class AudiobookEditionIntentMigrationTests
{
    [TestMethod]
    public async Task AnAudiobookRequestThatNamedItsWorkNamesTheAudioEditionNowAndMonitoringMayDecideOnEditions()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source=audiobook-edition-{Guid.NewGuid():N}.db;Foreign Keys=True").Options);
        await db.GetService<IMigrator>().MigrateAsync("20261009100000_RequestTargetsRepair");
        var work = Guid.NewGuid();
        var otherWork = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Works" ("Id", "MediaType", "CanonicalTitle", "CreatedAt", "UpdatedAt") VALUES ({work}, 3, 'Frieren', {now}, {now}), ({otherWork}, 3, 'Dune', {now}, {now})""");
        var request = Guid.NewGuid().ToString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "AcquisitionRequests" ("Id", "Kind", "Provider", "ExternalId", "Title", "RequestedByProfileId", "Status", "CreatedAt", "UpdatedAt", "WorkId")
            VALUES ({request}, 'audiobook', 'catalog', 'c1', 'Frieren', 'owner', 'approved', {now.ToString("o")}, {now.ToString("o")}, {work.ToString()})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt") VALUES ({request}, {work}, 4, {work}, now())""");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WantedItems" ("WorkId", "TargetKind", "TargetId", "CreatedAt") VALUES ({work}, 4, {work}, now())""");

        await db.Database.MigrateAsync();

        var edition = await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "WorkEditions" WHERE "WorkId" = {work} AND "EditionKey" = 'audiobook' AND "Format" = 'audiobook'""").SingleAsync();
        Assert.AreEqual(edition, await db.Database.SqlQuery<Guid>($"""SELECT "TargetId" AS "Value" FROM "RequestTargets" WHERE "RequestId" = {request} AND "TargetKind" = 4""").SingleAsync(), "The request names the edition now.");
        Assert.AreEqual(0, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WantedItems" WHERE "TargetKind" = 4""").SingleAsync(), "The rows of edition targets are rebuilt by the next reconcile.");
        Assert.AreEqual(0, await db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "WorkEditions" WHERE "WorkId" = {otherWork}""").SingleAsync(), "A Work no request named gets no edition.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "WorkMonitoring" ("WorkId", "Kind", "TargetId", "Monitored", "UpdatedAt") VALUES ({work}, 6, {edition}, TRUE, now())""");
    }
}
