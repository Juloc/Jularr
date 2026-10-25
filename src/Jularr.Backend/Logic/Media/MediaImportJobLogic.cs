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
                )
                INSERT INTO "MediaImportJobs" ("OperationId", "PublicId", "RequestedByAccountId", "Payload", "TotalCount")
                SELECT "Id", @PublicId, @AccountId, @Payload::jsonb, @TotalCount FROM queued WHERE true
                ON CONFLICT ("OperationId") DO UPDATE
                    SET "OperationId" = EXCLUDED."OperationId"
                RETURNING "PublicId" AS "JobId";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<MediaImportJobIdData>(sql, ct)).JobId;
        }

        public static async Task InitializeImportJobRowsAsync(this LogicContext ctx, MediaImportJobIdData data, CancellationToken ct)
        {
            // The normalized immutable snapshot owns all row titles. A duplicate
            // submit can never modify another pending job's choices or progress.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "MediaImportJobRows" ("OperationId", "RowIndex", "Title")
                SELECT job."OperationId", (item."Ordinality" - 1)::integer, left(item."Element"->>'title', 500)
                FROM "MediaImportJobs" AS job
                CROSS JOIN LATERAL jsonb_array_elements((job."Payload"->>'Json')::jsonb->'items')
                    WITH ORDINALITY AS item("Element", "Ordinality")
                WHERE job."PublicId" = @JobId
                ON CONFLICT ("OperationId", "RowIndex") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task MarkImportJobStageAsync(this LogicContext ctx, MediaImportJobStageData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH changed AS (
                    UPDATE "MediaImportJobRows"
                    SET "Stage" = @Stage, "UpdatedAt" = now(),
                        "Source" = COALESCE(@Source, "Source"),
                        "Query" = COALESCE(@Query, "Query"),
                        "Detail" = COALESCE(@Detail, "Detail"),
                        "CandidateCount" = COALESCE(@CandidateCount, "CandidateCount"),
                        "Attempt" = GREATEST("Attempt", @Attempt),
                        "History" = (
                            SELECT COALESCE(jsonb_agg(recent.item ORDER BY recent.ordinal), '[]'::jsonb)
                            FROM (
                                SELECT value AS item, ordinality AS ordinal
                                FROM jsonb_array_elements("MediaImportJobRows"."History") WITH ORDINALITY
                                ORDER BY ordinality DESC LIMIT 19
                            ) AS recent
                        ) || jsonb_build_array(jsonb_build_object(
                            'stage', @Stage, 'source', @Source, 'query', @Query,
                            'detail', @Detail, 'candidateCount', @CandidateCount,
                            'attempt', @Attempt, 'at', now()))
                    WHERE "OperationId" = @OperationId AND "RowIndex" = @RowIndex
                      AND "OutcomeTypeId" IS NULL
                    RETURNING "OperationId"
                )
                UPDATE "MediaImportJobs"
                SET "Sequence" = "Sequence" + 1, "UpdatedAt" = now()
                WHERE "OperationId" = @OperationId
                  AND EXISTS (SELECT 1 FROM changed);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
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
                SELECT row."RowIndex" AS "Index", row."Title", row."Stage", row."OutcomeTypeId", row."WorkId", row."Searches",
                       row."Source", row."Query", row."Detail", row."CandidateCount", row."Attempt",
                       row."History"::text AS "HistoryJson"
                FROM "MediaImportJobRows" AS row
                JOIN "MediaImportJobs" AS job ON job."OperationId" = row."OperationId"
                WHERE job."PublicId" = @JobId AND job."RequestedByAccountId" = @AccountId
                ORDER BY row."RowIndex";
                """;
            sql.Parameters.Add(data);
            var rows = await ctx.ReadSql.ReadListAsync<MediaImportJobRowData>(sql, ct);
            return new MediaImportJobStatusV1(header.JobId, header.StatusTypeId, header.TotalCount, header.ProcessedCount,
                header.Sequence, header.ErrorKey, new(rows.Select(row => new MediaImportJobRowV1(row.Index, row.Title, row.Stage, row.OutcomeTypeId, row.WorkId, row.Searches,
                    row.Source, row.Query, row.Detail, row.CandidateCount, row.Attempt, row.HistoryJson))));
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
                    INSERT INTO "MediaImportJobRows" ("OperationId", "RowIndex", "Stage", "OutcomeTypeId", "Title", "WorkId", "Searches", "UpdatedAt")
                    SELECT "OperationId", @RowIndex, 'Done', @OutcomeTypeId, @Title, @WorkId, @Searches, now()
                    FROM checkpoint WHERE true
                    ON CONFLICT ("OperationId", "RowIndex") DO UPDATE
                        SET "Stage" = 'Done',
                            "OutcomeTypeId" = EXCLUDED."OutcomeTypeId",
                            "Title" = coalesce(EXCLUDED."Title", "MediaImportJobRows"."Title"),
                            "WorkId" = EXCLUDED."WorkId",
                            "Searches" = EXCLUDED."Searches",
                            "History" = (
                                SELECT COALESCE(jsonb_agg(recent.item ORDER BY recent.ordinal), '[]'::jsonb)
                                FROM (
                                    SELECT value AS item, ordinality AS ordinal
                                    FROM jsonb_array_elements("MediaImportJobRows"."History") WITH ORDINALITY
                                    ORDER BY ordinality DESC LIMIT 19
                                ) AS recent
                            ) || jsonb_build_array(jsonb_build_object(
                                'stage', 'Done', 'source', NULL, 'query', NULL, 'detail', NULL,
                                'candidateCount', NULL, 'attempt', 0, 'at', now())),
                            "UpdatedAt" = now()
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
