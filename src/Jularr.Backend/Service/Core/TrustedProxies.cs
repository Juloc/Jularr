using System.Net;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    // Forwarded headers count only from configured proxies: read once at start, replaced when the settings are saved.
    internal sealed class TrustedProxies(SqlDatabase database, ILogger<TrustedProxies> logger) : IHostedService
    {
        private IPNetwork[] _networks = [];

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var sql = new SqlContext(database.DataSource);
                await sql.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: cancellationToken);
                Use(await new LogicContext(sql).ReadTrustedProxiesAsync(cancellationToken));
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                // Without its list no proxy is trusted; saving the settings sets it again.
                logger.LogWarning(exception, "The trusted proxies could not be read; no proxy is trusted until the settings are saved.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Use(string[] networks) => Volatile.Write(ref _networks, [.. networks.Select(network => IPNetwork.Parse(network))]);

        public void Apply(HttpContext http) => Apply(http, Volatile.Read(ref _networks));

        // Walks the chain from the nearest proxy; anything malformed changes nothing.
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

            var scheme = Aligned(Values(http, "X-Forwarded-Proto"), chain.Length, hop);
            var host = Aligned(Values(http, "X-Forwarded-Host"), chain.Length, hop);
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

        // A host name or address with an optional port, nothing else.
        private static bool IsHost(string value)
        {
            var host = new HostString(value);
            return host.Host.Length > 0 && Uri.CheckHostName(host.Host.Trim('[', ']')) != UriHostNameType.Unknown
                && (host.Port is { } port ? host.Host + ":" + port : host.Host) == value;
        }

        private static bool IsTrusted(IPAddress address, IReadOnlyList<IPNetwork> trusted)
        {
            var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return trusted.Any(network => network.Contains(plain));
        }

        private static string[] Values(HttpContext http, string header) =>
            http.Request.Headers[header].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The entry matching the client's position in the chain.
        private static string? Aligned(string[] values, int chainLength, int hop) =>
            values.Length == chainLength && hop < chainLength ? values[hop] : values.Length == 1 ? values[0] : null;
    }

    public static class TrustedProxyMiddleware
    {
        public static IApplicationBuilder UseTrustedProxies(this IApplicationBuilder app) => app.Use(async (http, next) =>
        {
            http.RequestServices.GetRequiredService<TrustedProxies>().Apply(http);
            await next(http);
        });
    }
}
