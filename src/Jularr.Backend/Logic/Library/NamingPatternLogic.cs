using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    internal static class NamingPatternLogic
    {
        public static async Task<IReadOnlyDictionary<MediaType, string>> ReadNamingPatternsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pattern."MediaTypeId",
                    pattern."Pattern"
                FROM
                    "NamingPatterns" AS pattern;
                """;
            return (await ctx.ReadSql.ReadListAsync<NamingPatternData>(sql, ct)).ToDictionary(pattern => pattern.MediaTypeId, pattern => pattern.Pattern);
        }

        public static async Task SaveNamingPatternAsync(this LogicContext ctx, NamingPatternSaveData data, CancellationToken ct)
        {
            NamingPattern.Validate(data.MediaTypeId, data.Pattern, nameof(data.Pattern));

            // Reject concurrent edits.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "NamingPatterns" AS pattern
                SET
                    "Pattern" = @Pattern,
                    "Version" = pattern."Version" + 1
                WHERE
                    pattern."MediaTypeId" = @MediaTypeId
                    AND pattern."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }
    }
}
