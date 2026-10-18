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
            // Recheck both the current work version and local IMDb identity in the write transaction.
            // The caller independently verified that TVDB reports precisely this IMDb title identifier.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id"
                FROM
                    "Works" AS work
                    JOIN "WorkExternalIdentities" AS imdb ON imdb."WorkId" = work."Id" AND imdb."Namespace" = 'imdb'
                WHERE
                    work."Id" = @Id
                    AND work."MediaTypeId" = 2
                    AND work."Version" = @Version
                    AND imdb."Value" = @ImdbId
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

            if (inserted == 1)
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
