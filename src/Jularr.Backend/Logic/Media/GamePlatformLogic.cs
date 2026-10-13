using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class GamePlatformLogic
    {
        public static async Task SaveWorkPlatformsAsync(this LogicContext ctx, WorkPlatformsData data, CancellationToken ct)
        {
            // Platforms are shared identities; a game lists the ones its provider names, each once.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH item AS (
                    SELECT DISTINCT ON (listed."Id")
                        listed."Id",
                        listed."Name",
                        listed."Abbreviation"
                    FROM
                        ROWS FROM (jsonb_to_recordset(@Platforms::jsonb) AS ("Id" integer, "Name" text, "Abbreviation" text)) WITH ORDINALITY AS listed ("Id", "Name", "Abbreviation", "Position")
                    ORDER BY
                        listed."Id",
                        listed."Position"
                ), platform AS (
                    INSERT INTO
                        "GamePlatforms" ("IgdbId", "Name", "Abbreviation")
                    SELECT
                        item."Id",
                        item."Name",
                        NULLIF(btrim(item."Abbreviation"), '')
                    FROM
                        item
                    ON CONFLICT ("IgdbId") DO UPDATE
                    SET
                        "Name" = EXCLUDED."Name",
                        "Abbreviation" = COALESCE(EXCLUDED."Abbreviation", "GamePlatforms"."Abbreviation")
                    RETURNING
                        "Id"
                ), removed AS (
                    DELETE FROM
                        "WorkPlatforms" AS listed
                    WHERE
                        listed."WorkId" = @WorkId
                        AND listed."GamePlatformId" NOT IN (SELECT "Id" FROM platform)
                )
                INSERT INTO
                    "WorkPlatforms" ("WorkId", "GamePlatformId")
                SELECT
                    @WorkId,
                    platform."Id"
                FROM
                    platform
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task TagGameReleaseAsync(this LogicContext ctx, GameReleaseData data, CancellationToken ct)
        {
            // The version a game file starts runs on the given platform, else on the one its verified dump or its folder names.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "GameReleases" ("WorkVersionId", "GamePlatformId")
                SELECT
                    asset."WorkVersionId",
                    platform."Id"
                FROM
                    "MediaAssets" AS asset
                    LEFT JOIN LATERAL (
                        SELECT
                            catalog."GamePlatformId"
                        FROM
                            "StoredFiles" AS file
                            JOIN "GameRoms" AS rom ON rom."Sha1" = file."Sha1"
                            JOIN "GameRomCatalogs" AS catalog ON catalog."Id" = rom."GameRomCatalogId"
                        WHERE
                            file."MediaAssetId" = asset."Id"
                        ORDER BY
                            catalog."Id"
                        LIMIT 1
                    ) AS dump ON TRUE
                    CROSS JOIN LATERAL (
                        SELECT
                            candidate."Id"
                        FROM
                            "GamePlatforms" AS candidate
                        WHERE
                            candidate."Id" = COALESCE(@GamePlatformId, dump."GamePlatformId")
                            OR COALESCE(@GamePlatformId, dump."GamePlatformId") IS NULL AND (lower(candidate."Name") = lower(@Folder) OR lower(candidate."Abbreviation") = lower(@Folder))
                        ORDER BY
                            candidate."Id"
                        LIMIT 1
                    ) AS platform
                WHERE
                    asset."Id" = @MediaAssetId
                ON CONFLICT ("WorkVersionId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
