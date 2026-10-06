using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.DownloadClients;

public sealed record DownloadSubmissionSpec(
    string OperationKind,
    string Title,
    string? Subject,
    string? ProfileId,
    Uri? Url,
    string? Name,
    MediaAcquisitionKind MediaKind,
    Stream? File = null,
    string? FileName = null,
    string? MediaTargetKey = null,
    OperationPriority Priority = OperationPriority.Normal);

public sealed record DownloadSubmissionOutcome(
    bool Accepted,
    Guid OperationId,
    string? ExternalId,
    Guid? ClientEntryId,
    string Message);

/// <summary>
/// The one path that sends a release to a download client for every media
/// type: picks the highest-priority enabled, healthy client (several
/// SABnzbd connections can be configured), and fails over to the next one
/// on submission failure. Operations remain the single status store; the
/// external reference is the chosen client's entry ID plus its own job ID.
/// </summary>
public sealed class DownloadClientSubmissionService(
    IDownloadClient client,
    DownloadClientSelector selector,
    AppDbContext db,
    ILogger<DownloadClientSubmissionService> logger,
    IJularrEventPublisher? events = null)
{
    public const string OperationCategory = "External downloads";

    public static bool IsDownloadClientOperation(OperationSnapshot operation) =>
        !string.IsNullOrWhiteSpace(operation.ExternalProvider) && !string.IsNullOrWhiteSpace(operation.ExternalId);

    /// <summary>
    /// Offers a release that several indexers returned to the download client source by source: the first source the client accepts wins,
    /// and when none is accepted the last refusal is returned. A release submitted from one source is never submitted again from another.
    /// </summary>
    public async Task<DownloadSubmissionOutcome> SubmitFirstAcceptedAsync(
        IReadOnlyList<Uri> sources,
        Func<Uri, DownloadSubmissionSpec> specFor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(specFor);
        if (sources.Count == 0)
        {
            throw new ArgumentException("A release needs at least one source.", nameof(sources));
        }

        DownloadSubmissionOutcome? outcome = null;
        foreach (var source in sources)
        {
            outcome = await SubmitAsync(specFor(source), cancellationToken);
            if (outcome.Accepted)
            {
                break;
            }
        }

        return outcome!;
    }

    public async Task<DownloadSubmissionOutcome> SubmitAsync(
        DownloadSubmissionSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var store = new OperationStore(db, events);
        var operationId = await store.CreateAsync(
            new OperationDescriptor(
                spec.OperationKind,
                OperationCategory,
                spec.Title,
                spec.Subject,
                spec.ProfileId,
                OperationLane.Normal,
                IsDownload: true,
                Retryable: true,
                Priority: spec.Priority),
            cancellationToken);

        await store.MarkRunningAsync(operationId, cancellationToken);
        await store.ReportProgressAsync(
            operationId, 0, "Selecting a download client.", cancellationToken: cancellationToken);

        var candidates = await selector.SelectAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            const string noClient = "No enabled, healthy download client is configured.";
            await store.MarkFailedAsync(operationId, noClient, CancellationToken.None);
            return new DownloadSubmissionOutcome(false, operationId, null, null, noClient);
        }

        string? lastError = null;
        foreach (var entry in candidates)
        {
            if (spec.File is { CanSeek: true })
            {
                spec.File.Position = 0;
            }

            DownloadClientSubmitResult result;
            try
            {
                result = await client.SubmitAsync(
                    entry,
                    new DownloadClientSubmitRequest(spec.Url, spec.Name, spec.MediaKind, spec.File, spec.FileName, spec.Priority),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or DownloadClientException)
            {
                result = new DownloadClientSubmitResult(false, null, exception.Message);
            }

            if (result.Success && !string.IsNullOrWhiteSpace(result.ExternalId))
            {
                // One write: the job id and the client it went to are never stored apart.
                await store.SetExternalReferenceAsync(
                    operationId,
                    client.ProviderId,
                    result.ExternalId,
                    new DownloadOperationDetails(
                        entry.Id,
                        spec.MediaKind,
                        entry.CategoryFor(spec.MediaKind),
                        TargetKey: string.IsNullOrWhiteSpace(spec.MediaTargetKey)
                            ? null
                            : spec.MediaTargetKey.Trim()).Serialize(),
                    cancellationToken);
                await store.ReportProgressAsync(
                    operationId,
                    0,
                    $"Accepted by {entry.Name}; waiting for download progress.",
                    cancellationToken: cancellationToken);
                return new DownloadSubmissionOutcome(
                    true,
                    operationId,
                    result.ExternalId,
                    entry.Id,
                    $"Sent to {entry.Name}. Track it under Admin → Operations → Downloads.");
            }

            if (result.Success)
            {
                // Accepted but the client returned no job ID to track; treat it as done.
                const string untracked = "Sent, but the download client returned no job ID, so live progress is unavailable.";
                await store.MarkSucceededAsync(operationId, untracked, CancellationToken.None);
                return new DownloadSubmissionOutcome(true, operationId, null, entry.Id, untracked);
            }

            lastError = result.Error ?? $"{entry.Name} rejected the request.";
            await store.AppendLogAsync(
                operationId,
                OperationLogLevel.Warning,
                "Acquisition",
                $"{entry.Name} did not accept the release: {lastError}",
                CancellationToken.None);
            logger.LogWarning("Download client '{Client}' rejected a submission: {Error}", entry.Name, lastError);
        }

        var message = lastError ?? "All configured download clients rejected the request.";
        await store.MarkFailedAsync(operationId, message, CancellationToken.None);
        return new DownloadSubmissionOutcome(false, operationId, null, null, message);
    }
}
