using Jularr.Web.Features.Acquisition.Quality;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Data;

/// <summary>
/// One-time follow-up of the Work number migration. The stores that live outside the database and name a Work by its old GUID are converted with the
/// migration's old-to-new map, then the map is dropped; without the map table there is nothing to do.
/// </summary>
public static class WorkIdMigrationFollowUp
{
    public static async Task RunAsync(AppDbContext db, QualityProfileStore profiles, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var hasMap = await db.Database.SqlQueryRaw<bool>("""SELECT to_regclass('public."WorkIdMigrationMap"') IS NOT NULL AS "Value" """).SingleAsync(cancellationToken);
        if (!hasMap)
        {
            return;
        }

        var map = await db.Database.SqlQueryRaw<WorkNumber>("""SELECT "OldId", "NewId" FROM "WorkIdMigrationMap" """).ToDictionaryAsync(row => row.OldId, row => row.NewId, cancellationToken);
        var converted = await profiles.ConvertWorkAssignmentsAsync(map, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""DROP TABLE "WorkIdMigrationMap" """, cancellationToken);
        log?.Invoke($"Work number migration finished: {converted} per-Work quality profile assignment(s) converted.");
    }

    private sealed record WorkNumber(Guid OldId, long NewId);
}
