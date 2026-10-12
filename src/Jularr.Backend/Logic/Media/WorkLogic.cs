using Jularr.Backend.Data.Common;
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
                    "CanonicalTitle"
                ) VALUES (
                    @MediaTypeId,
                    @CanonicalTitle
                )
                RETURNING
                    "Id",
                    "MediaTypeId",
                    "CanonicalTitle",
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
            // Reject stale edits atomically.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE "Works"
                SET
                    "CanonicalTitle" = @CanonicalTitle,
                    "Version" = "Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    "Id" = @Id
                    AND "Version" = @Version;
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
    }
}
