using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WorkLogic
    {
        public static async Task<WorkData> CreateWorkAsync(this LogicContext ctx, WorkCreationData data, CancellationToken ct)
        {
            // Create canonical identity.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "Works" (
                    "MediaTypeId",
                    "MediaSubtypeId",
                    "IsMediaSubtypeManual",
                    "CanonicalTitle",
                    "ReleaseYear"
                ) VALUES (
                    @MediaTypeId,
                    @MediaSubtypeId,
                    @IsMediaSubtypeManual,
                    @CanonicalTitle,
                    @ReleaseYear
                )
                RETURNING
                    "Id",
                    "MediaTypeId",
                    "MediaSubtypeId",
                    "IsMediaSubtypeManual",
                    "CanonicalTitle",
                    "ReleaseYear"::integer AS "ReleaseYear",
                    "Version";
                """;
            sql.Parameters.Add(data);
            var work = await ctx.ReadRequiredAsync<WorkData>(sql, ct);

            // Retain title provenance.
            await ctx.WriteManualTitleAsync(new WorkUpdateData(work.Id, work.CanonicalTitle, work.Version), ct);
            return work;
        }

        public static async Task UpdateWorkAsync(this LogicContext ctx, WorkUpdateData data, CancellationToken ct)
        {
            // The subtype must belong to the Work's type.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."IsMediaSubtypeManual",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Version"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @Id
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var work = await ctx.ReadOptionalAsync<WorkData>(sql, ct) ?? throw new KeyNotFoundException("The work was not found.");
            if (!MediaSubtypes.Fits(work.MediaTypeId, data.MediaSubtypeId))
            {
                throw new DataValidationException(nameof(data.MediaSubtypeId), "unknown_catalog_value");
            }

            // Reject stale edits; a changed subtype becomes a manual choice.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "Works" AS work
                SET
                    "CanonicalTitle" = @CanonicalTitle,
                    "ReleaseYear" = @ReleaseYear,
                    "IsMediaSubtypeManual" = work."IsMediaSubtypeManual" OR work."MediaSubtypeId" IS DISTINCT FROM @MediaSubtypeId,
                    "MediaSubtypeId" = @MediaSubtypeId,
                    "Version" = work."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    work."Id" = @Id
                    AND work."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }

            // Update manual source only.
            await ctx.WriteManualTitleAsync(data, ct);
        }

        private static async Task WriteManualTitleAsync(this LogicContext ctx, WorkUpdateData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "WorkTitles" (
                    "WorkId",
                    "Title",
                    "IsManual"
                ) VALUES (
                    @Id,
                    @CanonicalTitle,
                    TRUE
                )
                ON CONFLICT ("WorkId", "UiLocaleId") WHERE "IsManual"
                DO UPDATE SET
                    "Title" = EXCLUDED."Title",
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The manual title was not saved.");
            }
        }

        // The one way a provider's item finds its Work: TMDB first, then IMDB and TVDB, always of the same media type.
        public static async Task<WorkIdentityMatchesData> MatchWorksByIdentityAsync(this LogicContext ctx, WorkIdentityRowsData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT DISTINCT ON (item."Key")
                    item."Key",
                    identity."WorkId"
                FROM
                    jsonb_to_recordset(@Rows::jsonb) AS item ("Key" text, "MediaTypeId" smallint, "TmdbId" text, "ImdbId" text, "TvdbId" text)
                    JOIN "WorkExternalIdentities" AS identity ON identity."Namespace" = CASE item."MediaTypeId" WHEN 1 THEN 'tmdb_movie' ELSE 'tmdb_tv' END AND identity."Value" = item."TmdbId"
                        OR identity."Namespace" = 'imdb' AND identity."Value" = item."ImdbId"
                        OR identity."Namespace" = 'tvdb' AND identity."Value" = item."TvdbId"
                    JOIN "Works" AS work ON work."Id" = identity."WorkId" AND work."MediaTypeId" = item."MediaTypeId"
                ORDER BY
                    item."Key",
                    identity."Namespace" LIKE 'tmdb%' DESC,
                    identity."WorkId";
                """;
            sql.Parameters.Add(data);
            return new WorkIdentityMatchesData(JsonSerializer.Serialize(await ctx.ReadSql.ReadListAsync<WorkIdentityMatchData>(sql, ct)));
        }
    }
}
