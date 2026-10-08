using System.Collections.Concurrent;
using Jularr.Web.Features.Acquisition.AniListAutoMonitor;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.Acquisition.Pipeline;

/// <summary>
/// Decides when the anime pipeline runs, but owns no loop: the shared Wanted pass calls <see cref="AdvanceAsync"/> (through
/// <see cref="AnimeWantedSource"/>) on its own cadence. A pass recovers persisted import/attempt state once after startup, runs the
/// owner-requested searches and runs the pipeline for all monitored anime when the canonical interval stored in the monitoring state
/// has elapsed. Every pipeline run (periodic, requested, interactive grab) goes through one gate: at most one run is active, so two runs
/// can never grab the same episode, and each run is bounded by the pipeline's search limits.
/// </summary>
public sealed class AnimeAcquisitionScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<AnimeAcquisitionScheduler> logger,
    WantedPassTrigger? wanted = null)
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan AniListAutoMonitorInterval = TimeSpan.FromMinutes(30);
    private const int MaxQueuedRequests = 50;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentQueue<AnimeAcquisitionRunRequest> requests = new();
    private bool recovered;

    public DateTimeOffset? LastRunAtUtc { get; private set; }
    public AnimeAcquisitionRunSummary? LastRun { get; private set; }
    public string? LastRunError { get; private set; }
    public DateTimeOffset? NextRunAtUtc { get; private set; }
    public bool IsRunning => gate.CurrentCount == 0;
    public int QueuedRequests => requests.Count;

    /// <summary>
    /// Queues a run (all monitored anime when <paramref name="animeKey"/> is null) and asks the Wanted pass to take it now. Returns false when
    /// too many requests are already waiting.
    /// </summary>
    public bool RequestRun(string? animeKey = null, AnimeSearchTrigger trigger = AnimeSearchTrigger.Manual)
    {
        if (requests.Count >= MaxQueuedRequests)
        {
            return false;
        }

        requests.Enqueue(new AnimeAcquisitionRunRequest(animeKey, trigger));
        wanted?.Request();
        return true;
    }

    /// <summary>Runs an action against the pipeline while holding the run gate.</summary>
    public async Task<T> RunExclusiveAsync<T>(
        Func<AnimeAcquisitionPipeline, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            throw new InvalidOperationException("Anime module is disabled.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<AnimeAcquisitionPipeline>(), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AnimeAcquisitionRunSummary> RunNowAsync(
        string? animeKey,
        AnimeSearchTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            return new AnimeAcquisitionRunSummary(
                0,
                0,
                0,
                ["Anime module is disabled."]);
        }

        return await RunExclusiveAsync(
            async (_, token) =>
            {
                await ResumeImportsAsync(token);
                await using var scope = scopeFactory.CreateAsyncScope();
                var started = await scope.ServiceProvider.GetRequiredService<AnimeRequestStarter>().StartAsync(animeKey, token);
                var summary = new AnimeAcquisitionRunSummary(started, 0, 0, [started == 0 ? "Nothing is monitored that can be requested." : "The requests of the monitored anime are searched by the Wanted pass."]);
                if (animeKey is null)
                {
                    LastRunAtUtc = DateTimeOffset.UtcNow;
                    LastRun = summary;
                    LastRunError = null;
                }

                return summary;
            },
            cancellationToken);
    }

    /// <summary>
    /// Startup recovery: resumes interrupted or missed imports and brings monitoring attempts in
    /// line with the acquisition relation and Operations. Safe to run more than once.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            return 0;
        }

        return await RunExclusiveAsync(
            async (pipeline, token) =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var recovered = await scope.ServiceProvider
                    .GetRequiredService<AnimeImportRecovery>()
                    .RecoverAsync(token);
                await pipeline.ReconcileAttemptsAsync(token);
                return recovered;
            },
            cancellationToken);
    }

    /// <summary>
    /// One step of the shared Wanted pass: recovery after startup, then the queued runs, otherwise the periodic run when it is due.
    /// Returns how many pipeline runs it made.
    /// </summary>
    public async Task<int> AdvanceAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            return 0;
        }

        if (!recovered)
        {
            try
            {
                var resumed = await RecoverAsync(cancellationToken);
                recovered = true;
                if (resumed > 0)
                {
                    logger.LogInformation("Resumed {Count} anime import(s) after startup.", resumed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Anime acquisition recovery after startup failed; the next pass retries it.");
            }
        }

        NextRunAtUtc ??= nowUtc + StartupDelay;
        if (!requests.IsEmpty)
        {
            return await RunRequestsAsync(cancellationToken);
        }

        if (nowUtc < NextRunAtUtc)
        {
            return 0;
        }

        // Searching is the requests' business (the shared Wanted pass, their own back-off); the periodic step keeps the monitored list in line with the owner's AniList lists.
        NextRunAtUtc = nowUtc + AniListAutoMonitorInterval;
        await RunAniListAutoMonitorAsync(cancellationToken);
        return 1;
    }

    // Imports deferred while a library scan or rename ran, or missed by the SABnzbd monitor,
    // continue before every run so their episodes are no longer wanted.
    private async Task ResumeImportsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AnimeImportRecovery>().RecoverAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Pending anime imports could not be resumed; the next run retries them.");
        }
    }

    private async Task<int> RunRequestsAsync(CancellationToken stoppingToken)
    {
        var pending = new List<AnimeAcquisitionRunRequest>();
        while (requests.TryDequeue(out var request))
        {
            pending.Add(request);
        }

        // A full run covers every per-anime request queued with it.
        var batch = pending.Any(request => request.AnimeKey is null)
            ? [pending.First(request => request.AnimeKey is null)]
            : pending.DistinctBy(request => request.AnimeKey, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var request in batch)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await RunSafelyAsync(request.AnimeKey, request.Trigger, stoppingToken);
        }

        return batch.Length;
    }

    private async Task RunSafelyAsync(
        string? animeKey,
        AnimeSearchTrigger trigger,
        CancellationToken stoppingToken)
    {
        if (!await IsAnimeEnabledAsync(stoppingToken))
        {
            return;
        }

        try
        {
            var summary = await RunNowAsync(animeKey, trigger, stoppingToken);
            logger.LogInformation(
                "Anime acquisition run ({Trigger}, {Scope}) finished: {Summary}",
                trigger,
                animeKey ?? "all monitored anime",
                summary);
            if (animeKey is null)
            {
                await RunAniListAutoMonitorAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (animeKey is null)
            {
                LastRunAtUtc = DateTimeOffset.UtcNow;
                LastRunError = exception.Message;
            }

            logger.LogWarning(exception, "Anime acquisition run failed.");
        }
    }

    // P1 item 7: after every full run, auto-monitor anime on each opted-in profile's AniList
    // Current/Planning lists that already exist locally. Best-effort: a failure here never fails
    // the acquisition run itself and is retried on the next full run.
    private async Task RunAniListAutoMonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = await scope.ServiceProvider
                .GetRequiredService<AniListAutoMonitorSettingsStore>()
                .LoadAsync(cancellationToken);
            var enabledProfiles = settings.Profiles
                .Where(pair => pair.Value.Enabled)
                .Select(pair => pair.Key)
                .ToArray();
            if (enabledProfiles.Length == 0)
            {
                return;
            }

            var service = scope.ServiceProvider.GetRequiredService<AniListAutoMonitorService>();
            foreach (var profileId in enabledProfiles)
            {
                var result = await service.RunForProfileAsync(profileId, cancellationToken);
                if (result.NewlyMonitored > 0)
                {
                    logger.LogInformation(
                        "AniList auto-monitor for profile {ProfileId}: {NewlyMonitored} anime newly monitored from {ListEntries} Current/Planning list entries.",
                        profileId,
                        result.NewlyMonitored,
                        result.ListEntries);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            logger.LogWarning(exception, "AniList list auto-monitor pass failed; the next run retries it.");
        }
    }

    private async Task<bool> IsAnimeEnabledAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var modules = scope.ServiceProvider.GetService<IInstanceModuleService>();
        if (modules is null)
        {
            return true;
        }

        var instance = await modules.GetAsync(cancellationToken);
        return instance.IsEnabled(InstanceModule.Anime)
            && instance.IsEnabled(InstanceModule.Acquisition);
    }
}

public sealed record AnimeAcquisitionRunRequest(
    string? AnimeKey,
    AnimeSearchTrigger Trigger);
