using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;

namespace Jularr.Backend.Logic.Reading
{
    // A profile's notes on the pages of a book's PDF: the page number and the first words of the page, so a note finds its page again.
    internal static class ReadingPageNoteLogic
    {
        public static async Task<long> CreateReadingPageNoteAsync(this LogicContext ctx, ReadingPageNoteSaveData data, CancellationToken ct)
        {
            // One profile adds one note at a time; 1000 per book keep every list complete.
            await ctx.LockProfileAsync(data.ProfileId, ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                    AND (SELECT count(*) FROM "ReadingPageNotes" AS note WHERE note."ProfileId" = profile."Id" AND note."MediaAssetId" = @MediaAssetId) < 1000;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<ProfileParameters>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.Note), "annotation_limit");
            }

            // Only books the profile may see and that have a PDF.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingPageNotes" ("ProfileId", "MediaAssetId", "PageNumber", "Anchor", "Note")
                SELECT
                    @ProfileId,
                    asset."Id",
                    @PageNumber,
                    @Anchor,
                    @Note
                FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" IN (3, 4)
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                    AND EXISTS (SELECT 1 FROM "StoredFiles" AS file WHERE file."MediaAssetId" = asset."Id" AND file."RelativePath" ~* '\.pdf$')
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadOptionalAsync<ReadingPageNoteIdData>(sql, ct) ?? throw new KeyNotFoundException("The book was not found.")).Id;
        }

        public static async Task DeleteReadingPageNoteAsync(this LogicContext ctx, ReadingPageNoteKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ReadingPageNotes" AS note
                WHERE
                    note."Id" = @ReadingPageNoteId
                    AND note."ProfileId" = @ProfileId
                    AND note."MediaAssetId" = @MediaAssetId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The note was not found.");
            }
        }
    }
}
