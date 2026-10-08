using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Performance;

namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>
/// The one completed-download import step for every media type (request-backed Manga, Light Novel
/// and Book downloads, manual downloads and Anime): resolve the completed path from the exact
/// download client, apply the media type's remote-path mapping, hand the files to the media type's
/// importer through <see cref="CompletedDownloadDispatcher"/> and record on the download Operation
/// what happened (reported path, mapped path, destination, import mode, result). Request state
/// stays with Wanted and Anime's episode state with its import record; this class never grabs
/// another release.
/// </summary>
public sealed class CompletedDownloadImportService(
    ICompletedDownloadLocationResolver locations,
    CompletedDownloadDispatcher dispatcher,
    AppDbContext db,
    AcquisitionAccessStore requests,
    ILogger<CompletedDownloadImportService> logger,
    BackgroundWorkGovernor? governor = null)
{
    /// <summary>
    /// Operation kind of a download the owner sent by hand (an NZB URL or file), not for a
    /// request. Its media type comes from the download routing details.
    /// </summary>
    public const string ManualDownloadOperationKind = "sabnzbd-download";

    /// <summary>
    /// How long a finished download may wait for its files (a purged SABnzbd history entry,
    /// an unmapped path, an offline share) before Jularr stops trying and asks the owner.
    /// </summary>
    public static readonly TimeSpan CompletedImportTimeout = TimeSpan.FromHours(24);

    public const int MaxManualImportsPerPass = 25;

    public async Task<CompletedDownloadImportResult> ImportAsync(
        OperationSnapshot operation,
        MediaAcquisitionKind kind,
        AcquisitionRequest? request,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var location = await locations.ResolveAsync(operation, kind, cancellationToken);
        return await ImportAtAsync(operation, kind, request, location, nowUtc, cancellationToken);
    }

    /// <summary>
    /// Imports from a location that is already known: the same steps as
    /// <see cref="ImportAsync"/> without asking the download client again. Used to resume an
    /// interrupted import from the mapped path it recorded, which stays readable after the download
    /// client dropped the job from its history.
    /// </summary>
    public async Task<CompletedDownloadImportResult> ImportAtAsync(
        OperationSnapshot operation,
        MediaAcquisitionKind kind,
        AcquisitionRequest? request,
        CompletedDownloadLocation location,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(location);

        if (!location.Resolved || string.IsNullOrWhiteSpace(location.SourcePath))
        {
            var waiting = CompletedDownloadImportResult.RetryLater(location.Message);
            await RecordAsync(operation, location, waiting, nowUtc, cancellationToken);
            return waiting;
        }

        await RecordPhaseAsync(
            operation,
            location,
            new CompletedDownloadImportProgress(
                CompletedDownloadImportPhase.Verifying,
                "Verifying completed files before import."),
            nowUtc,
            cancellationToken);
        await RecordPhaseAsync(
            operation,
            location,
            new CompletedDownloadImportProgress(
                CompletedDownloadImportPhase.Importing,
                "Importing completed files into the library."),
            nowUtc,
            cancellationToken);

        var result = await governor.RunGovernedAsync(
            BackgroundWorkClass.Import,
            "Import.Execute",
            token => dispatcher.DispatchAsync(
                new CompletedDownloadImportRequest(request, operation, location.SourcePath, kind, progress => RecordPhaseAsync(operation, location, progress, nowUtc, token)),
                token),
            cancellationToken);
        await RecordAsync(operation, location, result, nowUtc, cancellationToken);
        return result;
    }

    /// <summary>Records that waiting for the files timed out, so the operation shows why nothing was imported.</summary>
    public Task RecordGaveUpAsync(
        OperationSnapshot operation,
        string message,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        WriteAsync(
            operation,
            previous => new DownloadImportDetails(
                DownloadImportState.GaveUp,
                message,
                nowUtc,
                previous?.ReportedPath,
                previous?.LocalPath,
                previous?.Destination,
                previous?.Mode),
            cancellationToken);

    /// <summary>
    /// Imports finished manual downloads (no request) of every media type with an importer.
    /// Each is imported once: a completed or rejected import is recorded on the operation and
    /// never repeated; a download whose files stay unavailable gives up after
    /// <see cref="CompletedImportTimeout"/>. Returns how many downloads were handled.
    /// </summary>
    public async Task<int> ImportManualDownloadsAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var store = new OperationStore(db);
        var finished = await store.ListAsync(
            new OperationListFilter(
                Status: OperationStatus.Succeeded,
                Kind: ManualDownloadOperationKind,
                Limit: 100),
            cancellationToken);

        var handled = 0;
        foreach (var operation in finished.OrderBy(item => item.FinishedAtUtc ?? item.UpdatedAtUtc))
        {
            if (handled >= MaxManualImportsPerPass)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!DownloadOperationDetails.TryParse(operation.Details, out var details) ||
                details is null ||
                !dispatcher.Supports(details.MediaKind) ||
                IsFinished(details.Import?.State) ||
                string.IsNullOrWhiteSpace(operation.ExternalId))
            {
                continue;
            }

            if (await requests.FindByOperationAsync(operation.Id, cancellationToken) is not null)
            {
                // Request-backed downloads belong to Wanted.
                continue;
            }

            var finishedAt = operation.FinishedAtUtc ?? operation.UpdatedAtUtc;
            if (nowUtc - finishedAt >= CompletedImportTimeout)
            {
                await RecordGaveUpAsync(
                    operation,
                    $"{details.Import?.Result ?? "The completed download could not be imported."} Gave up importing {CompletedImportTimeout.TotalHours:0} hours after the download finished.",
                    nowUtc,
                    cancellationToken);
                handled++;
                continue;
            }

            var result = await ImportAsync(
                operation,
                details.MediaKind,
                request: null,
                nowUtc,
                cancellationToken);
            if (result.Disposition != CompletedDownloadImportDisposition.RetryLater)
            {
                logger.LogInformation(
                    "Manual {MediaKind} download {OperationId} import finished: {Result}",
                    details.MediaKind,
                    operation.Id,
                    result.Message);
            }

            handled++;
        }

        return handled;
    }

    /// <summary>
    /// The import ended and is never repeated: it completed, the package was rejected, the importer
    /// needs the owner (review) or failed, or waiting for the files timed out.
    /// </summary>
    public static bool IsFinished(DownloadImportState? state) =>
        state is DownloadImportState.Completed or
            DownloadImportState.Rejected or
            DownloadImportState.GaveUp or
            DownloadImportState.ManualReview or
            DownloadImportState.Failed;

    private Task RecordAsync(
        OperationSnapshot operation,
        CompletedDownloadLocation location,
        CompletedDownloadImportResult result,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        DownloadImportRecorder.RecordResultAsync(
            new OperationStore(db),
            operation.Id,
            result,
            location,
            nowUtc,
            cancellationToken);

    private Task RecordPhaseAsync(
        OperationSnapshot operation,
        CompletedDownloadLocation location,
        CompletedDownloadImportProgress progress,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        WriteAsync(
            operation,
            previous => new DownloadImportDetails(
                progress.Phase switch
                {
                    CompletedDownloadImportPhase.Verifying => DownloadImportState.Verifying,
                    CompletedDownloadImportPhase.Importing => DownloadImportState.Importing,
                    CompletedDownloadImportPhase.MatchingMetadata => DownloadImportState.MatchingMetadata,
                    _ => throw new ArgumentOutOfRangeException(nameof(progress))
                },
                progress.Message,
                nowUtc,
                location.ReportedPath ?? previous?.ReportedPath,
                location.Resolved ? location.SourcePath : previous?.LocalPath,
                progress.Placement?.Destination ?? previous?.Destination,
                progress.Placement is { } placement ? placement.Mode : previous?.Mode),
            cancellationToken);

    private Task WriteAsync(
        OperationSnapshot operation,
        Func<DownloadImportDetails?, DownloadImportDetails> build,
        CancellationToken cancellationToken) =>
        DownloadImportRecorder.RecordAsync(new OperationStore(db), operation.Id, build, cancellationToken);
}

/// <summary>
/// Writes the import side of an external download onto its Operation (the one status store),
/// for every media importer including Anime: reported path, mapped path, destination, import
/// mode and result. Each change of state or result is also logged once.
/// </summary>
public static class DownloadImportRecorder
{
    /// <summary>
    /// Records what an importer reported on the download Operation: the state the disposition
    /// stands for, its message, the placement (destination and import mode) and, when the
    /// download's <paramref name="location"/> is known, the reported and mapped paths. Anything the
    /// result or location does not carry keeps its earlier value.
    /// </summary>
    public static Task RecordResultAsync(
        OperationStore store,
        Guid downloadOperationId,
        CompletedDownloadImportResult result,
        CompletedDownloadLocation? location,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        RecordAsync(
            store,
            downloadOperationId,
            previous => new DownloadImportDetails(
                StateFor(result.Disposition),
                result.Message,
                nowUtc,
                location?.ReportedPath ?? previous?.ReportedPath,
                location is { Resolved: true } ? location.SourcePath : previous?.LocalPath,
                result.Placement?.Destination ?? previous?.Destination,
                result.Placement is { } placement ? placement.Mode : previous?.Mode),
            cancellationToken);

    public static DownloadImportState StateFor(CompletedDownloadImportDisposition disposition) =>
        disposition switch
        {
            CompletedDownloadImportDisposition.Completed => DownloadImportState.Completed,
            CompletedDownloadImportDisposition.RejectedRelease => DownloadImportState.Rejected,
            CompletedDownloadImportDisposition.NeedsReview => DownloadImportState.ManualReview,
            CompletedDownloadImportDisposition.Failed => DownloadImportState.Failed,
            _ => DownloadImportState.Waiting
        };

    public static async Task RecordAsync(
        OperationStore store,
        Guid downloadOperationId,
        Func<DownloadImportDetails?, DownloadImportDetails> build,
        CancellationToken cancellationToken)
    {
        // Downloads submitted before routing details existed have nowhere to record the import.
        var current = await store.GetAsync(downloadOperationId, cancellationToken);
        if (current is null ||
            !DownloadOperationDetails.TryParse(current.Details, out var details) ||
            details is null)
        {
            return;
        }

        var previous = details.Import;
        var next = build(previous);
        await store.SetDetailsAsync(
            downloadOperationId,
            (details with { Import = next }).Serialize(),
            cancellationToken);

        // The log keeps each change of state or result once, not every retry of the same wait.
        if (previous is null || previous.State != next.State || previous.Result != next.Result)
        {
            await store.AppendLogAsync(
                downloadOperationId,
                next.State switch
                {
                    DownloadImportState.Completed => OperationLogLevel.Information,
                    DownloadImportState.Waiting => OperationLogLevel.Information,
                    DownloadImportState.Verifying => OperationLogLevel.Information,
                    DownloadImportState.Importing => OperationLogLevel.Information,
                    DownloadImportState.MatchingMetadata => OperationLogLevel.Information,
                    _ => OperationLogLevel.Warning
                },
                "Import",
                next.Result,
                cancellationToken);
        }
    }
}
