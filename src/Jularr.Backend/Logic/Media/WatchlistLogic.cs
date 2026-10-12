using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WatchlistLogic
    {
        public static async Task UpdateWatchlistAsync(this LogicContext context, WatchlistData data, bool isWatchlisted, CancellationToken cancellationToken)
        {
            // Verify canonical target.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    work."Version"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId
                FOR SHARE OF work;
                """;
            sql.Parameters.Add(data);
            await context.ReadRequiredAsync<WorkData>(sql, cancellationToken);

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
            var affected = await context.ExecuteAsyncForAffectedRows(sql, cancellationToken);
            if (affected is < 0 or > 1)
            {
                throw new InvalidOperationException("The personal list mutation affected unexpected rows.");
            }
        }
    }
}
