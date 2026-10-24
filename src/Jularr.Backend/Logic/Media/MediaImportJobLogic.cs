using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class MediaImportJobLogic
    {
        public static async Task<Guid> QueueMediaImportJobAsync(this LogicContext ctx, MediaImportJobQueueData data, CancellationToken ct)
        {
            // Only one active job per owner. A double click returns the original id,
            // never replaces the original immutable payload or silently queues another import.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH queued AS (
                    INSERT INTO "Operations" ("OperationTypeId", "RequestedByAccountId")
                    VALUES (6, @AccountId)
                    ON CONFLICT ("RequestedByAccountId")
                        WHERE "OperationTypeId" = 6 AND "OperationStatusTypeId" IN (1, 2)
                    DO UPDATE SET "RequestedByAccountId" = EXCLUDED."RequestedByAccountId"
                    RETURNING "Id"
                ), inserted AS (
                    INSERT INTO "MediaImportJobs" ("OperationId", "PublicId", "RequestedByAccountId", "Payload", "TotalCount")
                    SELECT "Id", @PublicId, @AccountId, @Payload::jsonb, @TotalCount FROM queued WHERE true
                    ON CONFLICT ("OperationId") DO NOTHING
                    RETURNING "PublicId"
                )
                SELECT coalesce(
                    (SELECT "PublicId" FROM inserted),
                    (SELECT job."PublicId" FROM "MediaImportJobs" AS job JOIN queued ON queued."Id" = job."OperationId")
                ) AS "JobId";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<MediaImportJobIdData>(sql, ct)).JobId;
        }

        public static Task<MediaImportJobIdData?> ReadLatestImportJobAsync(this LogicContext ctx, MediaImportJobOwnerData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT job."PublicId" AS "JobId"
                FROM "MediaImportJobs" AS job
                JOIN "Operations" AS operation ON operation."Id" = job."OperationId"
                WHERE job."RequestedByAccountId" = @AccountId
                ORDER BY (operation."OperationStatusTypeId" IN (1, 2)) DESC, job."OperationId" DESC
                LIMIT 1;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadOptionalAsync<MediaImportJobIdData>(sql, ct);
        }

        public static async Task<MediaImportJobStatusV1> ReadImportJobStatusAsync(this LogicContext ctx, MediaImportJobKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT job."PublicId" AS "JobId", operation."OperationStatusTypeId" AS "StatusTypeId",
                       job."TotalCount", operation."ProcessedCount", job."Sequence", operation."ErrorKey"
                FROM "MediaImportJobs" AS job
                JOIN "Operations" AS operation ON operation."Id" = job."OperationId"
                WHERE job."PublicId" = @JobId AND job."RequestedByAccountId" = @AccountId;
                """;
            sql.Parameters.Add(data);
            var header = await ctx.ReadRequiredAsync<MediaImportJobHeaderData>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT row."RowIndex" AS "Index", row."Title", row."OutcomeTypeId", row."WorkId", row."Searches"
                FROM "MediaImportJobRows" AS row
                JOIN "MediaImportJobs" AS job ON job."OperationId" = row."OperationId"
                WHERE job."PublicId" = @JobId AND job."RequestedByAccountId" = @AccountId
                ORDER BY row."RowIndex";
                """;
            sql.Parameters.Add(data);
            var rows = await ctx.ReadSql.ReadListAsync<MediaImportJobRowData>(sql, ct);
            return new MediaImportJobStatusV1(header.JobId, header.StatusTypeId, header.TotalCount, header.ProcessedCount,
                header.Sequence, header.ErrorKey, new(rows.Select(row => new MediaImportJobRowV1(row.Index, row.Title, row.OutcomeTypeId, row.WorkId, row.Searches))));
        }

        public static Task<MediaImportJobWorkData?> ReadImportJobWorkAsync(this LogicContext ctx, MediaImportJobOperationData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT job."OperationId", job."RequestedByAccountId", job."Payload"::text AS "Payload", job."TotalCount", job."Cursor"
                FROM "MediaImportJobs" AS job
                JOIN "Operations" AS operation ON operation."Id" = job."OperationId"
                WHERE job."OperationId" = @OperationId
                  AND operation."OperationTypeId" = 6 AND operation."OperationStatusTypeId" = 2;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadOptionalAsync<MediaImportJobWorkData>(sql, ct);
        }

        public static Task<MediaImportJobPermissionData> CheckImportJobOwnerAsync(this LogicContext ctx, MediaImportJobOwnerData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT (
                    EXISTS (SELECT 1 FROM "Accounts" WHERE "Id" = @AccountId AND "IsEnabled" = true AND "AccountRoleTypeId" IN (1, 3) FOR SHARE)
                    AND EXISTS (SELECT 1 FROM "InstanceModules" WHERE "Key" = 'Library' AND "IsEnabled" FOR SHARE)
                    AND EXISTS (SELECT 1 FROM "InstanceModules" WHERE "Key" = 'Requests' AND "IsEnabled" FOR SHARE)
                ) AS "Allowed";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadRequiredAsync<MediaImportJobPermissionData>(sql, ct);
        }

        public static async Task RecordImportJobRowAsync(this LogicContext ctx, MediaImportJobCheckpointData data, CancellationToken ct)
        {
            // The journal and cursor commit together, separately from the expensive provider I/O.
            // After a crash between Work commit and this checkpoint the next pass rereads the Work's
            // canonical identity; existing work imports are idempotent.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH checkpoint AS (
                    UPDATE "MediaImportJobs"
                    SET "Cursor" = "Cursor" + 1, "Sequence" = "Sequence" + 1, "UpdatedAt" = now()
                    WHERE "OperationId" = @OperationId AND "Cursor" = @RowIndex
                    RETURNING "OperationId"
                ), journal AS (
                    INSERT INTO "MediaImportJobRows" ("OperationId", "RowIndex", "OutcomeTypeId", "Title", "WorkId", "Searches", "UpdatedAt")
                    SELECT "OperationId", @RowIndex, @OutcomeTypeId, @Title, @WorkId, @Searches, now()
                    FROM checkpoint
                    ON CONFLICT ("OperationId", "RowIndex") DO NOTHING
                )
                UPDATE "Operations" SET "ProcessedCount" = @RowIndex + 1
                WHERE "Id" = @OperationId AND "OperationTypeId" = 6 AND "OperationStatusTypeId" = 2
                  AND EXISTS (SELECT 1 FROM checkpoint);
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new InvalidOperationException("The import job checkpoint was no longer current.");
            }
        }
    }
}
