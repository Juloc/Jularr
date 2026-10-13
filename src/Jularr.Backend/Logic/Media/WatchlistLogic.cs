using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WatchlistLogic
    {
        public static async Task UpdateWatchlistAsync(this LogicContext ctx, WatchlistData data, bool isWatchlisted, CancellationToken ct)
        {
            // Only Works the profile may open.
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
                    "ProfileWorks" AS visible
                    JOIN "Works" AS work ON work."Id" = visible."WorkId"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND work."Id" = @WorkId
                FOR SHARE OF work;
                """;
            sql.Parameters.Add(data);
            await ctx.ReadRequiredAsync<WorkData>(sql, ct);

            // Change personal state only.
            sql = new SqlStatement();
            if (isWatchlisted)
            {
                sql.Statement =
                    """
                    INSERT INTO "WatchlistEntries" (
                        "ProfileId",
                        "WorkId"
                    ) VALUES (
                        @ProfileId,
                        @WorkId
                    )
                    ON CONFLICT ("ProfileId", "WorkId") DO NOTHING;
                    """;
            }
            else
            {
                sql.Statement =
                    """
                    DELETE FROM "WatchlistEntries"
                    WHERE
                        "ProfileId" = @ProfileId
                        AND "WorkId" = @WorkId;
                    """;
            }

            sql.Parameters.Add(data);
            var affected = await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            if (affected is < 0 or > 1)
            {
                throw new InvalidOperationException("The personal list mutation affected unexpected rows.");
            }
        }
    }
}
