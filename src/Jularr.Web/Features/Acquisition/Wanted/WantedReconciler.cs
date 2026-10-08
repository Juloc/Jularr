using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Wanted;

// The one writer of WantedItems: what the intent sources want right now minus what the library already holds, set-based and idempotent.
public sealed class WantedReconciler(AppDbContext db, TimeProvider clock)
{
    private static readonly string[] CoveredTargets =
    [
        // Movie: a video asset of the Work itself, backed by a stored file.
        """
        SELECT w."Id" AS "WorkId", 0::smallint AS "TargetKind", w."Id" AS "TargetId"
        FROM "Works" w
        LEFT JOIN "WorkMonitoring" wd ON wd."TargetId" = w."Id"
        WHERE w."MediaType" = {1} AND ({0} = '00000000-0000-0000-0000-000000000000'::uuid OR w."Id" = {0})
          AND COALESCE(wd."Monitored", EXISTS (SELECT 1 FROM monitored_relation r WHERE r."WorkId" = w."Id"))
          AND NOT EXISTS (
              SELECT 1 FROM "MediaAssets" a JOIN "StoredFiles" f ON f."MediaAssetId" = a."Id"
              WHERE a."WorkId" = w."Id" AND a."WorkEpisodeId" IS NULL AND a."WorkTrackId" IS NULL AND a."Kind" = 0)
        """,
        // Episode: monitored through its own decision, its season, its Work or a relation; only once aired.
        """
        SELECT e."WorkId", 1::smallint, e."Id"
        FROM "WorkEpisodes" e
        JOIN "Works" w ON w."Id" = e."WorkId" AND w."MediaType" = {2}
        LEFT JOIN "WorkMonitoring" ed ON ed."TargetId" = e."Id"
        LEFT JOIN "WorkMonitoring" sd ON sd."TargetId" = e."SeasonId"
        LEFT JOIN "WorkMonitoring" wd ON wd."TargetId" = e."WorkId"
        WHERE ({0} = '00000000-0000-0000-0000-000000000000'::uuid OR e."WorkId" = {0})
          AND COALESCE(ed."Monitored", sd."Monitored", wd."Monitored", EXISTS (SELECT 1 FROM monitored_relation r WHERE r."WorkId" = e."WorkId"))
          AND (e."AiredAt" IS NULL OR e."AiredAt" <= {3})
          AND NOT EXISTS (
              SELECT 1 FROM "MediaAssets" a JOIN "StoredFiles" f ON f."MediaAssetId" = a."Id"
              WHERE a."WorkEpisodeId" = e."Id" AND a."Kind" = 0)
        """
    ];

    private static readonly string TargetsSql =
        "WITH monitored_relation AS (SELECT DISTINCT \"WorkId\" FROM (" + MonitoringResolver.RelationCoveredWorksSql + ") r), wanted AS ("
        + string.Join(" UNION ALL ", CoveredTargets)
        + ") ";

    // Brings the items of one Work, or of every Work of the reconciled types, in line with the current intent and installed coverage.
    public async Task ReconcileAsync(Guid? workId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Database.ExecuteSqlRawAsync(
            TargetsSql
            + """
            , added AS (
                INSERT INTO "WantedItems" ("WorkId", "TargetKind", "TargetId", "CreatedAt")
                SELECT "WorkId", "TargetKind", "TargetId", {3} FROM wanted
                ON CONFLICT ("TargetKind", "TargetId") DO NOTHING
                RETURNING 1)
            DELETE FROM "WantedItems" i
            WHERE ({0} = '00000000-0000-0000-0000-000000000000'::uuid OR i."WorkId" = {0})
              AND EXISTS (SELECT 1 FROM "Works" w WHERE w."Id" = i."WorkId" AND w."MediaType" IN ({1}, {2}))
              AND NOT EXISTS (SELECT 1 FROM wanted x WHERE x."TargetKind" = i."TargetKind" AND x."TargetId" = i."TargetId")
            """,
            Parameters(workId, 0),
            cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> TargetIdsAsync(Guid workId, WantedTargetKind kind, CancellationToken cancellationToken) =>
        (await db.WantedItems.AsNoTracking().Where(x => x.WorkId == workId && x.TargetKind == kind).Select(x => x.TargetId).ToListAsync(cancellationToken)).ToHashSet();

    // The Works of one type that have wanted items and no open request to carry them, in id order.
    public async Task<IReadOnlyList<Guid>> WorksWithoutOpenRequestAsync(WorkMediaType type, Guid after, int limit, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQueryRaw<Guid>(
                """
                SELECT DISTINCT x."WorkId" AS "Value" FROM "WantedItems" x
                JOIN "Works" w ON w."Id" = x."WorkId" AND w."MediaType" = {0} AND x."WorkId" > {1}
                WHERE NOT EXISTS (
                    SELECT 1 FROM "AcquisitionRequests" r
                    LEFT JOIN "WorkExternalIdentities" i ON i."WorkId" = x."WorkId" AND i."Provider" = r."Provider" AND i."ExternalId" = r."ExternalId"
                    WHERE r."Kind" = {2} AND r."Status" IN ('pending', 'approved', 'searching', 'downloading', 'importing')
                      AND (r."WorkId" = x."WorkId"::text OR i."WorkId" IS NOT NULL))
                ORDER BY 1 LIMIT {3}
                """,
                (int)type,
                after,
                AcquisitionAccessNames.Kind(VideoWorkLinks.AcquisitionKind(type)),
                limit)
            .ToListAsync(cancellationToken);

    private object[] Parameters(Guid? workId, int limit) => [workId ?? Guid.Empty, (int)WorkMediaType.Movie, (int)WorkMediaType.Series, clock.GetUtcNow().UtcDateTime, limit];
}
