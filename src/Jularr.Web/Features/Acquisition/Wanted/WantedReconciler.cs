using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Wanted;

// The one writer of WantedItems: what the intent sources want right now minus what the library already holds, set-based and idempotent.
public sealed class WantedReconciler(AppDbContext db, TimeProvider clock)
{
    private static readonly (MediaAcquisitionKind Kind, WorkMediaType Type)[] Reconciled =
    [
        (MediaAcquisitionKind.Movie, WorkMediaType.Movie),
        (MediaAcquisitionKind.Tv, WorkMediaType.Series),
        (MediaAcquisitionKind.Book, WorkMediaType.Book),
        (MediaAcquisitionKind.LightNovel, WorkMediaType.LightNovel),
        (MediaAcquisitionKind.Manga, WorkMediaType.Manga),
        (MediaAcquisitionKind.Music, WorkMediaType.Music)
    ];

    private const string AllWorks = "'00000000-0000-0000-0000-000000000000'::uuid";

    // Parameters: {0} one Work or the empty id for all, {1} Movie, {2} Series, {3} now, {4} Book, {5} LightNovel, {6} Manga, {7} Music, {8} all reconciled types.
    private static readonly string[] CoveredTargets =
    [
        // A Work is wanted as a whole while it is monitored and nothing of it is installed (video asset, book file, novel volume, manga chapter, audio asset).
        $$"""
        SELECT w."Id" AS "WorkId", 0::smallint AS "TargetKind", w."Id" AS "TargetId"
        FROM "Works" w
        LEFT JOIN "WorkMonitoring" wd ON wd."TargetId" = w."Id"
        WHERE w."MediaType" <> {2} AND w."MediaType" = ANY({8}) AND ({0} = {{AllWorks}} OR w."Id" = {0})
          AND COALESCE(wd."Monitored", EXISTS (SELECT 1 FROM monitored_relation r WHERE r."WorkId" = w."Id"))
          AND NOT CASE w."MediaType"
              WHEN {1} THEN EXISTS (
                  SELECT 1 FROM "MediaAssets" a JOIN "StoredFiles" f ON f."MediaAssetId" = a."Id"
                  WHERE a."WorkId" = w."Id" AND a."WorkEpisodeId" IS NULL AND a."WorkTrackId" IS NULL AND a."Kind" = 0)
              WHEN {4} THEN EXISTS (
                  SELECT 1 FROM "WorkSourceLinks" l JOIN "BookEditions" e ON e."Id" = l."SourceId" JOIN "BookFiles" f ON f."EditionId" = e."Id"
                  WHERE l."WorkId" = w."Id" AND l."SourceKind" = 2)
              WHEN {5} THEN EXISTS (
                  SELECT 1 FROM "WorkSourceLinks" l JOIN "NovelVolumes" v ON v."WorkId" = l."SourceId"
                  WHERE l."WorkId" = w."Id" AND l."SourceKind" = 1)
              WHEN {6} THEN EXISTS (
                  SELECT 1 FROM "WorkSourceLinks" l JOIN "MangaChapters" c ON c."SeriesId" = l."SourceId"::text
                  WHERE l."WorkId" = w."Id" AND l."SourceKind" = 3)
              WHEN {7} THEN EXISTS (
                  SELECT 1 FROM "MediaAssets" a JOIN "StoredFiles" f ON f."MediaAssetId" = a."Id"
                  WHERE a."WorkId" = w."Id" AND a."Kind" = 1)
              ELSE FALSE END
          AND (w."MediaType" <> {7} OR EXISTS (
              SELECT 1 FROM "MusicAlbums" m
              WHERE m."WorkId" = w."Id" AND m."MusicBrainzReleaseGroupId" IS NOT NULL AND (m."ReleaseDate" IS NULL OR m."ReleaseDate" <= {3})))
        """,
        // Episode: monitored through its own decision, its season, its Work or a relation; only once aired.
        $$"""
        SELECT e."WorkId", 1::smallint, e."Id"
        FROM "WorkEpisodes" e
        JOIN "Works" w ON w."Id" = e."WorkId" AND w."MediaType" = {2}
        LEFT JOIN "WorkMonitoring" ed ON ed."TargetId" = e."Id"
        LEFT JOIN "WorkMonitoring" sd ON sd."TargetId" = e."SeasonId"
        LEFT JOIN "WorkMonitoring" wd ON wd."TargetId" = e."WorkId"
        WHERE ({0} = {{AllWorks}} OR e."WorkId" = {0})
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

    private static readonly string ReconcileSql =
        TargetsSql
        + $$"""
        , added AS (
            INSERT INTO "WantedItems" ("WorkId", "TargetKind", "TargetId", "CreatedAt")
            SELECT "WorkId", "TargetKind", "TargetId", {3} FROM wanted
            ON CONFLICT ("TargetKind", "TargetId") DO NOTHING
            RETURNING 1)
        DELETE FROM "WantedItems" i
        WHERE ({0} = {{AllWorks}} OR i."WorkId" = {0})
          AND EXISTS (SELECT 1 FROM "Works" w WHERE w."Id" = i."WorkId" AND w."MediaType" = ANY({8}))
          AND NOT EXISTS (SELECT 1 FROM wanted x WHERE x."TargetKind" = i."TargetKind" AND x."TargetId" = i."TargetId")
        """;

    public static WorkMediaType WorkTypeOf(MediaAcquisitionKind kind) =>
        Reconciled.Single(entry => entry.Kind == kind).Type;

    // Brings the items of one Work, or of every Work of the reconciled types, in line with the current intent and installed coverage.
    public async Task ReconcileAsync(Guid? workId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync(ReconcileSql, Parameters(workId), cancellationToken);

    public async Task<IReadOnlySet<Guid>> TargetIdsAsync(Guid workId, WantedTargetKind kind, CancellationToken cancellationToken) =>
        (await db.WantedItems.AsNoTracking().Where(x => x.WorkId == workId && x.TargetKind == kind).Select(x => x.TargetId).ToListAsync(cancellationToken)).ToHashSet();

    // The Works of one kind that have wanted items and no open request to carry them, in id order.
    public async Task<IReadOnlyList<Guid>> WorksWithoutOpenRequestAsync(MediaAcquisitionKind kind, Guid after, int limit, CancellationToken cancellationToken) =>
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
                (int)WorkTypeOf(kind),
                after,
                AcquisitionAccessNames.Kind(kind),
                limit)
            .ToListAsync(cancellationToken);

    private object[] Parameters(Guid? workId) =>
    [
        workId ?? Guid.Empty,
        (int)WorkMediaType.Movie,
        (int)WorkMediaType.Series,
        clock.GetUtcNow().UtcDateTime,
        (int)WorkMediaType.Book,
        (int)WorkMediaType.LightNovel,
        (int)WorkMediaType.Manga,
        (int)WorkMediaType.Music,
        Reconciled.Select(entry => (int)entry.Type).ToArray()
    ];
}
