using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Reading
{
    // The text of a scanned PDF, read page by page in the background: the queue of files and the pages found so far.
    internal static class ReadingRecognitionLogic
    {
        // With the Ocr module on, a scanned PDF waits for recognition; a changed file starts over, also after a failure. Answers whether it waits.
        public static async Task<bool> RequestReadingRecognitionAsync(this LogicContext ctx, ReadingRecognitionData data, CancellationToken ct)
        {
            // Pages found in an older version of the file do not belong to this one.
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ReadingRecognizedPages" AS page
                USING
                    "ReadingRecognitions" AS recognition
                WHERE
                    recognition."StoredFileId" = @StoredFileId
                    AND page."StoredFileId" = recognition."StoredFileId"
                    AND (recognition."SourceSizeBytes" <> @SizeBytes OR recognition."SourceModifiedAt" <> @ModifiedAt);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                WITH enabled AS (
                    SELECT
                        1
                    FROM
                        "InstanceModules"
                    WHERE
                        "Key" = 'Ocr'
                        AND "IsEnabled"
                ), saved AS (
                    INSERT INTO
                        "ReadingRecognitions" ("StoredFileId", "SourceSizeBytes", "SourceModifiedAt")
                    SELECT
                        @StoredFileId,
                        @SizeBytes,
                        @ModifiedAt
                    FROM
                        enabled
                    ON CONFLICT ("StoredFileId") DO UPDATE
                    SET
                        "SourceSizeBytes" = EXCLUDED."SourceSizeBytes",
                        "SourceModifiedAt" = EXCLUDED."SourceModifiedAt",
                        "RequestedAt" = now(),
                        "Pages" = NULL,
                        "WantedPage" = NULL,
                        "WantedAt" = NULL,
                        "FailedAt" = NULL,
                        "FinishedAt" = NULL
                    WHERE
                        "ReadingRecognitions"."SourceSizeBytes" <> EXCLUDED."SourceSizeBytes"
                        OR "ReadingRecognitions"."SourceModifiedAt" <> EXCLUDED."SourceModifiedAt"
                    RETURNING
                        "FailedAt",
                        "FinishedAt"
                )
                SELECT
                    EXISTS (SELECT 1 FROM enabled)
                    AND coalesce(
                        (SELECT "FailedAt" IS NULL AND "FinishedAt" IS NULL FROM saved),
                        (SELECT "FailedAt" IS NULL AND "FinishedAt" IS NULL FROM "ReadingRecognitions" WHERE "StoredFileId" = @StoredFileId),
                        FALSE) AS "IsPending";
                """;
            sql.Parameters.Add(data);
            return (await ctx.Sql.ReadRequiredAsync<ReadingRecognitionStateData>(sql, ct)).IsPending;
        }

        // The page a reader has open comes first; only unfinished scans take it.
        public static async Task WantReadingPageAsync(this LogicContext ctx, ReadingWantedPageData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReadingRecognitions" AS recognition
                SET
                    "WantedPage" = @PageNumber,
                    "WantedAt" = now()
                FROM
                    "StoredFiles" AS file
                WHERE
                    file."MediaAssetId" = @MediaAssetId
                    AND recognition."StoredFileId" = file."Id"
                    AND recognition."FinishedAt" IS NULL
                    AND recognition."FailedAt" IS NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SetReadingRecognitionPagesAsync(this LogicContext ctx, ReadingRecognitionPagesData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReadingRecognitions" AS recognition
                SET
                    "Pages" = @Pages
                WHERE
                    recognition."StoredFileId" = @StoredFileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveReadingRecognizedPageAsync(this LogicContext ctx, ReadingRecognizedPageData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingRecognizedPages" ("StoredFileId", "PageNumber", "Reason", "Quality", "Lines")
                VALUES
                    (@StoredFileId, @PageNumber, @Reason, @Quality, @Lines::jsonb)
                ON CONFLICT ("StoredFileId", "PageNumber") DO UPDATE
                SET
                    "Reason" = EXCLUDED."Reason",
                    "Quality" = EXCLUDED."Quality",
                    "Lines" = EXCLUDED."Lines";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // A scan whose pages were all read keeps them; a failed one waits for a changed file.
        public static async Task FinishReadingRecognitionAsync(this LogicContext ctx, ReadingRecognitionResultData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = data.IsFailed
                ? """
                  UPDATE
                      "ReadingRecognitions"
                  SET
                      "FailedAt" = now()
                  WHERE
                      "StoredFileId" = @StoredFileId;
                  """
                : """
                  UPDATE
                      "ReadingRecognitions"
                  SET
                      "FinishedAt" = now()
                  WHERE
                      "StoredFileId" = @StoredFileId;
                  """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The reading text of a PDF is built again from what is now known: its prepared document counts as not cached until then.
        public static async Task ResetReadingDocumentsAsync(this LogicContext ctx, StoredFileIdData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReadingDocuments" AS document
                SET
                    "CacheBytes" = 0,
                    "CacheCompletedAt" = NULL
                WHERE
                    document."StoredFileId" = @Id
                    AND document."ReadingFormatTypeId" = 2;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
