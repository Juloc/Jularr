using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // The last answer of the health check, held for the Dashboard and the Jobs page. A check within a minute of it is not repeated unless somebody asks for it.
    internal sealed class HealthChecks(TimeProvider time)
    {
        public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);
        private readonly object _gate = new();
        private HealthV1? _current;

        public HealthV1? Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public bool IsFresh => Current?.CheckedAt is { } at && time.GetUtcNow().UtcDateTime - at < Fresh;

        public void Keep(HealthV1 health)
        {
            lock (_gate)
            {
                _current = health;
            }
        }
    }

    // Asks what is configured whether it answers: the storage worker's stored state, what providers answered last, and the media servers, download clients and indexers through their own tests.
    internal sealed class HealthProbes(HealthChecks checks, ProviderObservations observations, PlexServer plex, MediaServers mediaServers, AcquisitionConnectionProbe connectionProbe, TimeProvider time,
        ILogger<HealthProbes> logger)
    {
        // Nobody waits longer than this for one service.
        private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(8);

        // What is known without asking anyone: the services that are configured and what they last answered.
        public async Task<HealthV1> ReadStoredAsync(LogicContext ctx, CancellationToken ct)
        {
            var targets = await ctx.ReadHealthTargetsAsync(ct);
            var services = Known(targets, time.GetUtcNow().UtcDateTime);
            services.AddRange(targets.Connections.Select(HealthCheckLogic.StoredConnection));
            return HealthCheckLogic.Summarize(services, null);
        }

        // One check of everything configured. Reads come first, the services are asked at the same time outside any transaction, and a connection's result is kept with it.
        public async Task<HealthV1> RunAsync(LogicContext ctx, bool force, CancellationToken ct)
        {
            if (!force && checks.IsFresh && checks.Current is { } fresh)
            {
                return fresh;
            }

            var targets = await ctx.ExecuteBatchAsync((batch, token) => batch.ReadHealthTargetsAsync(token), ct);
            var now = time.GetUtcNow().UtcDateTime;
            var services = Known(targets, now);
            var asked = new List<Task<HealthServiceV1>>();
            var tests = new List<ConnectionTest>();

            // Plex, then Jellyfin and Emby while their module is on.
            if (await plex.ConnectAsync(ctx, ct) is { } plexConnection)
            {
                asked.Add(AskAsync(HealthServiceType.MediaServer, "mediaserver:plex", "Plex", "plex_unavailable", async token =>
                {
                    await plex.ReadServerAsync(plexConnection, token);
                    return null;
                }, ct));
            }

            if (targets.EnabledModules.Contains("ProgressSync") && targets.EnabledModules.Contains("Library"))
            {
                foreach (var type in new[] { MediaServerType.Jellyfin, MediaServerType.Emby })
                {
                    if (await mediaServers.ConnectAsync(ctx, type, ct) is { } connection)
                    {
                        asked.Add(AskAsync(HealthServiceType.MediaServer, "mediaserver:" + type.ProviderKey(), type.ToString(), "media_server_unavailable", async token =>
                        {
                            await mediaServers.ReadServerAsync(connection, token);
                            return null;
                        }, ct));
                    }
                }
            }

            // A download client every time, an indexer or news server when its stored test is old.
            foreach (var row in targets.Connections)
            {
                if (!HealthCheckLogic.NeedsTest(row, now))
                {
                    services.Add(HealthCheckLogic.StoredConnection(row));
                    continue;
                }

                AcquisitionConnectionData data;
                try
                {
                    data = await ctx.ExecuteBatchAsync((batch, token) => batch.ReadAcquisitionConnectionAsync(row.Id, token), ct);
                }
                catch (KeyNotFoundException)
                {
                    continue;
                }

                var test = new ConnectionTest(data);
                tests.Add(test);
                asked.Add(test.Service = AskAsync(HealthCheckLogic.ServiceType(row), "connection:" + row.Id, row.ConnectionName, "acquisition_unavailable", async token =>
                {
                    var (result, capabilities) = await connectionProbe.TestAsync(data, token);
                    test.Capabilities = capabilities;
                    return result.ErrorKey;
                }, ct));
            }

            services.AddRange(await Task.WhenAll(asked));
            foreach (var test in tests)
            {
                var errorKey = test.Service!.Result.ErrorKey;
                await ctx.ExecuteBatchAsync(async (batch, token) =>
                {
                    await batch.SaveAcquisitionConnectionCheckAsync(new AcquisitionConnectionCheckData(test.Data.Id, test.Data.Version, test.Capabilities, errorKey), token);
                    return true;
                }, ct);
            }

            var health = HealthCheckLogic.Summarize(services, now);
            checks.Keep(health);
            return health;
        }

        // The database answered, or nothing here could have been read; then what is stored and observed.
        private List<HealthServiceV1> Known(HealthTargets targets, DateTime now)
        {
            var services = new List<HealthServiceV1> { HealthCheckLogic.Database(now) };
            services.AddRange(targets.Mounts.Select(HealthCheckLogic.Mount));
            services.AddRange(targets.Providers.Select(provider => HealthCheckLogic.Provider(provider, observations.Last(provider.ProviderKey))).OfType<HealthServiceV1>());
            return services;
        }

        // The service is asked within its limit; what it says, or that it said nothing, is its answer.
        private async Task<HealthServiceV1> AskAsync(HealthServiceType type, string key, string name, string unavailableKey, Func<CancellationToken, Task<string?>> ask, CancellationToken ct)
        {
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limited.CancelAfter(ProbeLimit);
            string? errorKey;
            try
            {
                errorKey = await ask(limited.Token);
            }
            catch (ServiceUnavailableException exception)
            {
                errorKey = exception.Code;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(exception, "{Service} did not answer the health check.", key);
                errorKey = unavailableKey;
            }

            return new HealthServiceV1(type, key, name, errorKey is null ? HealthStateType.Available : HealthStateType.Unavailable, errorKey, time.GetUtcNow().UtcDateTime);
        }

        // A connection asked on this check and what its test found out about the indexer.
        private sealed class ConnectionTest(AcquisitionConnectionData data)
        {
            public AcquisitionConnectionData Data { get; } = data;
            public string? Capabilities { get; set; }
            public Task<HealthServiceV1>? Service { get; set; }
        }
    }
}
