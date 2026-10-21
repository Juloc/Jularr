using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Reading
{
    // The reading copy of a PDF: an EPUB beside it that a book is read from once it is ready and its PDF has not changed since.
    internal static class ReadingDerivationLogic
    {
        // An attempt starts for this version of the PDF; a former copy stays known until it is replaced or removed.
        public static async Task StartReadingDerivationAsync(this LogicContext ctx, ReadingDerivationStartData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingDerivations" ("StoredFileId", "SourceSizeBytes", "SourceModifiedAt", "AnalyzerVersion", "ReadingDerivationStatusTypeId")
                VALUES
                    (@StoredFileId, @SizeBytes, @ModifiedAt, @AnalyzerVersion, 1)
                ON CONFLICT ("StoredFileId") DO UPDATE
                SET
                    "Attempts" = CASE
                        WHEN "ReadingDerivations"."SourceSizeBytes" = EXCLUDED."SourceSizeBytes"
                            AND "ReadingDerivations"."SourceModifiedAt" = EXCLUDED."SourceModifiedAt"
                            AND "ReadingDerivations"."AnalyzerVersion" = EXCLUDED."AnalyzerVersion" THEN least("ReadingDerivations"."Attempts" + 1, 100)
                        ELSE 1
                    END,
                    "SourceSizeBytes" = EXCLUDED."SourceSizeBytes",
                    "SourceModifiedAt" = EXCLUDED."SourceModifiedAt",
                    "SourceSha256" = NULL,
                    "AnalyzerVersion" = EXCLUDED."AnalyzerVersion",
                    "ReadingDerivationStatusTypeId" = 1,
                    "Reason" = NULL,
                    "Pages" = NULL,
                    "Chapters" = NULL,
                    "Figures" = NULL,
                    "AttemptedAt" = now(),
                    "FinishedAt" = NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The attempt ends; a PDF that changed meanwhile keeps its newer row and is converted again.
        public static async Task FinishReadingDerivationAsync(this LogicContext ctx, ReadingDerivationResultData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReadingDerivations" AS derivation
                SET
                    "SourceSha256" = @Sha256,
                    "ReadingDerivationStatusTypeId" = @ReadingDerivationStatusTypeId,
                    "Reason" = @Reason,
                    "EbookStoredFileId" = @EbookStoredFileId,
                    "Pages" = @Pages,
                    "Chapters" = @Chapters,
                    "Figures" = @Figures,
                    "FinishedAt" = now()
                WHERE
                    derivation."StoredFileId" = @StoredFileId
                    AND derivation."SourceSizeBytes" = @SizeBytes
                    AND derivation."SourceModifiedAt" = @ModifiedAt
                    AND derivation."AnalyzerVersion" = @AnalyzerVersion;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The written copy belongs to its book's library at once; a stale row of that name is taken over.
        public static Task<StoredFileIdData> SaveReadingDerivedFileAsync(this LogicContext ctx, ReadingDerivedFileData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                VALUES
                    (@LibraryRootId, @RelativePath, @SizeBytes, @ModifiedAt, now(), @MediaAssetId)
                ON CONFLICT ("LibraryRootId", "RelativePath") DO UPDATE
                SET
                    "SizeBytes" = EXCLUDED."SizeBytes",
                    "ModifiedAt" = EXCLUDED."ModifiedAt",
                    "LastSeenAt" = now(),
                    "MissingSince" = NULL,
                    "MediaAssetId" = EXCLUDED."MediaAssetId"
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadRequiredAsync<StoredFileIdData>(sql, ct);
        }

        // A stale copy leaves the library with its file; only files that are a reading copy are removed here.
        public static async Task RemoveReadingDerivedFileAsync(this LogicContext ctx, StoredFileIdData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "StoredFiles" AS file
                USING
                    "ReadingDerivations" AS derivation
                WHERE
                    file."Id" = @Id
                    AND derivation."EbookStoredFileId" = file."Id";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
