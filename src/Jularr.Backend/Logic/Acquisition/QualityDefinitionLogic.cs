using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class QualityDefinitionLogic
    {
        public static async Task SaveQualityDefinitionsAsync(this LogicContext ctx, QualityDefinitionsWriteData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "QualityDefinitions" AS quality
                SET
                    "MinimumMbPerMinute" = input."MinimumMbPerMinute",
                    "PreferredMbPerMinute" = input."PreferredMbPerMinute",
                    "MaximumMbPerMinute" = input."MaximumMbPerMinute",
                    "Version" = quality."Version" + 1
                FROM
                    jsonb_to_recordset(@Rows::jsonb) AS input("Quality" text, "MinimumMbPerMinute" numeric, "PreferredMbPerMinute" numeric, "MaximumMbPerMinute" numeric, "Version" bigint)
                WHERE
                    quality."MediaTypeId" = @MediaTypeId
                    AND quality."Quality" = input."Quality"
                    AND quality."Version" = input."Version"
                    AND jsonb_array_length(@Rows::jsonb) = (SELECT count(*) FROM "QualityDefinitions" AS expected WHERE expected."MediaTypeId" = @MediaTypeId);
                """;
            sql.Parameters.Add(data);
            var expected = System.Text.Json.JsonDocument.Parse(data.Rows);
            using (expected)
            {
                if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != expected.RootElement.GetArrayLength()) throw new DataValidationException("Rows", "outdated_version");
            }
        }
    }
}
