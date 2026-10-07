using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.Acquisition.Health;

/// <summary>
/// Periodic health check for every enabled indexer and download client
/// entry, following the same hosted-service pattern as the SABnzbd
/// operation monitor. The recorded status is the one thing the search
/// coordinator and download client selector consult to skip an unhealthy
/// entry.
/// </summary>
public sealed class AcquisitionHealthCheckService(
    IServiceScopeFactory scopeFactory,
    ILogger<AcquisitionHealthCheckService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not refresh acquisition health.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var modules = services.GetService<IInstanceModuleService>();
        if (modules is not null
            && !await modules.IsEnabledAsync(
                InstanceModule.Acquisition,
                cancellationToken))
        {
            return;
        }

        var indexerStore = services.GetRequiredService<IndexerStore>();
        var indexers = services.GetRequiredService<IReadOnlyDictionary<IndexerType, IIndexer>>();
        var clientStore = services.GetRequiredService<DownloadClientStore>();
        var client = services.GetRequiredService<IDownloadClient>();
        var health = services.GetRequiredService<AcquisitionHealthStore>();

        foreach (var entry in (await indexerStore.LoadAllAsync(cancellationToken)).Where(entry => entry.Enabled))
        {
            if (!indexers.TryGetValue(entry.Type, out var indexer))
            {
                continue;
            }

            await CheckIndexerAsync(indexer, indexerStore, entry, health, cancellationToken);
        }

        foreach (var entry in (await clientStore.LoadAllAsync(cancellationToken)).Where(entry => entry.Enabled))
        {
            await CheckDownloadClientAsync(client, entry, health, cancellationToken);
        }
    }

    private async Task CheckIndexerAsync(
        IIndexer indexer,
        IndexerStore store,
        IndexerEntry entry,
        AcquisitionHealthStore health,
        CancellationToken cancellationToken)
    {
        bool reachable;
        bool authOk;
        string? error;
        try
        {
            var result = await indexer.TestAsync(entry, cancellationToken);
            reachable = true;
            authOk = result.Success;
            error = result.Error;
            if (result.Capabilities is { } capabilities)
            {
                await store.UpdateCapabilitiesAsync(entry.Id, capabilities, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reachable = false;
            authOk = false;
            error = exception.Message;
        }

        await health.RecordAsync(
            new AcquisitionHealthStatus(
                AcquisitionHealthKind.Indexer, entry.Id, entry.Name, reachable, authOk, error, DateTimeOffset.UtcNow),
            cancellationToken);

        if (!reachable || !authOk)
        {
            logger.LogWarning("Indexer '{Indexer}' is unhealthy: {Error}", entry.Name, error);
        }
    }

    private async Task CheckDownloadClientAsync(
        IDownloadClient client,
        DownloadClientEntry entry,
        AcquisitionHealthStore health,
        CancellationToken cancellationToken)
    {
        bool reachable;
        bool authOk;
        string? error;
        try
        {
            var result = await client.TestAsync(entry, cancellationToken);
            reachable = true;
            authOk = result.Success;
            error = result.Error;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reachable = false;
            authOk = false;
            error = exception.Message;
        }

        await health.RecordAsync(
            new AcquisitionHealthStatus(
                AcquisitionHealthKind.DownloadClient, entry.Id, entry.Name, reachable, authOk, error, DateTimeOffset.UtcNow),
            cancellationToken);

        if (!reachable || !authOk)
        {
            logger.LogWarning("Download client '{Client}' is unhealthy: {Error}", entry.Name, error);
        }
    }
}
