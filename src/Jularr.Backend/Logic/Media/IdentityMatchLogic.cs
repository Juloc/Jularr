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

            // Refuse a whole proposed season mapping if an ID is already held by a different unit or offset.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM jsonb_to_recordset(@Seasons::jsonb) AS item ("Season" integer, "Value" text, "Offset" integer)
                    LEFT JOIN "WorkUnits" AS season ON season."WorkId" = @WorkId
                        AND season."WorkUnitTypeId" = 1
                        AND season."ParentWorkUnitId" IS NULL
                        AND season."Number" = item."Season"
                    LEFT JOIN "WorkUnitExternalIdentities" AS mapped ON mapped."Namespace" = @Namespace
                        AND mapped."Value" = item."Value"
                    WHERE season."Id" IS NULL
                        OR (mapped."Id" IS NOT NULL
                            AND (mapped."WorkUnitId" <> season."Id" OR mapped."EpisodeOffset" <> item."Offset"))
                );
                """;
            sql.Parameters.Add(data);
            if ((bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!)
            {
                status = IdentityMatchStatusType.Review;
            }

            if (status == IdentityMatchStatusType.Matched)
            {
                // No partial imports: only a complete, unopposed proposal is written.
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

                // A concurrent conflicting entry also leaves the match awaiting review.
                sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM jsonb_to_recordset(@Seasons::jsonb) AS item ("Season" integer, "Value" text, "Offset" integer)
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM "WorkUnitExternalIdentities" AS mapped
                                JOIN "WorkUnits" AS season ON season."Id" = mapped."WorkUnitId"
                            WHERE mapped."Namespace" = @Namespace
                                AND mapped."Value" = item."Value"
                                AND mapped."EpisodeOffset" = item."Offset"
                                AND season."WorkId" = @WorkId
                                AND season."Number" = item."Season"
                        )
                    );
                    """;
                sql.Parameters.Add(data);
                if ((bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!)
                {
                    status = IdentityMatchStatusType.Review;
                }
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
