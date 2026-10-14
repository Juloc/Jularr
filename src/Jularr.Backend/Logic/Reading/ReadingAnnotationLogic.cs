using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Reading
{
    internal static class ReadingAnnotationLogic
    {
        public static async Task<long> CreateReadingAnnotationAsync(this LogicContext ctx, ReadingAnnotationSaveData data, CancellationToken ct)
        {
            // A highlight covers at least one character.
            if ((data.StartBlock, data.StartOffset).CompareTo((data.EndBlock, data.EndOffset)) >= 0)
            {
                throw new DataValidationException(nameof(data.EndOffset), "out_of_range");
            }

            // One profile adds one note at a time; 1000 per book keep every list complete.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                FOR UPDATE OF
                    profile;
                """;
            sql.Parameters.Add(data);
            _ = await ctx.ReadRequiredAsync<ProfileParameters>(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                    AND (SELECT count(*) FROM "ReadingAnnotations" AS annotation WHERE annotation."ProfileId" = profile."Id" AND annotation."MediaAssetId" = @MediaAssetId) < 1000;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<ProfileParameters>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.Quote), "annotation_limit");
            }

            // Only books with text the profile may see.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingAnnotations" ("ProfileId", "MediaAssetId", "SectionIndex", "StartBlock", "StartOffset", "EndBlock", "EndOffset", "Quote", "Note", "HighlightColorTypeId")
                SELECT
                    @ProfileId,
                    asset."Id",
                    @SectionIndex,
                    @StartBlock,
                    @StartOffset,
                    @EndBlock,
                    @EndOffset,
                    @Quote,
                    @Note,
                    @HighlightColorTypeId
                FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" = 3
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadOptionalAsync<ReadingAnnotationIdData>(sql, ct) ?? throw new KeyNotFoundException("The book was not found.")).Id;
        }

        // Only the note and the colour change; the anchor stays.
        public static async Task UpdateReadingAnnotationAsync(this LogicContext ctx, ReadingAnnotationUpdateData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ReadingAnnotations" AS annotation
                SET
                    "Note" = @Note,
                    "HighlightColorTypeId" = @HighlightColorTypeId,
                    "UpdatedAt" = now()
                WHERE
                    annotation."Id" = @ReadingAnnotationId
                    AND annotation."ProfileId" = @ProfileId
                    AND annotation."MediaAssetId" = @MediaAssetId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The note was not found.");
            }
        }

        public static async Task DeleteReadingAnnotationAsync(this LogicContext ctx, ReadingAnnotationKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ReadingAnnotations" AS annotation
                WHERE
                    annotation."Id" = @ReadingAnnotationId
                    AND annotation."ProfileId" = @ProfileId
                    AND annotation."MediaAssetId" = @MediaAssetId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The note was not found.");
            }
        }
    }
}
