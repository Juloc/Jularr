using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Logic.Localization;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Instance
{
    internal static class InstanceLogic
    {
        public static async Task UpdateInstanceSettingsAsync(this LogicContext ctx, InstanceSettingsUpdateData data, CancellationToken ct)
        {
            // Hold the enabled locale.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    locale."Id"
                FROM
                    "UiLocales" AS locale
                WHERE
                    locale."Id" = @UiLocaleId
                    AND locale."IsEnabled" = TRUE
                FOR SHARE OF
                    locale;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<UiLocaleData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.UiLocaleId), "unknown_reference");
            }

            // Reject concurrent edits.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSettings" AS settings
                SET
                    "Name" = @Name,
                    "UiLocaleId" = @UiLocaleId,
                    "ReadingCacheBytes" = coalesce(@ReadingCacheMegabytes::bigint * 1048576, settings."ReadingCacheBytes"),
                    "ReadingCacheWorks" = coalesce(@ReadingCacheWorks, settings."ReadingCacheWorks"),
                    "Version" = settings."Version" + 1
                WHERE
                    settings."Id" = 1
                    AND settings."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }
        }
    }
}
