using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class FranchiseLogic
    {
        public static async Task SaveWorkFranchiseAsync(this LogicContext ctx, WorkFranchiseData data, CancellationToken ct)
        {
            // The provider decides; its series is kept once and renamed with it.
            var sql = new SqlStatement();
            if (data.TmdbCollectionId is null || data.Name is null)
            {
                sql.Statement =
                    """
                    UPDATE
                        "Works" AS work
                    SET
                        "FranchiseId" = NULL
                    WHERE
                        work."Id" = @WorkId
                        AND work."FranchiseId" IS NOT NULL;
                    """;
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
                return;
            }

            sql.Statement =
                """
                WITH franchise AS (
                    INSERT INTO
                        "Franchises" ("Name", "TmdbCollectionId")
                    VALUES
                        (@Name, @TmdbCollectionId)
                    ON CONFLICT ("TmdbCollectionId") DO UPDATE
                    SET
                        "Name" = EXCLUDED."Name"
                    RETURNING
                        "Id"
                )
                UPDATE
                    "Works" AS work
                SET
                    "FranchiseId" = franchise."Id"
                FROM
                    franchise
                WHERE
                    work."Id" = @WorkId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
