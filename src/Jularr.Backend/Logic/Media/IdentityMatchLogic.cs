using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class IdentityMatchLogic
    {
        public static async Task SaveIdentityMatchAsync(this LogicContext ctx, IdentityMatchResultData data, CancellationToken ct)
        {
            // An entry another Work already holds stays there; the match then needs a person.
            var status = data.IdentityMatchStatusTypeId;
            SqlStatement sql;
            if (data.Value is not null)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    INSERT INTO
                        "WorkExternalIdentities" ("WorkId", "Namespace", "Value", "IsManual")
                    VALUES
                        (@WorkId, @Namespace, @Value, @IsManual)
                    ON CONFLICT DO NOTHING;
                    """;
                sql.Parameters.Add(data);
                if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
                {
                    status = IdentityMatchStatusType.Review;
                }
            }

            // Season entries land on the Work's regular seasons by number.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset", "IsManual")
                SELECT
                    season."Id",
                    @Namespace,
                    item."Value",
                    item."Offset",
                    @IsManual
                FROM
                    jsonb_to_recordset(@Seasons::jsonb) AS item ("Season" integer, "Value" text, "Offset" integer)
                    JOIN "WorkUnits" AS season ON season."WorkId" = @WorkId
                        AND season."WorkUnitTypeId" = 1
                        AND season."ParentWorkUnitId" IS NULL
                        AND season."Number" = item."Season"
                ON CONFLICT ("Namespace", "Value") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Season entries another Work holds leave this one waiting for a person.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (
                        SELECT
                            1
                        FROM
                            jsonb_to_recordset(@Seasons::jsonb) AS item ("Season" integer, "Value" text, "Offset" integer)
                        WHERE
                            NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WorkUnitExternalIdentities" AS mapped
                                    JOIN "WorkUnits" AS season ON season."Id" = mapped."WorkUnitId"
                                WHERE
                                    mapped."Namespace" = @Namespace
                                    AND mapped."Value" = item."Value"
                                    AND season."WorkId" = @WorkId
                            )
                    );
                """;
            sql.Parameters.Add(data);
            if ((bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!)
            {
                status = IdentityMatchStatusType.Review;
            }

            // The latest pass replaces the previous one.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkIdentityMatches" ("WorkId", "Namespace", "IdentityMatchStatusTypeId", "Candidates", "CheckedAt")
                VALUES
                    (@WorkId, @Namespace, @IdentityMatchStatusTypeId, @Candidates::jsonb, now())
                ON CONFLICT ("WorkId", "Namespace") DO UPDATE
                SET
                    "IdentityMatchStatusTypeId" = EXCLUDED."IdentityMatchStatusTypeId",
                    "Candidates" = EXCLUDED."Candidates",
                    "CheckedAt" = EXCLUDED."CheckedAt",
                    "RelationsCheckedAt" = NULL;
                """;
            sql.Parameters.Add(status == data.IdentityMatchStatusTypeId ? data
                : new IdentityMatchResultData(data.WorkId, data.Namespace, status, data.Candidates, data.Value, data.Seasons, data.IsManual));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // A season that waited as a Work of its own goes into the series now mapping it.
            await ctx.MergeAnimePlaceholdersAsync(data.WorkId, ct);
        }

        // An anime season no series held yet was a Work of its own: a series, anime, with only its AniList entry and nothing of its own.
        // Once a season of the series holds that entry, favorites, collections, relations, list places, requests, wanted entries and progress
        // notifications move to the series (a list place at that season), what the series already has stays, and the season's Work goes.
        public static async Task<int> MergeAnimePlaceholdersAsync(this LogicContext ctx, long seriesWorkId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    placeholder."Id"
                FROM
                    "WorkUnits" AS season
                    JOIN "WorkUnitExternalIdentities" AS mapped ON mapped."WorkUnitId" = season."Id" AND mapped."Namespace" = 'anilist'
                    JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = 'anilist' AND identity."Value" = mapped."Value"
                    JOIN "Works" AS placeholder ON placeholder."Id" = identity."WorkId"
                WHERE
                    season."WorkId" = @Id
                    AND season."WorkUnitTypeId" = 1
                    AND placeholder."Id" <> @Id
                    AND placeholder."MediaTypeId" = 2
                    AND placeholder."MediaSubtypeId" = 1
                    AND NOT EXISTS (SELECT 1 FROM "WorkUnits" AS unit WHERE unit."WorkId" = placeholder."Id")
                    AND NOT EXISTS (SELECT 1 FROM "WorkEditions" AS edition WHERE edition."WorkId" = placeholder."Id")
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkExternalIdentities" AS other
                        WHERE
                            other."WorkId" = placeholder."Id"
                            AND other."Namespace" <> 'anilist'
                    )
                ORDER BY
                    placeholder."Id"
                LIMIT 100
                FOR UPDATE OF placeholder;
                """;
            sql.Parameters.Add(new WorkIdData(seriesWorkId));
            var placeholders = await ctx.Sql.ReadListAsync<WorkIdData>(sql, ct);
            if (placeholders.Count == 0)
            {
                return 0;
            }

            // Personal lists keep the series once.
            var merge = new AnimePlaceholderMergeData(seriesWorkId, [.. placeholders.Select(placeholder => placeholder.Id)]);
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WatchlistEntries" ("ProfileId", "WorkId", "CreatedAt")
                SELECT
                    entry."ProfileId",
                    @SeriesWorkId,
                    min(entry."CreatedAt")
                FROM
                    "WatchlistEntries" AS entry
                WHERE
                    entry."WorkId" = ANY(@PlaceholderWorkIds)
                GROUP BY
                    entry."ProfileId"
                ON CONFLICT DO NOTHING;

                INSERT INTO
                    "CollectionWorks" ("CollectionId", "WorkId", "Position", "AddedAt")
                SELECT
                    member."CollectionId",
                    @SeriesWorkId,
                    min(member."Position"),
                    min(member."AddedAt")
                FROM
                    "CollectionWorks" AS member
                WHERE
                    member."WorkId" = ANY(@PlaceholderWorkIds)
                GROUP BY
                    member."CollectionId"
                ON CONFLICT DO NOTHING;

                UPDATE
                    "MediaProgress" AS progress
                SET
                    "WorkId" = @SeriesWorkId
                WHERE
                    progress."Id" IN (
                        SELECT DISTINCT ON (moved."ProfileId")
                            moved."Id"
                        FROM
                            "MediaProgress" AS moved
                        WHERE
                            moved."WorkId" = ANY(@PlaceholderWorkIds)
                            AND moved."WorkUnitId" IS NULL
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "MediaProgress" AS kept
                                WHERE
                                    kept."ProfileId" = moved."ProfileId"
                                    AND kept."WorkId" = @SeriesWorkId
                                    AND kept."WorkUnitId" IS NULL
                            )
                        ORDER BY
                            moved."ProfileId",
                            moved."UpdatedAt" DESC
                    );
                """;
            sql.Parameters.Add(merge);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Relations name the series; one that now names itself goes, of ones that now say the same one stays: a person's, a hidden one, one that already named the series.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH moved AS (
                    SELECT
                        relation."Id",
                        CASE WHEN relation."WorkId" = ANY(@PlaceholderWorkIds) THEN @SeriesWorkId ELSE relation."WorkId" END AS "WorkId",
                        CASE WHEN relation."RelatedWorkId" = ANY(@PlaceholderWorkIds) THEN @SeriesWorkId ELSE relation."RelatedWorkId" END AS "RelatedWorkId",
                        relation."WorkRelationTypeId",
                        relation."SourceKey",
                        relation."IsSuppressed",
                        relation."WorkId" = ANY(@PlaceholderWorkIds) OR relation."RelatedWorkId" = ANY(@PlaceholderWorkIds) AS "IsMoved"
                    FROM
                        "WorkRelations" AS relation
                    WHERE
                        relation."WorkId" = ANY(@PlaceholderWorkIds || @SeriesWorkId)
                        OR relation."RelatedWorkId" = ANY(@PlaceholderWorkIds || @SeriesWorkId)
                ), ranked AS (
                    SELECT
                        moved."Id",
                        moved."WorkId" = moved."RelatedWorkId" AS "IsSelf",
                        row_number() OVER (
                            PARTITION BY moved."WorkId", moved."RelatedWorkId", moved."WorkRelationTypeId"
                            ORDER BY moved."SourceKey" = 'manual' DESC, moved."IsSuppressed" DESC, NOT moved."IsMoved" DESC, moved."Id"
                        ) AS "Place"
                    FROM
                        moved
                )
                DELETE FROM
                    "WorkRelations" AS relation
                USING
                    ranked
                WHERE
                    relation."Id" = ranked."Id"
                    AND (ranked."IsSelf" OR ranked."Place" > 1);

                UPDATE
                    "WorkRelations" AS relation
                SET
                    "WorkId" = @SeriesWorkId
                WHERE
                    relation."WorkId" = ANY(@PlaceholderWorkIds);

                UPDATE
                    "WorkRelations" AS relation
                SET
                    "RelatedWorkId" = @SeriesWorkId
                WHERE
                    relation."RelatedWorkId" = ANY(@PlaceholderWorkIds);
                """;
            sql.Parameters.Add(merge);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // A list shows the series at the season instead; a place the series already has at that season stays the only one.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH moved AS (
                    SELECT
                        item."FeedId",
                        item."Position",
                        season."Number"::smallint AS "SeasonNumber",
                        row_number() OVER (PARTITION BY item."FeedId", season."Number" ORDER BY item."Position") AS "Place"
                    FROM
                        "ExternalWorkFeedItems" AS item
                        JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = item."WorkId" AND identity."Namespace" = 'anilist'
                        JOIN "WorkUnitExternalIdentities" AS mapped ON mapped."Namespace" = 'anilist' AND mapped."Value" = identity."Value"
                        JOIN "WorkUnits" AS season ON season."Id" = mapped."WorkUnitId" AND season."WorkId" = @SeriesWorkId
                    WHERE
                        item."WorkId" = ANY(@PlaceholderWorkIds)
                ), dropped AS (
                    DELETE FROM
                        "ExternalWorkFeedItems" AS item
                    USING
                        moved
                    WHERE
                        item."FeedId" = moved."FeedId"
                        AND item."Position" = moved."Position"
                        AND (moved."Place" > 1 OR EXISTS (
                            SELECT
                                1
                            FROM
                                "ExternalWorkFeedItems" AS kept
                            WHERE
                                kept."FeedId" = moved."FeedId"
                                AND kept."WorkId" = @SeriesWorkId
                                AND kept."SeasonNumber" IS NOT DISTINCT FROM moved."SeasonNumber"
                        ))
                    RETURNING
                        item."FeedId",
                        item."Position"
                )
                UPDATE
                    "ExternalWorkFeedItems" AS item
                SET
                    "WorkId" = @SeriesWorkId,
                    "SeasonNumber" = moved."SeasonNumber"
                FROM
                    moved
                WHERE
                    item."FeedId" = moved."FeedId"
                    AND item."Position" = moved."Position"
                    AND NOT EXISTS (SELECT 1 FROM dropped WHERE dropped."FeedId" = moved."FeedId" AND dropped."Position" = moved."Position");
                """;
            sql.Parameters.Add(merge);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Requests name the series; an open one the series already has for the same profile, language and platform is the same wish and is cancelled.
            // The series' own wanted entry takes the requests of the season's entry, else that entry becomes the series'.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionRequests" AS request
                SET
                    "AcquisitionRequestStatusTypeId" = 9,
                    "Version" = request."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    request."WorkId" = ANY(@PlaceholderWorkIds)
                    AND request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5
                    AND request."RequestedByProfileId" IS NOT NULL
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "AcquisitionRequests" AS kept
                        WHERE
                            kept."WorkId" = @SeriesWorkId
                            AND kept."RequestedByProfileId" = request."RequestedByProfileId"
                            AND kept."UiLocaleId" = request."UiLocaleId"
                            AND kept."GamePlatformId" IS NOT DISTINCT FROM request."GamePlatformId"
                            AND kept."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5
                    );

                UPDATE
                    "AcquisitionRequests" AS request
                SET
                    "AcquisitionRequestStatusTypeId" = 9,
                    "Version" = request."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    request."Id" IN (
                        SELECT
                            duplicate."Id"
                        FROM
                            (
                                SELECT
                                    open."Id",
                                    row_number() OVER (PARTITION BY open."RequestedByProfileId", open."UiLocaleId", open."GamePlatformId" ORDER BY open."CreatedAt", open."Id") AS "Place"
                                FROM
                                    "AcquisitionRequests" AS open
                                WHERE
                                    open."WorkId" = ANY(@PlaceholderWorkIds)
                                    AND open."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5
                                    AND open."RequestedByProfileId" IS NOT NULL
                            ) AS duplicate
                        WHERE
                            duplicate."Place" > 1
                    );

                UPDATE
                    "AcquisitionRequests" AS request
                SET
                    "WorkId" = @SeriesWorkId,
                    "UpdatedAt" = now()
                WHERE
                    request."WorkId" = ANY(@PlaceholderWorkIds);

                UPDATE
                    "WantedItems" AS wanted
                SET
                    "WorkId" = @SeriesWorkId,
                    "Version" = wanted."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    wanted."Id" IN (
                        SELECT DISTINCT ON (moved."GamePlatformId")
                            moved."Id"
                        FROM
                            "WantedItems" AS moved
                        WHERE
                            moved."WorkId" = ANY(@PlaceholderWorkIds)
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WantedItems" AS kept
                                WHERE
                                    kept."WorkId" = @SeriesWorkId
                                    AND kept."GamePlatformId" IS NOT DISTINCT FROM moved."GamePlatformId"
                            )
                        ORDER BY
                            moved."GamePlatformId",
                            moved."Id"
                    );

                INSERT INTO
                    "RequestTargets" ("AcquisitionRequestId", "WantedItemId")
                SELECT
                    target."AcquisitionRequestId",
                    kept."Id"
                FROM
                    "WantedItems" AS wanted
                    JOIN "RequestTargets" AS target ON target."WantedItemId" = wanted."Id"
                    JOIN "WantedItems" AS kept ON kept."WorkId" = @SeriesWorkId AND kept."GamePlatformId" IS NOT DISTINCT FROM wanted."GamePlatformId"
                WHERE
                    wanted."WorkId" = ANY(@PlaceholderWorkIds)
                ON CONFLICT DO NOTHING;

                DELETE FROM
                    "RequestTargets" AS target
                USING
                    "WantedItems" AS wanted
                WHERE
                    target."WantedItemId" = wanted."Id"
                    AND wanted."WorkId" = ANY(@PlaceholderWorkIds);

                DELETE FROM
                    "WantedItems" AS wanted
                WHERE
                    wanted."WorkId" = ANY(@PlaceholderWorkIds);

                DELETE FROM
                    "Notifications" AS notification
                WHERE
                    notification."Id" IN (
                        SELECT
                            duplicate."Id"
                        FROM
                            (
                                SELECT
                                    unread."Id",
                                    row_number() OVER (PARTITION BY unread."ProfileId", unread."NotificationTypeId", unread."Subject" ORDER BY unread."CreatedAt", unread."Id") AS "Place",
                                    EXISTS (
                                        SELECT
                                            1
                                        FROM
                                            "Notifications" AS kept
                                        WHERE
                                            kept."WorkId" = @SeriesWorkId
                                            AND kept."ProfileId" = unread."ProfileId"
                                            AND kept."NotificationTypeId" = unread."NotificationTypeId"
                                            AND kept."Subject" = unread."Subject"
                                            AND kept."ReadAt" IS NULL
                                    ) AS "IsKept"
                                FROM
                                    "Notifications" AS unread
                                WHERE
                                    unread."WorkId" = ANY(@PlaceholderWorkIds)
                                    AND unread."ReadAt" IS NULL
                            ) AS duplicate
                        WHERE
                            duplicate."Place" > 1
                            OR duplicate."IsKept"
                    );

                UPDATE
                    "Notifications" AS notification
                SET
                    "WorkId" = @SeriesWorkId
                WHERE
                    notification."WorkId" = ANY(@PlaceholderWorkIds);

                DELETE FROM
                    "Works" AS work
                WHERE
                    work."Id" = ANY(@PlaceholderWorkIds);
                """;
            sql.Parameters.Add(merge);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return placeholders.Count;
        }

        // Matched Works whose relations were never read or are a day old, the oldest first, with the AniList entries they hold.
        public static Task<IReadOnlyList<IdentityRelationTargetData>> ReadRelationTargetsAsync(this LogicContext ctx, int limit, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    ARRAY(
                        SELECT
                            identity."Value"::integer
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" = 'anilist'
                        UNION
                        SELECT
                            mapped."Value"::integer
                        FROM
                            "WorkUnitExternalIdentities" AS mapped
                            JOIN "WorkUnits" AS season ON season."Id" = mapped."WorkUnitId"
                        WHERE
                            season."WorkId" = work."Id"
                            AND mapped."Namespace" = 'anilist'
                        ORDER BY
                            1
                    ) AS "ProviderIds"
                FROM
                    "WorkIdentityMatches" AS match
                    JOIN "Works" AS work ON work."Id" = match."WorkId"
                WHERE
                    match."Namespace" = 'anilist'
                    AND match."IdentityMatchStatusTypeId" = 1
                    AND (match."RelationsCheckedAt" IS NULL OR match."RelationsCheckedAt" < now() - interval '1 day')
                ORDER BY
                    match."RelationsCheckedAt" NULLS FIRST,
                    work."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(new IdentityMatchQueryData(null, limit));
            return ctx.ReadSql.ReadListAsync<IdentityRelationTargetData>(sql, ct);
        }

        // The relations of a matched Work are read for now.
        public static async Task MarkRelationsCheckedAsync(this LogicContext ctx, long workId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkIdentityMatches"
                SET
                    "RelationsCheckedAt" = now()
                WHERE
                    "WorkId" = @Id
                    AND "Namespace" = 'anilist';
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
