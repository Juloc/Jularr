using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class TvdbEpisodeIdentityLogic
    {
        public static async Task LinkTvdbEpisodeIdentityAsync(this LogicContext ctx, TvdbEpisodeIdentityData data, CancellationToken ct)
        {
            // Verify the same series, unit and version in the write transaction after the remote API call.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id"
                FROM
                    "Works" AS work
                    JOIN "WorkExternalIdentities" AS identity
                        ON identity."WorkId" = work."Id" AND identity."Namespace" = 'tvdb' AND identity."Value" = @TvdbId::text
                    JOIN "WorkUnits" AS episode
                        ON episode."WorkId" = work."Id" AND episode."Id" = @WorkUnitId AND episode."WorkUnitTypeId" = 2
                WHERE
                    work."Id" = @WorkId
                    AND work."MediaTypeId" = 2
                    AND work."Version" = @Version
                FOR UPDATE OF work;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.WorkUnitId), "unknown_reference");
            }

            // The same local episode must not acquire two different TVDB identities.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."WorkUnitId" AS "Id"
                FROM
                    "WorkUnitExternalIdentities" AS identity
                WHERE
                    identity."WorkUnitId" = @WorkUnitId
                    AND identity."Namespace" = 'tvdb'
                    AND identity."Value" <> @TvdbEpisodeId::text;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) is not null)
            {
                throw new DataValidationException(nameof(data.TvdbEpisodeId), "in_use");
            }

            // Confirming a provider identity is not permission to alter episode numbering or assets.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset", "IsManual")
                VALUES
                    (@WorkUnitId, 'tvdb', @TvdbEpisodeId::text, 0, TRUE)
                ON CONFLICT DO NOTHING;
                """;
            sql.Parameters.Add(data);
            var inserted = await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."WorkUnitId" AS "Id"
                FROM
                    "WorkUnitExternalIdentities" AS identity
                WHERE
                    identity."Namespace" = 'tvdb'
                    AND identity."Value" = @TvdbEpisodeId::text
                    AND identity."WorkUnitId" = @WorkUnitId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.TvdbEpisodeId), "in_use");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "WorkUnitExternalIdentities"
                SET
                    "IsManual" = TRUE,
                    "UpdatedAt" = now()
                WHERE
                    "WorkUnitId" = @WorkUnitId
                    AND "Namespace" = 'tvdb'
                    AND "Value" = @TvdbEpisodeId::text
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
                        "Id" = @WorkId;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }
        }
    }
}
