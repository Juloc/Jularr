using System.Net;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    // Forwarded client address, scheme and host count only from the reverse proxies of the instance settings.
    internal sealed class TrustedProxies(NpgsqlDataSource database, TimeProvider time)
    {
        private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);
        private IPNetwork[] _networks = [];
        private long _version;
        private long _expectedVersion;
        private DateTimeOffset _readAt = DateTimeOffset.MinValue;

        // Saved settings are read again until their version is visible.
        public void Expect(long version) => _expectedVersion = Math.Max(_expectedVersion, version);

        public async Task ApplyAsync(HttpContext http)
        {
            if (time.GetUtcNow() - _readAt > Refresh || _version < _expectedVersion)
            {
                if (await ReadAsync(http.RequestAborted) is { } settings)
                {
                    _networks = [.. settings.TrustedProxies.Select(network => IPNetwork.Parse(network))];
                    _version = settings.Version;
                }

                _readAt = time.GetUtcNow();
            }

            Apply(http, _networks);
        }

        // Walks the forwarded chain from the nearest proxy until an untrusted address; anything malformed changes nothing.
        internal static void Apply(HttpContext http, IReadOnlyList<IPNetwork> trusted)
        {
            if (http.Connection.RemoteIpAddress is not { } remote || !IsTrusted(remote, trusted))
            {
                return;
            }

            var chain = Values(http, "X-Forwarded-For");
            var client = remote;
            var hop = chain.Length;
            while (hop > 0 && IsTrusted(client, trusted))
            {
                if (!IPEndPoint.TryParse(chain[hop - 1], out var forwarded))
                {
                    return;
                }

                client = forwarded.Address;
                hop--;
            }

            var schemes = Values(http, "X-Forwarded-Proto");
            var hosts = Values(http, "X-Forwarded-Host");
            var scheme = Aligned(schemes, chain.Length, hop);
            var host = Aligned(hosts, chain.Length, hop);
            if (scheme is not (null or "http" or "https") || host is not null && !IsHost(host))
            {
                return;
            }

            http.Connection.RemoteIpAddress = client;
            if (scheme is not null)
            {
                http.Request.Scheme = scheme;
            }

            if (host is not null)
            {
                http.Request.Host = new HostString(host);
            }
        }

        private static bool IsHost(string host) =>
            Uri.TryCreate("http://" + host + "/", UriKind.Absolute, out var address) && address.Authority == host.ToLowerInvariant() && address.UserInfo.Length == 0;

        private static bool IsTrusted(IPAddress address, IReadOnlyList<IPNetwork> trusted)
        {
            var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return trusted.Any(network => network.Contains(plain));
        }

        private static string[] Values(HttpContext http, string header) =>
            http.Request.Headers[header].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The entry the last trusted proxy added, matched to the client's position in the address chain.
        private static string? Aligned(string[] values, int chainLength, int hop) =>
            values.Length == chainLength && hop < chainLength ? values[hop] : values.Length == 1 ? values[0] : null;

        // An unreachable database keeps the last known proxies.
        private async Task<TrustedProxiesData?> ReadAsync(CancellationToken cancellationToken)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."TrustedProxies"::text[] AS "TrustedProxies",
                    settings."Version"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            try
            {
                await using var context = new SqlContext(database);
                await context.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: cancellationToken);
                return await context.ReadSql.ReadRequiredAsync<TrustedProxiesData>(sql, cancellationToken);
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                return null;
            }
        }
    }

    public static class TrustedProxyMiddleware
    {
        public static IApplicationBuilder UseTrustedProxies(this IApplicationBuilder app) => app.Use(async (http, next) =>
        {
            await http.RequestServices.GetRequiredService<TrustedProxies>().ApplyAsync(http);
            await next(http);
        });
    }
}
