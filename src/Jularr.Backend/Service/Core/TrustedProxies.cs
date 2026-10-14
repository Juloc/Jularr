using System.Net;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    // Forwarded headers count only from configured proxies.
    internal sealed class TrustedProxies(NpgsqlDataSource database, TimeProvider time, ILogger<TrustedProxies> logger)
    {
        private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
        private readonly SemaphoreSlim _reading = new(1, 1);
        private Snapshot _current = new([], 0, DateTimeOffset.MinValue);
        private Expectation _expected = new(0, DateTimeOffset.MinValue, DateTimeOffset.MinValue);

        private sealed record Snapshot(IPNetwork[] Networks, long Version, DateTimeOffset ReadAt);

        private sealed record Expectation(long Version, DateTimeOffset Since, DateTimeOffset Until);

        // Saved settings apply with the next request.
        public void Expect(long version) => Volatile.Write(ref _expected, new Expectation(version, time.GetUtcNow(), time.GetUtcNow().AddMinutes(1)));

        public async Task ApplyAsync(HttpContext http)
        {
            var current = Volatile.Read(ref _current);
            var expected = Volatile.Read(ref _expected);
            var now = time.GetUtcNow();
            var waiting = current.Version < expected.Version && now < expected.Until;
            if ((now - current.ReadAt > Refresh || waiting && (current.ReadAt < expected.Since || now - current.ReadAt > Retry)) && _reading.Wait(0))
            {
                // One request reads; the others keep the last list.
                try
                {
                    current = await ReadAsync(current, now);
                    Volatile.Write(ref _current, current);
                }
                finally
                {
                    _reading.Release();
                }
            }

            Apply(http, current.Networks);
        }

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

        // A failed read keeps the last list and waits before trying again.
        private async Task<Snapshot> ReadAsync(Snapshot current, DateTimeOffset now)
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
                using var timeout = new CancellationTokenSource(ReadTimeout);
                await using var context = new SqlContext(database);
                await context.BeginAsync(SqlAccessMode.ReadOnly, cancellationToken: timeout.Token);
                var settings = await context.ReadSql.ReadRequiredAsync<TrustedProxiesData>(sql, timeout.Token);
                return new Snapshot([.. settings.TrustedProxies.Select(network => IPNetwork.Parse(network))], settings.Version, now);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning(exception, "The trusted proxies could not be read; the last list stays.");
                return current with { ReadAt = now };
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
