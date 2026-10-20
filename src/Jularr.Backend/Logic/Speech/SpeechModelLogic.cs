using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Speech;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Speech
{
    internal static class SpeechModelLogic
    {
        internal static async Task UpdateSpeechModelAsync(this LogicContext ctx, SpeechModelData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "SpeechModels" ("Hash", "IsEnabled", "Version")
                SELECT
                    @Hash,
                    @IsEnabled,
                    1
                WHERE
                    @Version = 0
                ON CONFLICT ("Hash") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            if (data.Version == 0)
            {
                if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException(nameof(data.Version), "outdated_version");
                return;
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "SpeechModels" AS model
                SET
                    "IsEnabled" = @IsEnabled,
                    "Version" = model."Version" + 1
                WHERE
                    model."Hash" = @Hash
                    AND model."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new DataValidationException(nameof(data.Version), "outdated_version");
        }
    }
}
