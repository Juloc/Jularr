using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;

namespace Jularr.Web.Features.Acquisition.Wanted;

// The SQL of the Wanted queue. Every value is a named parameter; nothing outside this fixed text is ever part of a statement.
//
// Shared parameters: @workId (one Work, or null for every Work), @now, @types (the media types that are reconciled) and @movie, @series, @anime, @book,
// @lightNovel, @manga, @music (the media type numbers the installed-coverage rules distinguish).
internal static class WantedSql
{
    private const string Prerequisites =
        $$"""
        monitored_relation AS (
            SELECT DISTINCT reached."WorkId" FROM ({{MonitoringResolver.RelationCoveredWorksSql}}) reached
        ),
        open_request_targets AS (
            SELECT target."TargetKind", target."TargetId"
            FROM "RequestTargets" target
            JOIN "AcquisitionRequests" request ON request."Id" = target."RequestId"
            WHERE request."Status" IN ('approved', 'searching', 'downloading', 'importing')
        )
        """;

    // Whether the library holds anything of a Work (alias work) or of an episode (alias episode): a video asset, a book file, a novel volume, a manga chapter, an audio asset.
    private const string WorkInstalled =
        """
        CASE work."MediaType"
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
        """;

    private const string EpisodeInstalled =
        """
        EXISTS (
            SELECT 1 FROM "MediaAssets" asset JOIN "StoredFiles" stored ON stored."MediaAssetId" = asset."Id"
            WHERE asset."WorkEpisodeId" = episode."Id" AND asset."Kind" = 0)
        """;

    // Whether the library holds an audiobook of the Work (alias work): the audio edition is bridged to the Work as a source link with files.
    private const string AudiobookInstalled =
        """
        EXISTS (
            SELECT 1 FROM "WorkSourceLinks" link JOIN "AudiobookFiles" file ON file."AudiobookId" = link."SourceId"
            WHERE link."WorkId" = work."Id" AND link."SourceKind" = 7)
        """;

    // What Monitoring or an open request wants, with whether the library already holds it. A Work is intended while it is monitored or explicitly requested
    // (an album only once released), an episode once aired while it is monitored through its own decision, its season, its Work or a relation, or requested.
    private const string Intended =
        $$"""
        intended_works AS (
            SELECT work."Id" AS "WorkId", work."MediaType", 0::smallint AS "TargetKind", work."Id" AS "TargetId", {{WorkInstalled}} AS "Installed"
            FROM "Works" work
            LEFT JOIN "WorkMonitoring" decision ON decision."TargetId" = work."Id"
            WHERE work."MediaType" NOT IN (@series, @anime) AND work."MediaType" = ANY(@types)
              AND (@workId::uuid IS NULL OR work."Id" = @workId)
              AND (COALESCE(decision."Monitored", EXISTS (SELECT 1 FROM monitored_relation relation WHERE relation."WorkId" = work."Id"))
                   OR EXISTS (SELECT 1 FROM open_request_targets asked WHERE asked."TargetKind" = 0 AND asked."TargetId" = work."Id"))
              AND (work."MediaType" <> @music OR EXISTS (
                  SELECT 1 FROM "MusicAlbums" album
                  WHERE album."WorkId" = work."Id" AND album."MusicBrainzReleaseGroupId" IS NOT NULL AND (album."ReleaseDate" IS NULL OR album."ReleaseDate" <= @now)))
        ),
        intended_episodes AS (
            SELECT episode."WorkId", work."MediaType", 1::smallint AS "TargetKind", episode."Id" AS "TargetId", {{EpisodeInstalled}} AS "Installed"
            FROM "WorkEpisodes" episode
            JOIN "Works" work ON work."Id" = episode."WorkId" AND work."MediaType" IN (@series, @anime)
            LEFT JOIN "WorkMonitoring" own ON own."TargetId" = episode."Id"
            LEFT JOIN "WorkMonitoring" season ON season."TargetId" = episode."SeasonId"
            LEFT JOIN "WorkMonitoring" whole ON whole."TargetId" = episode."WorkId"
            WHERE (@workId::uuid IS NULL OR episode."WorkId" = @workId)
              AND (COALESCE(own."Monitored", season."Monitored", whole."Monitored", EXISTS (SELECT 1 FROM monitored_relation relation WHERE relation."WorkId" = episode."WorkId"))
                   OR EXISTS (SELECT 1 FROM open_request_targets asked
                              WHERE (asked."TargetKind" = 1 AND asked."TargetId" = episode."Id") OR (asked."TargetKind" = 0 AND asked."TargetId" = episode."WorkId")))
              AND (episode."AiredAt" IS NULL OR episode."AiredAt" <= @now)
        ),
        -- The audio edition of a Book Work is its own target: wanted while its own decision monitors it or a request names it, never because the Book is monitored.
        intended_audiobooks AS (
            SELECT edition."WorkId", work."MediaType", 4::smallint AS "TargetKind", edition."Id" AS "TargetId", {{AudiobookInstalled}} AS "Installed"
            FROM "WorkEditions" edition
            JOIN "Works" work ON work."Id" = edition."WorkId"
            LEFT JOIN "WorkMonitoring" decision ON decision."TargetId" = edition."Id"
            WHERE edition."Format" = '{{LegacyWorkBridge.AudiobookEditionFormat}}' AND (@workId::uuid IS NULL OR edition."WorkId" = @workId)
              AND (COALESCE(decision."Monitored", FALSE)
                   OR EXISTS (SELECT 1 FROM open_request_targets asked WHERE asked."TargetKind" = 4 AND asked."TargetId" = edition."Id"))
        ),
        intended AS (
            SELECT * FROM intended_works
            UNION ALL
            SELECT * FROM intended_episodes
            UNION ALL
            SELECT * FROM intended_audiobooks
        )
        """;

    // @upgradeTypes: the media types whose installed targets stay in the queue while their profile still wants a better version of them. An installed target
    // of any other type, and a missing one that became installed, leaves the queue; the rows of installed targets are written by SyncUpgrades.
    public const string Reconcile =
        $$"""
        WITH {{Prerequisites}},
        {{Intended}},
        wanted AS (
            SELECT "WorkId", "TargetKind", "TargetId" FROM intended WHERE NOT "Installed"
        ),
        held AS (
            SELECT "WorkId", "TargetKind", "TargetId" FROM intended WHERE "Installed" AND "MediaType" = ANY(@upgradeTypes)
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
          AND NOT EXISTS (SELECT 1 FROM held kept WHERE kept."TargetKind" = item."TargetKind" AND kept."TargetId" = item."TargetId")
        """;

    // The targets of one Work that Monitoring or a request wants and the library already holds: what an upgrade assessment looks at.
    public const string HeldTargets =
        $$"""
        WITH {{Prerequisites}},
        {{Intended}}
        SELECT "TargetKind", "TargetId" FROM intended WHERE "Installed"
        """;

    // @upgradeKinds and @upgradeIds: the held targets of @workId whose profile still wants a better version, as parallel arrays. They are queued, and every
    // other held target of the Work leaves the queue.
    public const string SyncUpgrades =
        $$"""
        WITH {{Prerequisites}},
        {{Intended}},
        upgradable AS (
            SELECT held."WorkId", held."TargetKind", held."TargetId"
            FROM intended held
            JOIN unnest(@upgradeKinds, @upgradeIds) AS chosen ("TargetKind", "TargetId") ON chosen."TargetKind" = held."TargetKind" AND chosen."TargetId" = held."TargetId"
            WHERE held."Installed"
        ),
        added AS (
            INSERT INTO "WantedItems" ("WorkId", "TargetKind", "TargetId", "CreatedAt")
            SELECT "WorkId", "TargetKind", "TargetId", @now FROM upgradable
            ON CONFLICT ("TargetKind", "TargetId") DO NOTHING
            RETURNING 1
        )
        DELETE FROM "WantedItems" item
        WHERE item."WorkId" = @workId
          AND EXISTS (SELECT 1 FROM intended held WHERE held."Installed" AND held."TargetKind" = item."TargetKind" AND held."TargetId" = item."TargetId")
          AND NOT EXISTS (SELECT 1 FROM upgradable still WHERE still."TargetKind" = item."TargetKind" AND still."TargetId" = item."TargetId")
        """;

    // @mediaType: the Work type, @after: the last Work of the previous page, @kind: the request kind name, @musicBrainz: its provider key, @limit,
    // @editions: whether the audio editions (an Audiobook request) or the other targets are listed.
    // Works with something still missing are listed, and so are Works that hold something upgradable and were never requested (an installed library
    // item); one that has a request is continued through it (see UpgradeWantedSource), so an upgrade never opens a second request.
    private const string RequestOf =
        """
        SELECT 1 FROM "AcquisitionRequests" request
        LEFT JOIN "WorkExternalIdentities" identity
               ON identity."WorkId" = work."Id" AND identity."Provider" = request."Provider" AND identity."ExternalId" = request."ExternalId"
        WHERE request."Kind" = @kind
          AND (request."WorkId" = work."Id"::text
               OR identity."WorkId" IS NOT NULL
               OR EXISTS (SELECT 1 FROM "MusicAlbums" album
                          WHERE album."WorkId" = work."Id" AND request."Provider" = @musicBrainz AND request."ExternalId" = album."MusicBrainzReleaseGroupId")
               OR EXISTS (SELECT 1 FROM "WorkSourceLinks" anime JOIN "AnimeMetadata" match ON match."AnimeId" = anime."SourceId"
                          WHERE anime."WorkId" = work."Id" AND anime."SourceKind" = 0 AND request."Provider" = match."Provider" AND request."ExternalId" = match."ExternalId"))
        """;

    public const string WorksWithoutOpenRequest =
        $$"""
        SELECT DISTINCT work."Id" AS "Value"
        FROM "WantedItems" item
        JOIN "Works" work ON work."Id" = item."WorkId" AND work."MediaType" = @mediaType
        LEFT JOIN "WorkEpisodes" episode ON item."TargetKind" = 1 AND episode."Id" = item."TargetId"
        WHERE work."Id" > @after
          AND (item."TargetKind" = 4) = @editions
          AND NOT EXISTS ({{RequestOf}} AND request."Status" IN ('pending', 'approved', 'searching', 'downloading', 'importing'))
          AND (NOT (CASE item."TargetKind" WHEN 1 THEN {{EpisodeInstalled}} WHEN 4 THEN {{AudiobookInstalled}} ELSE {{WorkInstalled}} END) OR NOT EXISTS ({{RequestOf}}))
        ORDER BY 1
        LIMIT @limit
        """;

    // The Works of one media type with something installed that Monitoring or a request wants, in id order: the ones an upgrade scan looks at.
    public const string HeldWorks =
        $$"""
        WITH {{Prerequisites}},
        {{Intended}}
        SELECT DISTINCT "WorkId" AS "Value" FROM intended WHERE "Installed" AND "MediaType" = @mediaType AND "WorkId" > @after ORDER BY 1 LIMIT @limit
        """;

    // The request that carried the Work last when it ended Completed, so a target that is wanted again continues it (its tried releases stay remembered).
    public const string CompletedRequestOf =
        """
        SELECT latest."Id" AS "Value"
        FROM (
            SELECT request."Id", request."Status"
            FROM "AcquisitionRequests" request
            JOIN "Works" work ON work."Id" = @workId
            LEFT JOIN "WorkExternalIdentities" identity
                   ON identity."WorkId" = work."Id" AND identity."Provider" = request."Provider" AND identity."ExternalId" = request."ExternalId"
            WHERE request."Kind" = @kind
              AND (request."WorkId" = work."Id"::text
                   OR identity."WorkId" IS NOT NULL
                   OR EXISTS (SELECT 1 FROM "MusicAlbums" album
                              WHERE album."WorkId" = work."Id" AND request."Provider" = @musicBrainz AND request."ExternalId" = album."MusicBrainzReleaseGroupId")
                   OR EXISTS (SELECT 1 FROM "WorkSourceLinks" anime JOIN "AnimeMetadata" match ON match."AnimeId" = anime."SourceId"
                              WHERE anime."WorkId" = work."Id" AND anime."SourceKind" = 0 AND request."Provider" = match."Provider" AND request."ExternalId" = match."ExternalId"))
            ORDER BY request."CreatedAt" DESC
            LIMIT 1
        ) latest
        WHERE latest."Status" = 'completed'
        """;

    public const string RecordWork =
        """
        INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt")
        SELECT @requestId, work."Id", 0, work."Id", @now FROM "Works" work WHERE work."Id" = @workId
        ON CONFLICT DO NOTHING
        """;

    public const string RecordEdition =
        """
        INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt")
        SELECT @requestId, edition."WorkId", 4, edition."Id", @now FROM "WorkEditions" edition WHERE edition."Id" = @editionId
        ON CONFLICT DO NOTHING
        """;

    public const string HasRequestTargets =
        """
        SELECT EXISTS (SELECT 1 FROM "RequestTargets" target WHERE target."RequestId" = @requestId) AS "Value"
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
