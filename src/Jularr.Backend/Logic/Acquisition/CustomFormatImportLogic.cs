using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class CustomFormatImportLogic
    {
        public static async Task<IReadOnlyList<ImportedCustomFormatKeyData>> ImportCustomFormatsAsync(this LogicContext ctx, MediaType mediaType, IReadOnlyList<CustomFormatImportData> formats, CancellationToken ct)
        {
            if (formats.Count == 0) return [];
            if (formats.Count > 1000) throw new DataValidationException("Formats", "out_of_range");
            foreach (var format in formats)
            {
                format.Validate();
                CustomFormatLogic.ValidateConditions(mediaType, format.Conditions);
            }

            if (formats.Select(format => format.SourceId).Distinct().Count() != formats.Count
                || formats.Select(format => format.Name).Distinct(StringComparer.Ordinal).Count() != formats.Count) throw new DataValidationException("Formats", "duplicate_value");

            // Matching definitions are reused. A conflicting name never overwrites the Owner's rules.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH input AS (
                    SELECT row."SourceId", row."Name", row."IncludeWhenRenaming", row."Conditions"
                    FROM jsonb_to_recordset(@Rows::jsonb) AS row("SourceId" integer, "Name" text, "IncludeWhenRenaming" boolean, "Conditions" jsonb)
                ), existing AS MATERIALIZED (
                    SELECT format."Id", format."Name", format."IncludeWhenRenaming", format."Conditions", format."Version"
                    FROM "CustomFormats" AS format
                    WHERE format."MediaTypeId" = @MediaTypeId AND format."Name" IN (SELECT input."Name" FROM input)
                    ORDER BY format."Id"
                    FOR UPDATE
                ), conflicts AS (
                    SELECT 1
                    FROM input JOIN existing USING ("Name")
                    WHERE input."Conditions" <> existing."Conditions" OR input."IncludeWhenRenaming" <> existing."IncludeWhenRenaming"
                ), inserted AS (
                    INSERT INTO "CustomFormats" ("MediaTypeId", "Name", "IncludeWhenRenaming", "Conditions")
                    SELECT @MediaTypeId, input."Name", input."IncludeWhenRenaming", input."Conditions"
                    FROM input
                    WHERE NOT EXISTS (SELECT 1 FROM conflicts)
                        AND NOT EXISTS (SELECT 1 FROM existing WHERE existing."Name" = input."Name")
                    ON CONFLICT ("MediaTypeId", "Name") DO NOTHING
                    RETURNING "Id", "Name", "Version"
                )
                SELECT
                    input."SourceId",
                    coalesce(inserted."Id", existing."Id") AS "Id",
                    coalesce(inserted."Version", existing."Version") AS "Version"
                FROM
                    input
                    LEFT JOIN inserted USING ("Name")
                    LEFT JOIN existing USING ("Name")
                WHERE
                    NOT EXISTS (SELECT 1 FROM conflicts)
                    AND coalesce(inserted."Id", existing."Id") IS NOT NULL;
                """;
            sql.Parameters.Add(new CustomFormatBatchData(mediaType, JsonSerializer.Serialize(formats, AcquisitionSelectionLogic.DefinitionOptions)));
            var result = await ctx.Sql.ReadListAsync<ImportedCustomFormatKeyData>(sql, ct);
            if (result.Count != formats.Count) throw new DataValidationException("Formats", "invalid_transition");
            return result;
        }
    }
}
