using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.Wanted;

/// <summary>
/// Media-specific Wanted policy behind the shared scheduler. The scheduler owns timing and
/// download/import state transitions; handlers only interpret their target payload and tell
/// the canonical request service how to continue after a bad release.
/// </summary>
public interface IWantedRequestHandler
{
    MediaAcquisitionKind Kind { get; }

    bool IsSearchDue(
        AcquisitionRequest request,
        DateTime nowUtc);

    Task ContinueAfterProblemAsync(
        AcquisitionRequest request,
        string problem,
        CancellationToken cancellationToken);

    Task<bool> ContinueAfterCompletedImportAsync(
        AcquisitionRequest request,
        CompletedDownloadImportResult result,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

/// <summary>
/// Generic durable Wanted lifecycle for request-backed media.
///
/// It deliberately does not talk to SABnzbd directly. The shared download monitor projects
/// queue/history state onto Operations; Wanted consumes that canonical state, transitions
/// requests through Downloading -> Importing -> Completed, dispatches imports once, and lets
/// the media handler continue with another release only when the downloaded package itself
/// is unsuitable.
/// </summary>
public sealed class WantedAcquisitionService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<WantedAcquisitionService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    public const int MaxRequestsPerKindPerPass = 25;

    /// <summary>
    /// The most open requests of a monitored media type one pass reads back from its pipeline. A request that is already where its
    /// pipeline is keeps its update time, so unlike the shared download lifecycle this is a safety bound, not a rotation.
    /// </summary>
    public const int MaxFollowedRequestsPerKind = 200;

    /// <summary>
    /// How long a finished download may wait for its files before the request stops waiting and
    /// asks the owner for attention (the shared completed-download import timeout).
    /// </summary>
    public static readonly TimeSpan CompletedImportTimeout = CompletedDownloadImportService.CompletedImportTimeout;

    /// <summary>How long a request may stay Searching without a download operation before the Wanted pass takes it back.</summary>
    public static readonly TimeSpan StaleSearchingAfter = TimeSpan.FromMinutes(30);

    public const string InterruptedSearchMessage = "The previous search was interrupted. Searching again.";

    public const string InterruptedDownloadMessage = "The previous search was interrupted. Checking the download.";

    public const string CancelledMessage =
        "The download was cancelled, so no other release was grabbed. Approve the request again to search.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await ProcessOnceAsync(
                    scope.ServiceProvider,
                    clock.GetUtcNow().UtcDateTime,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not advance Wanted acquisition requests.");
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

    public static async Task<int> ProcessOnceAsync(
        IServiceProvider services,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var modules = services.GetService<IInstanceModuleService>();
        InstanceModuleSettings? instance = null;
        if (modules is not null)
        {
            instance = await modules.GetAsync(cancellationToken);
            if (!instance.IsEnabled(InstanceModule.Acquisition))
            {
                return 0;
            }
        }

        var handlers = services
            .GetServices<IWantedRequestHandler>()
            .GroupBy(handler => handler.Kind)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? group.Single()
                    : throw new InvalidOperationException(
                        $"More than one Wanted handler is registered for {group.Key}."));

        var advanced = 0;
        foreach (var handler in handlers.Values)
        {
            if (instance is not null
                && !instance.IsEnabled(AcquisitionInstanceModules.For(handler.Kind)))
            {
                continue;
            }

            advanced += await RecoverInFlightAsync(
                services,
                handler,
                nowUtc,
                cancellationToken);
            advanced += await RecoverStaleSearchingAsync(
                services,
                handler.Kind,
                nowUtc,
                cancellationToken);
            advanced += await SearchDueAsync(
                services,
                handler,
                nowUtc,
                cancellationToken);
        }

        // Media types whose own monitoring pipeline searches, grabs and imports (Anime) have no download lifecycle of their own to
        // follow here: their requests are brought to the state of that pipeline.
        foreach (var executor in services.GetServices<IAcquisitionRequestExecutor>().OfType<IMonitoredAcquisitionExecutor>())
        {
            if (instance is not null
                && !instance.IsEnabled(AcquisitionInstanceModules.For(executor.Kind)))
            {
                continue;
            }

            advanced += await RecoverStaleSearchingAsync(
                services,
                executor.Kind,
                nowUtc,
                cancellationToken);
            advanced += await FollowMonitoredAsync(
                services,
                executor.Kind,
                cancellationToken);
        }

        // Manual downloads (no request) go through the same importer.
        advanced += await services
            .GetRequiredService<CompletedDownloadImportService>()
            .ImportManualDownloadsAsync(nowUtc, cancellationToken);

        return advanced;
    }

    private static async Task<int> RecoverInFlightAsync(
        IServiceProvider services,
        IWantedRequestHandler handler,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<AcquisitionAccessStore>();
        var operations = new OperationStore(
            services.GetRequiredService<AppDbContext>());
        var imports = services.GetRequiredService<CompletedDownloadImportService>();

        var downloading = await store.ListByStatusAsync(
            handler.Kind,
            AcquisitionRequestStatus.Downloading,
            cancellationToken);
        var importing = await store.ListByStatusAsync(
            handler.Kind,
            AcquisitionRequestStatus.Importing,
            cancellationToken);

        var requests = downloading
            .Concat(importing)
            .GroupBy(request => request.Id)
            .Select(group => group.First())
            .OrderBy(request => request.UpdatedAt)
            .Take(MaxRequestsPerKindPerPass)
            .ToArray();

        var advanced = 0;
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operation = request.OperationId is { } operationId
                ? await operations.GetAsync(operationId, cancellationToken)
                : null;

            if (operation is null)
            {
                await handler.ContinueAfterProblemAsync(
                    request,
                    "The download operation no longer exists.",
                    cancellationToken);
                advanced++;
                continue;
            }

            if (operation.Status == OperationStatus.Cancelled)
            {
                // An owner cancelling a download means "stop", never "try the next release".
                await store.UpdateStatusAsync(
                    request.Id,
                    AcquisitionRequestStatus.Failed,
                    CancelledMessage,
                    operation.Id,
                    resultUrl: null,
                    decidedByProfileId: null,
                    cancellationToken);
                advanced++;
                continue;
            }

            if (operation.Status is
                OperationStatus.Failed or
                OperationStatus.Interrupted)
            {
                var problem = string.IsNullOrWhiteSpace(operation.Error)
                    ? "The download failed."
                    : $"The download failed: {operation.Error.Trim().TrimEnd('.')}.";
                await handler.ContinueAfterProblemAsync(
                    request,
                    problem,
                    cancellationToken);
                advanced++;
                continue;
            }

            if (operation.Status is OperationStatus.Queued or OperationStatus.Running)
            {
                if (request.Status == AcquisitionRequestStatus.Importing)
                {
                    await store.UpdateStatusAsync(
                        request.Id,
                        AcquisitionRequestStatus.Downloading,
                        "Download is still in progress.",
                        operation.Id,
                        resultUrl: null,
                        decidedByProfileId: null,
                        cancellationToken);
                    advanced++;
                }

                continue;
            }

            if (operation.Status != OperationStatus.Succeeded)
            {
                continue;
            }

            if (request.Status != AcquisitionRequestStatus.Importing)
            {
                await store.UpdateStatusAsync(
                    request.Id,
                    AcquisitionRequestStatus.Importing,
                    "Download complete. Importing into the library.",
                    operation.Id,
                    resultUrl: null,
                    decidedByProfileId: null,
                    cancellationToken);
                advanced++;
            }

            var result = await imports.ImportAsync(
                operation,
                request.Kind,
                request,
                nowUtc,
                cancellationToken);

            switch (result.Disposition)
            {
                case CompletedDownloadImportDisposition.Completed:
                    if (await handler.ContinueAfterCompletedImportAsync(
                            request,
                            result,
                            cancellationToken))
                    {
                        advanced++;
                        break;
                    }

                    await store.UpdateStatusAsync(
                        request.Id,
                        AcquisitionRequestStatus.Completed,
                        result.Message,
                        operation.Id,
                        result.ResultUrl,
                        decidedByProfileId: null,
                        cancellationToken);
                    advanced++;
                    break;

                case CompletedDownloadImportDisposition.RetryLater:
                    advanced += await KeepImportingAsync(
                        store,
                        imports,
                        request,
                        operation,
                        result.Message,
                        nowUtc,
                        cancellationToken);
                    break;

                case CompletedDownloadImportDisposition.RejectedRelease:
                    await handler.ContinueAfterProblemAsync(
                        request,
                        result.Message,
                        cancellationToken);
                    advanced++;
                    break;

                case CompletedDownloadImportDisposition.NeedsReview:
                case CompletedDownloadImportDisposition.Failed:
                    // The importer ended the import and keeps what the owner has to resolve. The
                    // release is not at fault, so no other release is grabbed.
                    await store.UpdateStatusAsync(
                        request.Id,
                        AcquisitionRequestStatus.Failed,
                        result.Message,
                        operation.Id,
                        resultUrl: null,
                        decidedByProfileId: null,
                        cancellationToken);
                    advanced++;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return advanced;
    }

    /// <summary>
    /// A finished download whose files cannot be imported yet stays Importing (it is not the
    /// release's fault, so no other release is grabbed) until <see cref="CompletedImportTimeout"/>
    /// has passed since the download finished; then the request fails with the reason so the
    /// owner can fix the path or approve it again. Returns 1 when the request was failed.
    /// </summary>
    private static async Task<int> KeepImportingAsync(
        AcquisitionAccessStore store,
        CompletedDownloadImportService imports,
        AcquisitionRequest request,
        OperationSnapshot operation,
        string message,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var finishedAt = operation.FinishedAtUtc ?? operation.UpdatedAtUtc;
        var timedOut = nowUtc - finishedAt >= CompletedImportTimeout;
        var reason = string.IsNullOrWhiteSpace(message)
            ? "The completed download could not be imported."
            : message.Trim();

        if (timedOut)
        {
            await imports.RecordGaveUpAsync(
                operation,
                $"{reason} Gave up importing {CompletedImportTimeout.TotalHours:0} hours after the download finished.",
                nowUtc,
                cancellationToken);
        }

        await store.UpdateStatusAsync(
            request.Id,
            timedOut
                ? AcquisitionRequestStatus.Failed
                : AcquisitionRequestStatus.Importing,
            timedOut
                ? $"{reason} Gave up importing {CompletedImportTimeout.TotalHours:0} hours after the download finished; fix the download path or approve the request again."
                : reason,
            operation.Id,
            resultUrl: null,
            decidedByProfileId: null,
            cancellationToken);
        return timedOut ? 1 : 0;
    }

    // A request is Searching only while a search or a Manual Search grab runs, which takes seconds. One that stays Searching without a
    // download operation lost its worker (a crash between claiming and finishing) and would otherwise wait forever: it searches again.
    private static async Task<int> RecoverStaleSearchingAsync(
        IServiceProvider services,
        MediaAcquisitionKind kind,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<AcquisitionAccessStore>();
        var recovered = 0;
        foreach (var request in await store.ListByStatusAsync(kind, AcquisitionRequestStatus.Searching, cancellationToken))
        {
            // The claim sets UpdatedAt and nothing else touches it while the status stays Searching, so its age says whether a worker still has it.
            // A request that already has a download linked goes back to Downloading, where its operation is followed, never to a new search.
            var back = request.OperationId is null ? AcquisitionRequestStatus.Approved : AcquisitionRequestStatus.Downloading;
            var message = back == AcquisitionRequestStatus.Approved ? InterruptedSearchMessage : InterruptedDownloadMessage;
            if (nowUtc - request.UpdatedAt >= StaleSearchingAfter
                && await store.TryTransitionStatusAsync(request.Id, [AcquisitionRequestStatus.Searching], back, message, null, cancellationToken) is not null)
            {
                recovered++;
            }
        }

        return recovered;
    }

    /// <summary>
    /// Brings every open request of a monitored media type to the state of its pipeline. Returns how many requests changed; a request
    /// that is already where its pipeline is stays untouched.
    /// </summary>
    private static async Task<int> FollowMonitoredAsync(
        IServiceProvider services,
        MediaAcquisitionKind kind,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<AcquisitionAccessStore>();
        var requestService = services.GetRequiredService<AcquisitionRequestService>();
        var requests = new List<AcquisitionRequest>();
        foreach (var status in AcquisitionAccessNames.UnderwayStatuses)
        {
            requests.AddRange(await store.ListByStatusAsync(kind, status, cancellationToken));
        }

        var followed = 0;
        foreach (var request in requests.OrderBy(item => item.UpdatedAt).Take(MaxFollowedRequestsPerKind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await requestService.FollowMonitoredAsync(request.Id, cancellationToken);
            if (current.Status != request.Status || current.StatusMessage != request.StatusMessage)
            {
                followed++;
            }
        }

        return followed;
    }

    private static async Task<int> SearchDueAsync(
        IServiceProvider services,
        IWantedRequestHandler handler,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<AcquisitionAccessStore>();

        var due = (await store.ListByStatusAsync(
                handler.Kind,
                AcquisitionRequestStatus.Approved,
                cancellationToken))
            .Where(request => handler.IsSearchDue(request, nowUtc))
            .OrderBy(request => request.UpdatedAt)
            .Take(MaxRequestsPerKindPerPass)
            .ToArray();

        if (due.Length == 0)
        {
            return 0;
        }

        var requestService = services.GetRequiredService<AcquisitionRequestService>();
        foreach (var request in due)
        {
            await requestService.ContinueAsync(
                request.Id,
                cancellationToken);
        }

        return due.Length;
    }
}
