using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Performance;
using Jularr.Web.Infrastructure;

namespace Jularr.Web.Features.Metadata;

/// <summary>What one spool pass leaves the worker with: a provider pause that nothing may shorten, or when the next entry falls due.</summary>
public sealed record WorkMetadataPass(TimeSpan? ProviderPause, TimeSpan? NextDueIn);

/// <summary>Wakes the Work metadata spool worker after something was queued for it.</summary>
public sealed class WorkMetadataRefreshSignal : BackgroundWakeSignal;

/// <summary>
/// The narrow way into the metadata spool (#820). A durable action (a Request materializing a Work) queues the Work at Request
/// priority; a detail page that opens a Work calls it with <c>interactive: true</c>, which promotes a missing or queued fetch to the
/// front without waiting for it: the page renders the best local fallback meanwhile. Fresh metadata is not refetched.
/// </summary>
public sealed class WorkMetadataRefreshQueue(WorkMetadataStore store, WorkMetadataRefreshSignal signal, TimeProvider clock)
{
    public async Task RequestMetadataRefreshAsync(Guid workId, bool interactive, CancellationToken cancellationToken)
    {
        var priority = interactive ? WorkMetadataRefreshPriority.Interactive : WorkMetadataRefreshPriority.Requested;
        var changed = await store.EnqueueAsync(workId, WorkMetadataLocales.InstanceDefault, priority, clock.GetUtcNow().UtcDateTime, cancellationToken);
        if (changed > 0)
        {
            signal.Wake();
        }
    }
}

/// <summary>
/// The background worker of the Work metadata spool. It follows the durable due-list pattern of the franchise refresh: what to do is
/// read from <c>WorkMetadataRefreshes</c>, a wake only shortens the wait. One entry runs at a time (one provider budget, shared with
/// the provider framework's rate gate and circuit); bulk entries are spaced out, a provider pause stops the pass. On start and every
/// <see cref="ReconcileInterval"/> it backfills spool entries for durable Works that have none and sweeps orphaned artwork files.
/// Nothing runs on the startup path: the first pass waits <see cref="StartupDelay"/>.
/// </summary>
public sealed class WorkMetadataRefreshService(
    IServiceScopeFactory scopes,
    WorkMetadataRefreshSignal signal,
    TimeProvider clock,
    ILogger<WorkMetadataRefreshService> logger,
    BackgroundWorkGovernor? governor = null) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromHours(6);

    /// <summary>How long a claimed entry stays owned by a run; an interrupted run is picked up again after it.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>Spacing between bulk entries, so a large backfill trickles instead of bursting.</summary>
    public static readonly TimeSpan BulkSpacing = TimeSpan.FromMilliseconds(500);

    /// <summary>Spacing between interactive and requested entries: short enough to feel immediate, long enough that opening many Works cannot hit the provider back to back.</summary>
    public static readonly TimeSpan InteractiveSpacing = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound of runs per pass, so one wake cannot spin forever.</summary>
    public const int MaxRunsPerPass = 100;

    private const int SweepBatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await signal.WaitAsync(StartupDelay, stoppingToken);
            DateTime? reconciledAt = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                var pass = new WorkMetadataPass(null, null);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (reconciledAt is null || clock.GetUtcNow().UtcDateTime - reconciledAt >= ReconcileInterval)
                    {
                        await ReconcileAsync(scope.ServiceProvider, stoppingToken);
                        reconciledAt = clock.GetUtcNow().UtcDateTime;
                    }

                    pass = await governor.RunGovernedAsync(
                        BackgroundWorkClass.ProviderRefresh,
                        "Metadata.Refresh",
                        token => ProcessDueAsync(scope.ServiceProvider, MaxRunsPerPass, token),
                        stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "The Work metadata spool pass failed.");
                }

                if (pass.ProviderPause is { } pause)
                {
                    // A wake must not cut a provider pause short: every Request wakes the worker, and each extra pass would ask the
                    // provider again (re-probing refused credentials, pushing the next entry back).
                    await Task.Delay(pause, clock, stoppingToken);
                    continue;
                }

                await signal.WaitAsync(pass.NextDueIn ?? Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Runs due entries, most urgent first, until none is due or <paramref name="maxRuns"/> ran. The result says whether the provider
    /// asked for a pause (nothing may run until it ends) or when the next entry falls due: <see cref="TimeSpan.Zero"/> when the pass
    /// stopped at its bound with work left, the time until a backoff ends when that is sooner than <see cref="Interval"/>, else null
    /// (also when no provider is configured or no Movie or TV module is enabled).
    /// </summary>
    public static async Task<WorkMetadataPass> ProcessDueAsync(IServiceProvider services, int maxRuns, CancellationToken cancellationToken)
    {
        var refresher = services.GetRequiredService<WorkMetadataRefresher>();
        var store = services.GetRequiredService<WorkMetadataStore>();
        var clock = services.GetRequiredService<TimeProvider>();
        var idle = new WorkMetadataPass(null, null);
        if (!await refresher.IsProviderReadyAsync(cancellationToken))
        {
            return idle;
        }

        WorkMediaType[] mediaTypes = [WorkMediaType.Movie, WorkMediaType.Series];
        if (services.GetService<IInstanceModuleService>() is { } modules)
        {
            var instance = await modules.GetAsync(cancellationToken);
            mediaTypes = [.. mediaTypes.Where(x => instance.IsEnabled(InstanceModuleMedia.For(x)))];
        }

        if (mediaTypes.Length == 0)
        {
            return idle;
        }

        for (var runs = 0; runs < maxRuns; runs++)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var claim = await store.ClaimNextDueAsync(mediaTypes, now, Lease, WorkMetadataRefresher.BaseBackoff, WorkMetadataRefresher.MaxBackoff, cancellationToken);
            if (claim is null)
            {
                // A backoff shorter than the regular interval wakes the worker when it ends, not a whole interval later.
                if (await store.FindNextDueAtAsync(mediaTypes, cancellationToken) is not { } due || due - now >= Interval)
                {
                    return idle;
                }

                return new WorkMetadataPass(null, due > now ? due - now : TimeSpan.Zero);
            }

            var outcome = await refresher.RunAsync(claim, cancellationToken);
            if (outcome.ProviderPause is { } pause)
            {
                return new WorkMetadataPass(pause, null);
            }

            await Task.Delay(claim.Priority >= WorkMetadataRefreshPriority.Imported ? BulkSpacing : InteractiveSpacing, clock, cancellationToken);
        }

        return new WorkMetadataPass(null, TimeSpan.Zero);
    }

    /// <summary>
    /// The idempotent backfill of the instance locale for durable Works without a spool entry, then the orphan sweep of the artwork
    /// cache: a derivative no variant references any more (replaced while a merge or crash got in the way) is deleted.
    /// </summary>
    public static async Task ReconcileAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<WorkMetadataStore>();
        var cache = services.GetRequiredService<WorkArtworkCache>();
        var clock = services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow().UtcDateTime;
        await store.EnqueueDurableWorksAsync(WorkMetadataLocales.InstanceDefault, now, cancellationToken);

        foreach (var batch in cache.ListCachedKeys(now - Lease).Chunk(SweepBatchSize))
        {
            var referenced = await store.FindReferencedCacheKeysAsync(batch, cancellationToken);
            foreach (var orphan in batch.Where(key => !referenced.Contains(key)))
            {
                cache.Delete(orphan);
            }
        }
    }
}
