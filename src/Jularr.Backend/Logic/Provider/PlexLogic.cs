using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Logic.Provider
{
    internal static class PlexLogic
    {
        public static Task<PlexServerStateData?> ReadPlexServerAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."IsEnabled",
                    provider."BaseUrl",
                    provider."ProtectedSecret",
                    provider."ExternalId"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = 'plex';
                """;
            return ctx.ReadSql.ReadOptionalAsync<PlexServerStateData>(sql, ct);
        }

        public static async Task SavePlexServerAsync(this LogicContext ctx, PlexServerSaveData data, CancellationToken ct)
        {
            // Another server owns other items; links and sync state start over.
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."Namespace" = 'plex'
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "Providers" AS provider
                        WHERE
                            provider."Key" = 'plex'
                            AND provider."ExternalId" IS DISTINCT FROM @ExternalId
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            var update = new SqlStatement();
            update.Statement =
                """
                UPDATE
                    "Providers" AS provider
                SET
                    "BaseUrl" = @BaseUrl,
                    "ProtectedSecret" = CASE WHEN @BaseUrl::text IS NULL THEN NULL ELSE coalesce(@ProtectedSecret, provider."ProtectedSecret") END,
                    "SyncedAt" = CASE WHEN provider."ExternalId" IS NOT DISTINCT FROM @ExternalId THEN provider."SyncedAt" END,
                    "ExternalId" = @ExternalId
                WHERE
                    provider."Key" = 'plex';
                """;
            update.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(update, ct);
        }

        public static async Task<PlexItemsResultData> SavePlexItemsAsync(this LogicContext ctx, PlexItemsData data, CancellationToken ct)
        {
            // Keys of this page are assigned again below.
            var release = new SqlStatement();
            release.Statement =
                """
                DELETE FROM
                    "WorkExternalIdentities" AS identity
                USING
                    jsonb_to_recordset(@Items::jsonb) AS item ("Key" text)
                WHERE
                    identity."Namespace" = 'plex'
                    AND identity."Value" = item."Key";
                """;
            release.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(release, ct);

            // Match by provider ids; unknown titles become Works; one Plex item per Work.
            var matches = await ctx.MatchWorksByIdentityAsync(new WorkIdentityRowsData(data.Items), ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH item AS (
                    SELECT DISTINCT ON (item."Key")
                        item."Key",
                        item."MediaTypeId",
                        item."Title",
                        item."Year",
                        item."TmdbId",
                        item."ImdbId",
                        item."TvdbId"
                    FROM
                        jsonb_to_recordset(@Items::jsonb) AS item ("Key" text, "MediaTypeId" smallint, "Title" text, "Year" smallint, "TmdbId" text, "ImdbId" text, "TvdbId" text)
                    WHERE
                        item."TmdbId" IS NOT NULL
                        OR item."ImdbId" IS NOT NULL
                        OR item."TvdbId" IS NOT NULL
                ), matched AS (
                    SELECT
                        item.*,
                        match."WorkId"
                    FROM
                        item
                        LEFT JOIN jsonb_to_recordset(@Matches::jsonb) AS match ("Key" text, "WorkId" bigint) ON match."Key" = item."Key"
                ), created AS (
                    SELECT DISTINCT ON (matched."MediaTypeId", coalesce(matched."TmdbId", matched."ImdbId", matched."TvdbId"))
                        matched.*,
                        nextval(pg_get_serial_sequence('"Works"', 'Id')) AS "NewWorkId"
                    FROM
                        matched
                    WHERE
                        matched."WorkId" IS NULL
                    ORDER BY
                        matched."MediaTypeId",
                        coalesce(matched."TmdbId", matched."ImdbId", matched."TvdbId"),
                        matched."Key"
                ), works AS (
                    INSERT INTO
                        "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear")
                    SELECT
                        created."NewWorkId",
                        created."MediaTypeId",
                        created."Title",
                        created."Year"
                    FROM
                        created
                ), titles AS (
                    INSERT INTO
                        "WorkTitles" ("WorkId", "Title", "ProviderId", "IsManual")
                    SELECT
                        created."NewWorkId",
                        created."Title",
                        provider."Id",
                        FALSE
                    FROM
                        created
                        JOIN "Providers" AS provider ON provider."Key" = 'plex'
                ), identities AS (
                    INSERT INTO
                        "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                    SELECT
                        created."NewWorkId",
                        identity."Namespace",
                        identity."Value"
                    FROM
                        created
                        CROSS JOIN LATERAL (
                            VALUES
                                (CASE created."MediaTypeId" WHEN 1 THEN 'tmdb_movie' ELSE 'tmdb_tv' END, created."TmdbId"),
                                ('imdb', created."ImdbId"),
                                ('tvdb', created."TvdbId")
                        ) AS identity ("Namespace", "Value")
                    WHERE
                        identity."Value" IS NOT NULL
                    ON CONFLICT DO NOTHING
                ), linked AS (
                    SELECT DISTINCT ON (target."WorkId")
                        target."WorkId",
                        target."Key"
                    FROM (
                        SELECT
                            matched."WorkId",
                            matched."Key"
                        FROM
                            matched
                        WHERE
                            matched."WorkId" IS NOT NULL
                        UNION ALL
                        SELECT
                            created."NewWorkId",
                            created."Key"
                        FROM
                            created
                    ) AS target
                    ORDER BY
                        target."WorkId",
                        target."Key"
                ), plex AS (
                    INSERT INTO
                        "WorkExternalIdentities" ("WorkId", "Namespace", "Value")
                    SELECT
                        linked."WorkId",
                        'plex',
                        linked."Key"
                    FROM
                        linked
                    ON CONFLICT ("WorkId", "Namespace") DO UPDATE
                    SET
                        "Value" = EXCLUDED."Value",
                        "UpdatedAt" = now()
                    RETURNING
                        1
                )
                SELECT
                    (SELECT count(*) FROM plex)::integer AS "LinkedCount",
                    (SELECT count(*) FROM created)::integer AS "CreatedCount";
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(matches);
            return await ctx.ReadRequiredAsync<PlexItemsResultData>(sql, ct);
        }

        public static async Task CompletePlexSyncAsync(this LogicContext ctx, PlexKeysData data, CancellationToken ct)
        {
            // Only a complete pass may unlink what Plex no longer has.
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."Namespace" = 'plex'
                    AND NOT identity."Value" = ANY (@RatingKeys);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            var synced = new SqlStatement();
            synced.Statement =
                """
                UPDATE
                    "Providers" AS provider
                SET
                    "SyncedAt" = now()
                WHERE
                    provider."Key" = 'plex';
                """;
            await ctx.ExecuteAsyncForAffectedRows(synced, ct);
        }
    }
}
