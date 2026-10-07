using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Library;

// Queues one full reconciliation per enabled root once the host is up. The runs themselves
// go through LibraryScanCoordinator like every other scan, so they share its guard,
// progress reporting and history.
//
// A root whose storage is offline at startup (a sleeping NAS) keeps its startup
// reconciliation pending: it is offered again every RetryInterval and runs as soon as the
// storage is readable, for example after playback woke the NAS. The retry only observes the
// storage; it never sends Wake-on-LAN.
public sealed class LibraryStartupScanService(
    IServiceScopeFactory scopeFactory,
    LibraryScanCoordinator scans,
    IHostApplicationLifetime lifetime,
    ILogger<LibraryStartupScanService> logger) : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Host startup is never held up by NAS probing. Lane recovery only abandons work of the
        // previous process, so these runs stay queued even if recovery is still in progress.
        await WaitForStartAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        Guid[] roots;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            roots = await (await db.AnimeRootsForBackgroundWorkAsync(scope.ServiceProvider, stoppingToken))
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.Id)
                .ToArrayAsync(stoppingToken);
        }

        try
        {
            var pending = await QueueStartupAsync(roots, stoppingToken);
            while (pending.Count > 0)
            {
                await Task.Delay(RetryInterval, stoppingToken);
                pending = await QueueStartupAsync(pending, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Queues the startup reconciliation of each root and returns the roots whose storage was
    // not readable, so their reconciliation is still owed.
    public async Task<IReadOnlyList<Guid>> QueueStartupAsync(
        IReadOnlyList<Guid> roots,
        CancellationToken stoppingToken)
    {
        var pending = new List<Guid>();
        foreach (var rootId in roots)
        {
            try
            {
                var queued = await scans.QueueAsync(
                    new LibraryScanRequest(rootId, LibraryScanTrigger.Startup),
                    stoppingToken);

                if (queued.Outcome == LibraryScanQueueOutcome.RootUnavailable)
                {
                    pending.Add(rootId);
                    logger.LogInformation(
                        "Startup library reconciliation for root {RootId} waits for its media storage to come online.",
                        rootId);
                }
                else if (!queued.Queued && queued.Outcome != LibraryScanQueueOutcome.AlreadyActive)
                {
                    logger.LogWarning(
                        "Startup library reconciliation was not queued for root {RootId}: {Reason}",
                        rootId,
                        queued.Message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Startup library reconciliation could not be queued for root {RootId}; remaining roots are still queued.",
                    rootId);
            }
        }

        return pending;
    }

    private async Task WaitForStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var stoppingRegistration = stoppingToken.Register(() => started.TrySetResult());
        await started.Task;
    }
}
