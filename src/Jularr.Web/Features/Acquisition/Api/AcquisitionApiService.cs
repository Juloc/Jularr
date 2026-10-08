using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.Api;

/// <summary>
/// The one façade the acquisition automation API endpoints call. Every write goes through the
/// same canonical services the owner Razor Pages use (<see cref="AnimeAcquisitionPipeline"/>,
/// <see cref="AnimeAcquisitionScheduler"/>, <see cref="AnimeImportExecutor"/>) — this class only
/// adds API-shaped request/response mapping and a Sonarr-ownership pre-check that turns an
/// otherwise silent no-op into a clear problem-details refusal.
/// </summary>
public sealed class AcquisitionApiService(
    AppDbContext db,
    AnimeAcquisitionPipeline pipeline,
    AnimeAcquisitionScheduler scheduler,
    AnimeImportStore importStore,
    AnimeImportExecutor importExecutor,
    AcquisitionHistoryService history,
    IndexerStore indexerStore,
    DownloadClientStore downloadClientStore,
    AcquisitionHealthStore healthStore)
{
    public const string SearchRequestOperationKind = "anime-search-request";

    public async Task<IReadOnlyList<MonitoredAnimeResponse>> GetMonitoredAsync(CancellationToken cancellationToken)
    {
        var overview = await pipeline.GetOverviewAsync(cancellationToken);
        return overview.Monitored.Select(MonitoredAnimeResponse.From).ToArray();
    }

    public async Task<AnimeMonitoringResponse> GetAnimeMonitoringAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var panel = await pipeline.GetAnimePanelAsync(animeId, cancellationToken)
            ?? throw AcquisitionApiException.NotFound("The requested anime does not exist.");
        return AnimeMonitoringResponse.From(panel);
    }

    public async Task<AnimeMonitoringResponse> SetAnimeMonitoringAsync(
        Guid animeId,
        SetAnimeMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var panel = await pipeline.GetAnimePanelAsync(animeId, cancellationToken)
            ?? throw AcquisitionApiException.NotFound("The requested anime does not exist.");
        if (!panel.CanAcquire)
        {
            throw AcquisitionApiException.SonarrOwned(panel.AnimeKey, panel.Mode);
        }

        AnimeSettingsUpdate? update;
        try
        {
            update = await pipeline.UpdateAnimeSettingsAsync(
                animeId,
                request.Monitored,
                request.SearchOnAdd,
                string.IsNullOrWhiteSpace(request.QualityProfileId) ? null : request.QualityProfileId.Trim(),
                request.IndexerIds ?? [],
                cancellationToken,
                request.TargetRootId);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw AcquisitionApiException.BadRequest(exception.Message);
        }

        if (update is null)
        {
            throw AcquisitionApiException.NotFound("The requested anime does not exist.");
        }

        if (update.StartedMonitoring && request.SearchOnAdd)
        {
            scheduler.RequestRun(update.AnimeKey, AnimeSearchTrigger.SearchOnAdd);
        }

        var refreshed = await pipeline.GetAnimePanelAsync(animeId, cancellationToken)
            ?? throw AcquisitionApiException.NotFound("The requested anime does not exist.");
        return AnimeMonitoringResponse.From(refreshed);
    }

    public async Task<IReadOnlyList<WantedEpisodeResponse>> GetWantedAsync(
        Guid? animeId,
        CancellationToken cancellationToken)
    {
        var overview = await pipeline.GetOverviewAsync(cancellationToken);
        var wanted = overview.Wanted.AsEnumerable();
        if (animeId is { } id)
        {
            wanted = wanted.Where(row => row.AnimeId == id);
        }

        return wanted.Select(WantedEpisodeResponse.From).ToArray();
    }

    /// <summary>Queues a search for every monitored anime, the same request "Search now" sends.</summary>
    public Task<SearchQueuedResponse> SearchAllMonitoredAsync(CancellationToken cancellationToken) =>
        QueueSearchAsync(null, cancellationToken);

    /// <summary>Queues a search for one anime's wanted episodes, refusing a Sonarr-owned anime.</summary>
    public async Task<SearchQueuedResponse> SearchAnimeAsync(Guid animeId, CancellationToken cancellationToken)
    {
        var panel = await pipeline.GetAnimePanelAsync(animeId, cancellationToken)
            ?? throw AcquisitionApiException.NotFound("The requested anime does not exist.");
        if (!panel.CanAcquire)
        {
            throw AcquisitionApiException.SonarrOwned(panel.AnimeKey, panel.Mode);
        }

        return await QueueSearchAsync(panel.AnimeKey, cancellationToken);
    }

    private async Task<SearchQueuedResponse> QueueSearchAsync(string? animeKey, CancellationToken cancellationToken)
    {
        // The one canonical run gate every "Search now" request goes through, whether from the
        // owner UI or here; a full queue is reported as a clear refusal instead of being silently
        // dropped.
        if (!scheduler.RequestRun(animeKey, AnimeSearchTrigger.Manual))
        {
            throw AcquisitionApiException.Conflict(
                "Too many searches are already queued; try again once they finish.");
        }

        var message = animeKey is null
            ? "Search for all monitored anime queued. Decisions appear as acquisition operations and history."
            : $"Search for '{animeKey}' queued. Decisions appear as acquisition operations and history.";

        var operations = new OperationStore(db);
        var operationId = await operations.CreateAsync(
            new OperationDescriptor(
                SearchRequestOperationKind,
                AnimeAcquisitionPipeline.OperationCategory,
                "Search request",
                animeKey ?? "All monitored anime",
                Lane: OperationLane.Interactive,
                Retryable: false),
            cancellationToken);
        await operations.MarkRunningAsync(operationId, cancellationToken);
        await operations.MarkSucceededAsync(operationId, message, cancellationToken);

        return new SearchQueuedResponse(operationId, message);
    }

    public async Task<IReadOnlyList<OperationResponse>> ListOperationsAsync(
        string? status,
        string? kind,
        int limit,
        CancellationToken cancellationToken)
    {
        OperationStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse(status, ignoreCase: true, out OperationStatus parsed))
            {
                throw AcquisitionApiException.BadRequest(
                    $"status must be one of {string.Join(", ", Enum.GetNames<OperationStatus>())}.");
            }

            parsedStatus = parsed;
        }

        var operations = new OperationStore(db);
        var snapshots = await operations.ListAsync(
            new OperationListFilter(
                // Without a specific kind, scope the default view to the pipeline's own
                // search/grab operations; a caller after import or download operations passes
                // their kind explicitly (for example "anime-import").
                Category: kind is null ? AnimeAcquisitionPipeline.OperationCategory : null,
                Status: parsedStatus,
                Kind: kind,
                Limit: Math.Clamp(limit <= 0 ? 50 : limit, 1, 200)),
            cancellationToken);
        return snapshots.Select(OperationResponse.From).ToArray();
    }

    public async Task<OperationDetailResponse> GetOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var operations = new OperationStore(db);
        var snapshot = await operations.GetAsync(operationId, cancellationToken)
            ?? throw AcquisitionApiException.NotFound("The requested operation does not exist.");
        var logs = await operations.ListLogsAsync(
            new OperationLogFilter(OperationId: operationId, Limit: 200),
            cancellationToken);
        return new OperationDetailResponse(
            OperationResponse.From(snapshot),
            logs.Select(OperationLogEntryResponse.From).ToArray());
    }

    public async Task<IReadOnlyList<AcquisitionHistoryEntryResponse>> GetHistoryAsync(
        Guid? animeId,
        int limit,
        CancellationToken cancellationToken)
    {
        var clampedLimit = Math.Clamp(limit <= 0 ? 30 : limit, 1, 200);
        var entries = animeId is { } id
            ? await history.ForAnimeAsync(id, clampedLimit, cancellationToken)
            : await history.RecentAsync(clampedLimit, cancellationToken);
        return entries.Select(AcquisitionHistoryEntryResponse.From).ToArray();
    }

    public async Task<IReadOnlyList<ManualImportItemResponse>> GetManualImportsAsync(
        bool needingAttentionOnly,
        CancellationToken cancellationToken)
    {
        var state = await importStore.LoadAsync(cancellationToken);
        var records = needingAttentionOnly
            ? state.Imports.Where(record => record.NeedsAttention)
            : state.Imports;
        return records
            .OrderByDescending(record => record.UpdatedAtUtc)
            .Select(ManualImportItemResponse.From)
            .ToArray();
    }

    public async Task<AcquisitionActionResponse> ResolveManualImportAsync(
        Guid recordId,
        ResolveManualImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw AcquisitionApiException.BadRequest("sourcePath is required.");
        }

        if (await importStore.GetAsync(recordId, cancellationToken) is null)
        {
            throw AcquisitionApiException.NotFound("The requested manual-import record does not exist.");
        }

        var result = await importExecutor.ImportManuallyAsync(
            recordId,
            request.SourcePath,
            request.Season,
            request.Episode,
            cancellationToken);
        if (result.Success)
        {
            await scheduler.RunExclusiveAsync(
                async (runner, token) =>
                {
                    await runner.ReconcileAttemptsAsync(token);
                    return true;
                },
                cancellationToken);
        }

        return new AcquisitionActionResponse(result.Success, result.Message);
    }

    public async Task<AcquisitionActionResponse> DismissManualImportAsync(
        Guid recordId,
        CancellationToken cancellationToken)
    {
        if (await importStore.GetAsync(recordId, cancellationToken) is null)
        {
            throw AcquisitionApiException.NotFound("The requested manual-import record does not exist.");
        }

        var result = await importExecutor.DismissAsync(recordId, cancellationToken);
        return new AcquisitionActionResponse(result.Success, result.Message);
    }

    public async Task<AcquisitionHealthSummaryResponse> GetHealthSummaryAsync(CancellationToken cancellationToken)
    {
        var indexers = await indexerStore.LoadAllAsync(cancellationToken);
        var downloadClients = await downloadClientStore.LoadAllAsync(cancellationToken);
        var health = await healthStore.LoadAsync(cancellationToken);

        AcquisitionHealthEntryResponse ToEntry(Guid id, string name, bool enabled, AcquisitionHealthKind kind)
        {
            var status = health.Statuses.FirstOrDefault(entry => entry.Kind == kind && entry.EntryId == id);
            return new AcquisitionHealthEntryResponse(
                id,
                name,
                enabled,
                status?.IsHealthy,
                status?.LastError,
                status?.LastCheckedUtc);
        }

        return new AcquisitionHealthSummaryResponse(
            indexers
                .Select(entry => ToEntry(entry.Id, entry.Name, entry.Enabled, AcquisitionHealthKind.Indexer))
                .ToArray(),
            downloadClients
                .Select(entry => ToEntry(entry.Id, entry.Name, entry.Enabled, AcquisitionHealthKind.DownloadClient))
                .ToArray());
    }
}
