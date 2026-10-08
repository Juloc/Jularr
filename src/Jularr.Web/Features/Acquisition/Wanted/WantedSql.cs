using Jularr.Web.Features.Monitoring;

namespace Jularr.Web.Features.Acquisition.Wanted;

// The SQL of the Wanted queue. Every value is a named parameter; nothing outside this file's fixed text is ever part of a statement.
internal static class WantedSql
{
    // @workId: one Work, or null for every Work. @now. @types: the media types that are reconciled.
    // @movie, @series, @book, @lightNovel, @manga, @music: the media type numbers the installed-coverage rules distinguish.
    public const string Reconcile =
        $$"""
        WITH monitored_relation AS (
            SELECT DISTINCT reached."WorkId" FROM ({{MonitoringResolver.RelationCoveredWorksSql}}) reached
        ),
        open_request_targets AS (
            SELECT target."TargetKind", target."TargetId"
            FROM "RequestTargets" target
            JOIN "AcquisitionRequests" request ON request."Id" = target."RequestId"
            WHERE request."Status" IN ('approved', 'searching', 'downloading', 'importing')
        ),
        -- A Work is wanted as a whole while it is monitored or explicitly requested and nothing of it is installed.
        wanted_works AS (
            SELECT work."Id" AS "WorkId", 0::smallint AS "TargetKind", work."Id" AS "TargetId"
            FROM "Works" work
            LEFT JOIN "WorkMonitoring" decision ON decision."TargetId" = work."Id"
            WHERE work."MediaType" <> @series AND work."MediaType" = ANY(@types)
              AND (@workId::uuid IS NULL OR work."Id" = @workId)
              AND (COALESCE(decision."Monitored", EXISTS (SELECT 1 FROM monitored_relation relation WHERE relation."WorkId" = work."Id"))
                   OR EXISTS (SELECT 1 FROM open_request_targets asked WHERE asked."TargetKind" = 0 AND asked."TargetId" = work."Id"))
              AND NOT CASE work."MediaType"
                  WHEN @movie THEN EXISTS (
                      SELECT 1 FROM "MediaAssets" asset JOIN "StoredFiles" stored ON stored."MediaAssetId" = asset."Id"
                      WHERE asset."WorkId" = work."Id" AND asset."WorkEpisodeId" IS NULL AND asset."WorkTrackId" IS NULL AND asset."Kind" = 0)
                  WHEN @book THEN EXISTS (
                      SELECT 1 FROM "WorkSourceLinks" link
                      JOIN "BookEditions" edition ON edition."Id" = link."SourceId"
                      JOIN "BookFiles" file ON file."EditionId" = edition."Id"
                      WHERE link."WorkId" = work."Id" AND link."SourceKind" = 2)
                  WHEN @lightNovel THEN EXISTS (
                      SELECT 1 FROM "WorkSourceLinks" link
                      JOIN "NovelVolumes" volume ON volume."WorkId" = link."SourceId"
                      WHERE link."WorkId" = work."Id" AND link."SourceKind" = 1)
                  WHEN @manga THEN EXISTS (
                      SELECT 1 FROM "WorkSourceLinks" link
                      JOIN "MangaChapters" chapter ON chapter."SeriesId" = link."SourceId"::text
                      WHERE link."WorkId" = work."Id" AND link."SourceKind" = 3)
                  WHEN @music THEN EXISTS (
                      SELECT 1 FROM "MediaAssets" asset JOIN "StoredFiles" stored ON stored."MediaAssetId" = asset."Id"
                      WHERE asset."WorkId" = work."Id" AND asset."Kind" = 1)
                  ELSE FALSE END
              AND (work."MediaType" <> @music OR EXISTS (
                  SELECT 1 FROM "MusicAlbums" album
                  WHERE album."WorkId" = work."Id" AND album."MusicBrainzReleaseGroupId" IS NOT NULL AND (album."ReleaseDate" IS NULL OR album."ReleaseDate" <= @now)))
        ),
        -- An episode is wanted once aired, while it is monitored (its own decision, its season, its Work or a relation) or explicitly requested, and has no file.
        wanted_episodes AS (
            SELECT episode."WorkId", 1::smallint AS "TargetKind", episode."Id" AS "TargetId"
            FROM "WorkEpisodes" episode
            JOIN "Works" work ON work."Id" = episode."WorkId" AND work."MediaType" = @series
            LEFT JOIN "WorkMonitoring" own ON own."TargetId" = episode."Id"
            LEFT JOIN "WorkMonitoring" season ON season."TargetId" = episode."SeasonId"
            LEFT JOIN "WorkMonitoring" whole ON whole."TargetId" = episode."WorkId"
            WHERE (@workId::uuid IS NULL OR episode."WorkId" = @workId)
              AND (COALESCE(own."Monitored", season."Monitored", whole."Monitored", EXISTS (SELECT 1 FROM monitored_relation relation WHERE relation."WorkId" = episode."WorkId"))
                   OR EXISTS (SELECT 1 FROM open_request_targets asked
                              WHERE (asked."TargetKind" = 1 AND asked."TargetId" = episode."Id") OR (asked."TargetKind" = 0 AND asked."TargetId" = episode."WorkId")))
              AND (episode."AiredAt" IS NULL OR episode."AiredAt" <= @now)
              AND NOT EXISTS (
                  SELECT 1 FROM "MediaAssets" asset JOIN "StoredFiles" stored ON stored."MediaAssetId" = asset."Id"
                  WHERE asset."WorkEpisodeId" = episode."Id" AND asset."Kind" = 0)
        ),
        wanted AS (
            SELECT * FROM wanted_works
            UNION ALL
            SELECT * FROM wanted_episodes
        ),
        added AS (
            INSERT INTO "WantedItems" ("WorkId", "TargetKind", "TargetId", "CreatedAt")
            SELECT "WorkId", "TargetKind", "TargetId", @now FROM wanted
            ON CONFLICT ("TargetKind", "TargetId") DO NOTHING
            RETURNING 1
        )
        DELETE FROM "WantedItems" item
        WHERE (@workId::uuid IS NULL OR item."WorkId" = @workId)
          AND EXISTS (SELECT 1 FROM "Works" work WHERE work."Id" = item."WorkId" AND work."MediaType" = ANY(@types))
          AND NOT EXISTS (SELECT 1 FROM wanted still WHERE still."TargetKind" = item."TargetKind" AND still."TargetId" = item."TargetId")
        """;

    // @mediaType: the Work type, @after: the last Work of the previous page, @kind: the request kind name, @limit.
    public const string WorksWithoutOpenRequest =
        """
        SELECT DISTINCT item."WorkId" AS "Value"
        FROM "WantedItems" item
        JOIN "Works" work ON work."Id" = item."WorkId" AND work."MediaType" = @mediaType
        WHERE item."WorkId" > @after
          AND NOT EXISTS (
              SELECT 1 FROM "AcquisitionRequests" request
              LEFT JOIN "WorkExternalIdentities" identity
                     ON identity."WorkId" = item."WorkId" AND identity."Provider" = request."Provider" AND identity."ExternalId" = request."ExternalId"
              WHERE request."Kind" = @kind
                AND request."Status" IN ('pending', 'approved', 'searching', 'downloading', 'importing')
                AND (request."WorkId" = item."WorkId"::text OR identity."WorkId" IS NOT NULL))
        ORDER BY 1
        LIMIT @limit
        """;

    public const string RecordWork =
        """
        INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt")
        SELECT @requestId, work."Id", 0, work."Id", @now FROM "Works" work WHERE work."Id" = @workId
        ON CONFLICT DO NOTHING
        """;

    // @episodeIds and @seasonIds: what a custom request names; a season stands for all of its episodes.
    public const string RecordEpisodes =
        """
        INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt")
        SELECT @requestId, episode."WorkId", 1, episode."Id", @now FROM "WorkEpisodes" episode
        WHERE episode."WorkId" = @workId AND (episode."Id" = ANY(@episodeIds) OR episode."SeasonId" = ANY(@seasonIds))
        ON CONFLICT DO NOTHING
        """;

    // @workId: the episodes the open requests of the Work explicitly ask for, whole-Work requests included.
    public const string RequestedEpisodes =
        """
        SELECT episode."Id" AS "Value"
        FROM "RequestTargets" target
        JOIN "AcquisitionRequests" request ON request."Id" = target."RequestId" AND request."Status" IN ('approved', 'searching', 'downloading', 'importing')
        JOIN "WorkEpisodes" episode ON episode."WorkId" = target."WorkId" AND ((target."TargetKind" = 1 AND episode."Id" = target."TargetId") OR target."TargetKind" = 0)
        WHERE target."WorkId" = @workId
        """;
}
