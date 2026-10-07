using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Performance;
using Jularr.Web.Infrastructure;

namespace Jularr.Web.Features.Franchises;

/// <summary>Wakes the franchise refresh worker, for example after a follow or a manual refresh.</summary>
public sealed class FranchiseRefreshSignal : BackgroundWakeSignal;

/// <summary>
/// Background refresh of followed franchises. Each run of <see cref="FranchiseService.RefreshAsync"/>
/// is bounded; the worker keeps going while franchises have due members, pauses as long as AniList
/// asks after a 429, and otherwise sleeps until a follow or manual refresh wakes it or
/// <see cref="Interval"/> has passed.
/// </summary>
public sealed class FranchiseRefreshService(
    IServiceScopeFactory scopes,
    FranchiseRefreshSignal signal,
    ILogger<FranchiseRefreshService> logger,
    TimeProvider clock,
    BackgroundWorkGovernor? governor = null) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Upper bound of runs before the worker sleeps again, so one wake-up cannot spin.</summary>
    public const int MaxRunsPerWake = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await signal.WaitAsync(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = Interval;
                try
                {
                    wait = await governor.RunGovernedAsync(BackgroundWorkClass.ProviderRefresh, "Franchise.Refresh", RefreshDueAsync, stoppingToken) ?? Interval;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Refreshing followed franchises failed.");
                }

                await signal.WaitAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs until nothing is due; returns the pause AniList or a failure asked for.</summary>
    private async Task<TimeSpan?> RefreshDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<FranchiseStore>();
        var service = scope.ServiceProvider.GetRequiredService<FranchiseService>();
        var modules = scope.ServiceProvider.GetService<IInstanceModuleService>();
        var instance = modules is null
            ? null
            : await modules.GetAsync(cancellationToken);
        var runs = 0;
        while (runs < MaxRunsPerWake)
        {
            var due = await store.ListRefreshDueAsync(
                clock.GetUtcNow().UtcDateTime - FranchiseService.MemberRecheckAfter,
                cancellationToken);
            if (due.Count == 0)
            {
                return null;
            }

            if (instance is not null)
            {
                var enabled = new List<Guid>(due.Count);
                foreach (var franchiseId in due)
                {
                    var summary = await store.GetAsync(franchiseId, cancellationToken);
                    if (summary is not null
                        && instance.IsEnabled(
                            InstanceModuleMedia.For(
                                WorkMediaTypes.FromWatchlist(summary.Seed.MediaType))))
                    {
                        enabled.Add(franchiseId);
                    }
                }

                due = enabled;
                if (due.Count == 0)
                {
                    return null;
                }
            }

            foreach (var franchiseId in due)
            {
                var result = await service.RefreshAsync(franchiseId, cancellationToken);
                runs++;
                if (result.RetryAfter is { } retryAfter)
                {
                    logger.LogInformation(
                        "Franchise refresh pauses for {Seconds:0} s.",
                        retryAfter.TotalSeconds);
                    return retryAfter;
                }

                if (runs >= MaxRunsPerWake)
                {
                    break;
                }
            }
        }

        return null;
    }
}
