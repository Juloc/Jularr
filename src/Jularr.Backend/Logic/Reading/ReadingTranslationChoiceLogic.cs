using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Reading
{
    // Which model's translation a passage of a book shows, for every reader of it. A re-translation names its model for one block, the blocks of a
    // section or those of every section; until that model has translated a block, the block keeps showing the text it had.
    internal static class ReadingTranslationChoiceLogic
    {
        // Only a book the profile may open; returns the number of blocks chosen (none: no such block or book).
        public static async Task<int> ChooseReadingTranslationAsync(this LogicContext ctx, ReadingTranslationScopeWriteData data, long profileId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingTranslationChoices" ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex", "Model")
                SELECT
                    document."Id",
                    section."Number",
                    @Language,
                    block."Number",
                    @Model
                FROM
                    "ReadingDocuments" AS document
                    CROSS JOIN LATERAL (
                        SELECT
                            number AS "Number",
                            coalesce((document."Structure" -> 'Sections' -> number ->> 'Blocks')::integer, 0) AS "Blocks"
                        FROM generate_series(0, jsonb_array_length(document."Structure" -> 'Sections') - 1) AS number
                    ) AS section
                    CROSS JOIN LATERAL generate_series(0, section."Blocks" - 1) AS block ("Number")
                WHERE
                    document."Id" = @DocumentId
                    AND jsonb_typeof(document."Structure" -> 'Sections') = 'array'
                    AND (@SectionIndex::integer IS NULL OR section."Number" = @SectionIndex)
                    AND (@BlockIndex::integer IS NULL OR block."Number" = @BlockIndex)
                    AND EXISTS (
                        SELECT 1
                        FROM "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId
                        WHERE file."Id" = document."StoredFileId"
                    )
                ON CONFLICT ("ReadingDocumentId", "SectionIndex", "Language", "BlockIndex") DO UPDATE
                SET
                    "Model" = EXCLUDED."Model",
                    "ChosenAt" = now();
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(new ProfileParameters(profileId));
            return await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
