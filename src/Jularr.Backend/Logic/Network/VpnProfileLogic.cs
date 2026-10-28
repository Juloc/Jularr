using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Network;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Network
{
    internal static class VpnProfileLogic
    {
        // A profile is only kept when its configuration names a DNS server: names are then resolved through the tunnel, never by the host.
        public static WireGuardProfilePreview Validate(string configuration)
        {
            WireGuardProfilePreview preview;
            try
            {
                preview = WireGuardProfileLogic.Preview(configuration);
            }
            catch (ArgumentException)
            {
                // Never reflect submitted configuration or keys into responses, exceptions or logs.
                throw new DataValidationException("Configuration", "invalid_wireguard_configuration");
            }

            if (preview.DnsServers.Length == 0) throw new DataValidationException("Configuration", "wireguard_dns_required");
            return preview;
        }

        public static async Task<long> CreateVpnProfileAsync(this LogicContext ctx, VpnProfileSaveData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "VpnProfiles" ("Name", "ProtectedConfiguration", "EndpointHost", "EndpointPort", "TunnelAddresses", "DnsServers", "HasIpv6Route")
                VALUES
                    (@Name, @ProtectedConfiguration, @EndpointHost, @EndpointPort, @TunnelAddresses, @DnsServers, @HasIpv6Route)
                ON CONFLICT ("Name") DO NOTHING
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            var created = await ctx.ReadOptionalAsync<VpnProfileIdData>(sql, ct);
            return created?.Id ?? throw new DataValidationException(nameof(data.Name), "duplicate_value");
        }

        public static async Task DeleteVpnProfileAsync(this LogicContext ctx, VpnProfileKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Version"
                FROM
                    "VpnProfiles" AS profile
                WHERE
                    profile."Id" = @Id
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<VpnProfileKeyData>(sql, ct) ?? throw new KeyNotFoundException("The VPN profile was not found.");
            if (current.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "VpnProfiles" AS profile
                WHERE
                    profile."Id" = @Id;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1) throw new KeyNotFoundException("The VPN profile was not found.");
        }

        // Every profile in the order a client tries them.
        public static async Task<IReadOnlyList<VpnProfileSecretData>> ReadVpnProfileSecretsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."ProtectedConfiguration"
                FROM
                    "VpnProfiles" AS profile
                ORDER BY
                    profile."Id"
                LIMIT 100;
                """;
            return await ctx.Sql.ReadListAsync<VpnProfileSecretData>(sql, ct);
        }

        public static async Task<VpnProfileSecretData> ReadVpnProfileSecretAsync(this LogicContext ctx, long vpnProfileId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."ProtectedConfiguration"
                FROM
                    "VpnProfiles" AS profile
                WHERE
                    profile."Id" = @Id;
                """;
            sql.Parameters.Add(new VpnProfileIdData(vpnProfileId));
            return await ctx.ReadOptionalAsync<VpnProfileSecretData>(sql, ct) ?? throw new KeyNotFoundException("The VPN profile was not found.");
        }
    }
}
