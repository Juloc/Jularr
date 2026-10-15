using System.Net;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Localization;
using Jularr.Backend.Logic.Media;

namespace Jularr.Backend.Logic.Instance
{
    internal static class InstanceLogic
    {
        public static async Task<string[]> ReadTrustedProxiesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."TrustedProxies"::text[] AS "TrustedProxies"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return (await ctx.ReadSql.ReadRequiredAsync<TrustedProxiesData>(sql, ct)).TrustedProxies;
        }

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

            // A public address is stored like a provider address, ending in a slash.
            if (data.PublicUrl is { } publicUrl)
            {
                data.PublicUrl = Provider.ProviderLogic.ServerAddress(publicUrl, nameof(data.PublicUrl)).AbsoluteUri;
            }

            // Canonical addresses or networks; trusting everyone is no proxy.
            if (data.TrustedProxies is { } proxies)
            {
                data.TrustedProxies = [.. proxies.Select(proxy => TrustedNetwork(proxy, nameof(data.TrustedProxies)).ToString()).Distinct(StringComparer.Ordinal)];
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
                    "PublicUrl" = @PublicUrl,
                    "TrustedProxies" = coalesce(@TrustedProxies::cidr[], settings."TrustedProxies"),
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

        private static IPNetwork TrustedNetwork(string proxy, string field)
        {
            var network = IPNetwork.TryParse(proxy, out var parsed) ? parsed
                : IPAddress.TryParse(proxy, out var address) ? new IPNetwork(new IPAddress(address.GetAddressBytes()), address.GetAddressBytes().Length * 8)
                : throw new DataValidationException(field, "invalid_network");
            if (network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96)
            {
                network = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
            }

            return network.PrefixLength > 0 ? network : throw new DataValidationException(field, "invalid_network");
        }
    }
}
