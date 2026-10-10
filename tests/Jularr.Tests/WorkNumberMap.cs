using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// The migration tests that seed an older schema with Work GUIDs and then migrate to the latest one: the Work number migration keeps its old-to-new map
/// until the application converts the file-based stores, so a test finds the number its seeded Work got there.
/// </summary>
internal static class WorkNumberMap
{
    public static async Task<long> OfAsync(AppDbContext db, Guid oldId) =>
        await db.Database.SqlQuery<long>($"""SELECT "NewId" AS "Value" FROM "WorkIdMigrationMap" WHERE "OldId" = {oldId}""").SingleAsync();
}
