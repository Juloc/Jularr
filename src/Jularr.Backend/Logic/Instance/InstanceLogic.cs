using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Localization;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Logic.Instance
{
    internal static class InstanceLogic
    {
        public static async Task UpdateInstanceSettingsAsync(this LogicContext ctx, InstanceSettingsUpdateData data, CancellationToken ct)
        {
            await ctx.RequireEnabledUiLocaleAsync(data.UiLocaleId, nameof(data.UiLocaleId), ct);

            // Subtitle languages are known two-letter codes, each once.
            if (data.SubtitleLanguages is { } languages)
            {
                var codes = languages.Select(LanguageCodes.ToTwoLetter).ToArray();
                if (codes.Any(code => code is null))
                {
                    throw new DataValidationException(nameof(data.SubtitleLanguages), "unknown_language");
                }

                data.SubtitleLanguages = [.. codes.OfType<string>().Distinct(StringComparer.Ordinal)];
            }

            // Reject concurrent edits.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSettings" AS settings
                SET
                    "Name" = @Name,
                    "UiLocaleId" = @UiLocaleId,
                    "ReadingCacheBytes" = coalesce(@ReadingCacheMegabytes::bigint * 1048576, settings."ReadingCacheBytes"),
                    "ReadingCacheWorks" = coalesce(@ReadingCacheWorks, settings."ReadingCacheWorks"),
                    "UsersConfirmSources" = coalesce(@UsersConfirmSources, settings."UsersConfirmSources"),
                    "LibraryScanIntervalHours" = coalesce(@LibraryScanIntervalHours, settings."LibraryScanIntervalHours"),
                    "HistoryRetentionDays" = coalesce(@HistoryRetentionDays, settings."HistoryRetentionDays"),
                    "HistoryMegabytes" = coalesce(@HistoryMegabytes, settings."HistoryMegabytes"),
                    "CollectionLimit" = coalesce(@CollectionLimit, settings."CollectionLimit"),
                    "UnitGroupLimit" = coalesce(@UnitGroupLimit, settings."UnitGroupLimit"),
                    "SubtitleLanguages" = coalesce(@SubtitleLanguages::varchar(2)[], settings."SubtitleLanguages"),
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
