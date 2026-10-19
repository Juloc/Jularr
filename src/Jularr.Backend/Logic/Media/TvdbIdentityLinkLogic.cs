using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class TvdbIdentityLinkLogic
    {
        // Only the Work-level TVDB identity is written. Episode identity must be reviewed separately.
        public static async Task LinkTvdbSeriesIdentityAsync(this LogicContext ctx, TvdbIdentityLinkData data, CancellationToken ct)
        {
            // Recheck both Work version and external identity in the write transaction.
            // Without a local IMDb ID an administrator must explicitly confirm the TVDB choice.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id"
                FROM
                    "Works" AS work
                    LEFT JOIN "WorkExternalIdentities" AS imdb ON imdb."WorkId" = work."Id" AND imdb."Namespace" = 'imdb'
                WHERE
                    work."Id" = @Id
                    AND work."MediaTypeId" = 2
                    AND work."Version" = @Version
                    AND (
                        (@ImdbId::text IS NOT NULL AND imdb."Value" = @ImdbId)
                        OR (imdb."Id" IS NULL AND @ConfirmUnverified::boolean)
                    )
                    AND NOT EXISTS (
                        SELECT 1 FROM "WorkExternalIdentities" AS other
                        WHERE other."Namespace" = 'imdb'
                            AND other."Value" = @ImdbId
                            AND other."WorkId" <> @Id
                    )
                FOR UPDATE OF work;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.Version), "outdated_version");
            }

            // Fail closed on either a conflicting mapping on this Work or a TVDB ID owned by another Work.
            // ON CONFLICT is not consent to silently reassign the identity.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkExternalIdentities" ("WorkId", "Namespace", "Value", "IsManual")
                VALUES
                    (@Id, 'tvdb', @TvdbId::text, TRUE)
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            var inserted = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."WorkId" AS "Id"
                FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."WorkId" = @Id
                    AND identity."Namespace" = 'tvdb'
                    AND identity."Value" = @TvdbId::text;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.TvdbId), "in_use");
            }

            // An explicitly verified existing auto mapping becomes manual, so later refreshes
            // cannot silently supersede the administrator's choice.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkExternalIdentities"
                SET
                    "IsManual" = TRUE,
                    "UpdatedAt" = now()
                WHERE
                    "WorkId" = @Id
                    AND "Namespace" = 'tvdb'
                    AND "Value" = @TvdbId::text
                    AND NOT "IsManual";
                """;
            sql.Parameters.Add(data);
            var confirmed = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            if (inserted == 1 || confirmed == 1)
            {
                sql = new SqlStatement();
                sql.Statement =
                    """
                    UPDATE
                        "Works"
                    SET
                        "Version" = "Version" + 1,
                        "UpdatedAt" = now()
                    WHERE
                        "Id" = @Id;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }
        }
    }
}
