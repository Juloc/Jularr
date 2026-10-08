using System.Collections.Concurrent;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Instance;

namespace Jularr.Web.Features.Operations;

public sealed record SabnzbdProjectedFailure(
    OperationSnapshot Operation,
    SabnzbdFailureKind FailureKind,
    string Reason);

public sealed record SabnzbdProjectionResult(
    IReadOnlyList<OperationSnapshot> Completed,
    IReadOnlyList<SabnzbdProjectedFailure> Failed);

/// <summary>
/// Projects SABnzbd queue/history state onto the canonical Operations that
/// carry SABnzbd jobs. Operations are the only place job lifecycle,
/// progress and ETA are stored.
/// </summary>
public static class SabnzbdOperationProjector
{
    public static readonly TimeSpan MissingJobTimeout = TimeSpan.FromMinutes(15);

    public static async Task<SabnzbdProjectionResult> ApplyAsync(
        OperationStore store,
        IReadOnlyList<OperationSnapshot> operations,
        SabnzbdQueueSnapshot queue,
        SabnzbdHistorySnapshot history,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var completed = new List<OperationSnapshot>();
        var failed = new List<SabnzbdProjectedFailure>();

        var queueById = queue.Jobs
            .GroupBy(job => job.NzoId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var historyById = history.Jobs
            .GroupBy(job => job.NzoId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            if (string.IsNullOrWhiteSpace(operation.ExternalId))
            {
                continue;
            }

            if (queueById.TryGetValue(operation.ExternalId, out var queued))
            {
                await EnsureRunningAsync(store, operation, cancellationToken);

                long? completedBytes = queued.SizeBytes is { } total && queued.SizeLeftBytes is { } left
                    ? Math.Clamp(total - left, 0, total)
                    : null;
                DateTime? eta = queued.TimeLeft is { } remaining && remaining > TimeSpan.Zero
                    ? nowUtc.Add(remaining)
                    : null;

                await store.ReportProgressAsync(
                    operation.Id,
                    queued.Percentage is { } percent ? (int)Math.Round(percent) : null,
                    queue.Paused
                        ? "SABnzbd queue is paused."
                        : $"SABnzbd: {queued.Status ?? "Queued"}.",
                    completedBytes,
                    queued.SizeBytes,
                    // SABnzbd only reports the overall queue speed.
                    queue.Jobs.Count == 1 ? queue.BytesPerSecond : null,
                    eta,
                    cancellationToken);
                continue;
            }

            if (historyById.TryGetValue(operation.ExternalId, out var finished))
            {
                if (finished.IsCompleted)
                {
                    await store.MarkSucceededAsync(
                        operation.Id,
                        "SABnzbd download and post-processing completed.",
                        cancellationToken);
                    completed.Add(operation);
                    continue;
                }

                if (finished.IsFailed)
                {
                    var kind = finished.FailureKind == SabnzbdFailureKind.None
                        ? SabnzbdFailureKind.Unknown
                        : finished.FailureKind;
                    var reason = SabnzbdFailureDescriptions.Describe(kind, finished.FailureMessage);
                    await store.MarkFailedAsync(operation.Id, reason, cancellationToken);
                    await RecordFailureKindAsync(store, operation, kind, cancellationToken);
                    failed.Add(new SabnzbdProjectedFailure(operation, kind, reason));
                    continue;
                }

                await EnsureRunningAsync(store, operation, cancellationToken);
                await store.ReportProgressAsync(
                    operation.Id,
                    Math.Max(operation.ProgressPercent ?? 0, 99),
                    $"SABnzbd post-processing: {finished.Status ?? "processing"}.",
                    finished.SizeBytes,
                    finished.SizeBytes,
                    bytesPerSecond: null,
                    etaUtc: null,
                    cancellationToken);
                continue;
            }

            if (nowUtc - operation.UpdatedAtUtc >= MissingJobTimeout)
            {
                const string missing = "SABnzbd job no longer appears in queue or history.";
                await store.MarkFailedAsync(operation.Id, missing, cancellationToken);
                await RecordFailureKindAsync(store, operation, SabnzbdFailureKind.Lost, cancellationToken);
                failed.Add(new SabnzbdProjectedFailure(operation, SabnzbdFailureKind.Lost, missing));
            }
        }

        return new SabnzbdProjectionResult(completed, failed);
    }

    /// <summary>Keeps why the client failed the job next to the job, so the reliability evidence can tell a bad release from a local problem.</summary>
    public static async Task RecordFailureKindAsync(OperationStore store, OperationSnapshot operation, SabnzbdFailureKind kind, CancellationToken cancellationToken)
    {
        if (DownloadOperationDetails.TryParse(operation.Details, out var details) && details is not null)
        {
            await store.SetDetailsAsync(operation.Id, (details with { FailureKind = kind.ToString() }).Serialize(), cancellationToken);
        }
    }

    private static async Task EnsureRunningAsync(
        OperationStore store,
        OperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        if (operation.Status == OperationStatus.Queued)
        {
            await store.MarkRunningAsync(operation.Id, cancellationToken);
        }
    }
}

/// <summary>
/// The one SABnzbd monitor loop for every media type's downloads. It resumes
/// after a restart from the persisted external references on Operations.
/// </summary>
public sealed class SabnzbdOperationMonitorService(
    IServiceScopeFactory scopeFactory,
    DownloadClientStore downloadClients,
    ILogger<SabnzbdOperationMonitorService> logger) : BackgroundService
{
    private static readonly TimeSpan ActivePollInterval =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdlePollInterval =
        TimeSpan.FromSeconds(12);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();
        await RecoverAcquisitionsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var hasActiveJobs = false;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                hasActiveJobs = await PollOnceAsync(
                    scope.ServiceProvider,
                    DateTime.UtcNow,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not refresh SABnzbd operation status.");
            }

            try
            {
                await Task.Delay(
                    hasActiveJobs
                        ? ActivePollInterval
                        : IdlePollInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>How often the same "client unavailable" warning may be logged.</summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(15);

    public const string RemovedClientMessage =
        "The SABnzbd connection used by this download was removed.";

    private readonly ConcurrentDictionary<string, DateTime> lastWarnings = new(StringComparer.Ordinal);

    /// <summary>
    /// One monitor pass. Each download client is polled on its own: an unreachable client only
    /// delays its own downloads. Downloads pinned to a disabled client stay active (the owner may
    /// re-enable it) with a rate-limited warning; downloads pinned to a removed client can never
    /// be resolved and fail, so the media workflows move on.
    /// </summary>
    public async Task<bool> PollOnceAsync(
        IServiceProvider services,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var modules = services.GetService<IInstanceModuleService>();
        if (modules is not null
            && !await modules.IsEnabledAsync(
                InstanceModule.Acquisition,
                cancellationToken))
        {
            return false;
        }

        var store = new OperationStore(
            services.GetRequiredService<AppDbContext>(),
            services.GetService<IJularrEventPublisher>());
        var operations = await store.ListActiveExternalAsync(
            SabnzbdClient.ProviderId,
            cancellationToken);

        if (operations.Count == 0)
        {
            return false;
        }

        var configured = (await downloadClients.LoadAllAsync(cancellationToken))
            .Where(item => item.Type == DownloadClientType.Sabnzbd)
            .ToDictionary(item => item.Id);
        var enabled = configured.Values
            .Where(item => item.Enabled)
            .OrderBy(item => item.Priority)
            .ToArray();

        foreach (var group in operations.GroupBy(SelectedClientId))
        {
            DownloadClientEntry entry;
            if (group.Key is { } selectedClientId)
            {
                if (!configured.TryGetValue(selectedClientId, out entry!))
                {
                    await FailRemovedClientDownloadsAsync(services, store, group.ToArray(), cancellationToken);
                    continue;
                }

                if (!entry.Enabled)
                {
                    if (ShouldWarn($"disabled:{selectedClientId}", nowUtc))
                    {
                        logger.LogWarning(
                            "{Count} SABnzbd downloads use the disabled download client {ClientName}; they stay active until it is enabled again or an owner cancels them.",
                            group.Count(),
                            entry.Name);
                    }

                    continue;
                }
            }
            else if (enabled.Length == 0)
            {
                if (ShouldWarn("no-client", nowUtc))
                {
                    logger.LogWarning(
                        "{Count} SABnzbd downloads are active but no SABnzbd download client is enabled.",
                        group.Count());
                }

                continue;
            }
            else
            {
                // Pre-#389 operations did not persist a client ID. Preserve their historical
                // behavior instead of guessing a new mapping for completed jobs.
                entry = enabled[0];
            }

            try
            {
                await PollClientAsync(services, store, entry, group.ToArray(), nowUtc, cancellationToken);
                lastWarnings.TryRemove($"poll:{entry.Id}", out _);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (ShouldWarn($"poll:{entry.Id}", nowUtc))
                {
                    logger.LogWarning(
                        exception,
                        "Could not refresh SABnzbd downloads on {ClientName}; other download clients are still monitored.",
                        entry.Name);
                }
            }
        }

        return true;
    }

    private async Task PollClientAsync(
        IServiceProvider services,
        OperationStore store,
        DownloadClientEntry entry,
        IReadOnlyList<OperationSnapshot> groupOperations,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var client = services.GetRequiredService<ISabnzbdClient>();
        var connection = SabnzbdDownloadClient.ToConnection(entry);
        var queue = await client.GetQueueAsync(connection, cancellationToken);
        var history = await client.GetHistoryAsync(
            connection,
            groupOperations.Select(operation => operation.ExternalId!).ToArray(),
            cancellationToken);

        var result = await SabnzbdOperationProjector.ApplyAsync(
            store,
            groupOperations,
            queue,
            history,
            nowUtc,
            cancellationToken);

        await ImportCompletedAnimeDownloadsAsync(services, result.Completed, cancellationToken);
        await ContinueFailedAnimeAcquisitionsAsync(services, result.Failed, cancellationToken);
    }

    private async Task FailRemovedClientDownloadsAsync(
        IServiceProvider services,
        OperationStore store,
        IReadOnlyList<OperationSnapshot> groupOperations,
        CancellationToken cancellationToken)
    {
        var failed = new List<SabnzbdProjectedFailure>();
        foreach (var operation in groupOperations)
        {
            await store.MarkFailedAsync(operation.Id, RemovedClientMessage, cancellationToken);
            await SabnzbdOperationProjector.RecordFailureKindAsync(store, operation, SabnzbdFailureKind.Lost, cancellationToken);
            failed.Add(new SabnzbdProjectedFailure(operation, SabnzbdFailureKind.Lost, RemovedClientMessage));
        }

        logger.LogWarning(
            "Failed {Count} SABnzbd downloads because their download client was removed.",
            failed.Count);
        await ContinueFailedAnimeAcquisitionsAsync(services, failed, cancellationToken);
    }

    private bool ShouldWarn(string key, DateTime nowUtc)
    {
        if (lastWarnings.TryGetValue(key, out var last) && nowUtc - last < WarningInterval)
        {
            return false;
        }

        lastWarnings[key] = nowUtc;
        return true;
    }

    private static Guid? SelectedClientId(OperationSnapshot operation) =>
        DownloadOperationDetails.TryParse(operation.Details, out var details)
            ? details!.ClientEntryId
            : null;

    private async Task ImportCompletedAnimeDownloadsAsync(
        IServiceProvider services,
        IReadOnlyList<OperationSnapshot> completed,
        CancellationToken cancellationToken)
    {
        var modules = services.GetService<IInstanceModuleService>();
        if (modules is not null
            && !await modules.IsEnabledAsync(
                InstanceModule.Anime,
                cancellationToken))
        {
            return;
        }

        foreach (var operation in completed.Where(AnimeImportExecutor.IsAnimeDownload))
        {
            try
            {
                // The shared import step: the exact download client's completed path, the Anime
                // remote path mapping, then the Anime importer behind the dispatcher.
                await services.GetRequiredService<CompletedDownloadImportService>()
                    .ImportAsync(
                        operation,
                        MediaAcquisitionKind.Anime,
                        request: null,
                        DateTime.UtcNow,
                        cancellationToken);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or InvalidDataException
                    or IOException
                    or UnauthorizedAccessException)
            {
                // The import executor recovers unfinished imports on the next start.
                logger.LogWarning(
                    exception,
                    "Could not import the completed anime download of operation {OperationId}.",
                    operation.Id);
            }
        }
    }

    private async Task RecoverAcquisitionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var modules = scope.ServiceProvider.GetService<IInstanceModuleService>();
            if (modules is not null)
            {
                var instance = await modules.GetAsync(cancellationToken);
                if (!instance.IsEnabled(InstanceModule.Acquisition)
                    || !instance.IsEnabled(InstanceModule.Anime))
                {
                    return;
                }
            }

            var advanced = await scope.ServiceProvider
                .GetRequiredService<SabnzbdAcquisitionService>()
                .RecoverAsync(cancellationToken);
            if (advanced > 0)
            {
                logger.LogInformation(
                    "Resumed {Count} anime acquisitions whose SABnzbd download failed before the restart.",
                    advanced);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not recover SABnzbd anime acquisitions after startup.");
        }
    }

    private async Task ContinueFailedAnimeAcquisitionsAsync(
        IServiceProvider services,
        IReadOnlyList<SabnzbdProjectedFailure> failures,
        CancellationToken cancellationToken)
    {
        var modules = services.GetService<IInstanceModuleService>();
        if (modules is not null
            && !await modules.IsEnabledAsync(
                InstanceModule.Anime,
                cancellationToken))
        {
            return;
        }

        var animeFailures = failures
            .Where(failure => failure.Operation.Kind == SabnzbdAcquisitionService.OperationKind)
            .ToArray();
        if (animeFailures.Length == 0)
        {
            return;
        }

        var acquisitions = services.GetRequiredService<SabnzbdAcquisitionService>();
        foreach (var failure in animeFailures)
        {
            try
            {
                await acquisitions.HandleFailedAsync(
                    failure.Operation.Id,
                    failure.FailureKind,
                    failure.Reason,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or InvalidDataException
                    or IOException)
            {
                // RecoverAsync retries this acquisition on the next start.
                logger.LogWarning(
                    exception,
                    "Could not continue the anime acquisition after SABnzbd operation {OperationId} failed.",
                    failure.Operation.Id);
            }
        }
    }
}
