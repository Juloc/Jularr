using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class WorkLogic
    {
        public static async Task<WorkData> CreateWorkAsync(this LogicContext context, WorkCreationData data, CancellationToken cancellationToken)
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
            var work = await context.ReadRequiredAsync<WorkData>(sql, cancellationToken);

            // Retain title provenance.
            await context.WriteManualTitleAsync(new WorkUpdateData(work.Id, work.CanonicalTitle, work.Version), cancellationToken);
            return work;
        }

        public static async Task UpdateWorkAsync(this LogicContext context, WorkUpdateData data, CancellationToken cancellationToken)
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
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }

            // Update manual source only.
            await context.WriteManualTitleAsync(data, cancellationToken);
        }

        private static async Task WriteManualTitleAsync(this LogicContext context, WorkUpdateData data, CancellationToken cancellationToken)
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
            if (await context.ExecuteAsyncForAffectedRows(sql, cancellationToken) != 1)
            {
                throw new InvalidOperationException("The manual title was not saved.");
            }
        }
    }
}
