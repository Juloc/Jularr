using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Acquisition.DownloadClients;

/// <summary>
/// Adapts the existing, unchanged SABnzbd client (<see cref="ISabnzbdClient"/>)
/// to the canonical <see cref="IDownloadClient"/> abstraction, so SABnzbd is
/// one download client among several instead of a hardcoded special case.
/// </summary>
public sealed class SabnzbdDownloadClient(ISabnzbdClient client) : IDownloadClient
{
    public string ProviderId => SabnzbdClient.ProviderId;

    public async Task<DownloadClientTestResult> TestAsync(
        DownloadClientEntry entry,
        CancellationToken cancellationToken)
    {
        var result = await client.TestAsync(ToConnection(entry), cancellationToken);
        return new DownloadClientTestResult(result.Success, result.Version, result.Error);
    }

    public async Task<DownloadClientSubmitResult> SubmitAsync(
        DownloadClientEntry entry,
        DownloadClientSubmitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SabnzbdGrabResult result;
        if (request.File is not null)
        {
            result = await client.AddFileAsync(
                ToConnection(entry),
                request.File,
                request.FileName ?? "release.nzb",
                entry.CategoryFor(request.MediaKind),
                cancellationToken);
        }
        else if (request.Url is not null)
        {
            result = await client.GrabAsync(
                ToConnection(entry),
                new SabnzbdGrabRequest(request.Url, request.Name, entry.CategoryFor(request.MediaKind), SabnzbdPriority(request.Priority)),
                cancellationToken);
        }
        else
        {
            return new DownloadClientSubmitResult(false, null, "SABnzbd needs either an NZB URL or an NZB file.");
        }

        var nzoId = result.NzoIds.FirstOrDefault();
        return new DownloadClientSubmitResult(
            result.Success,
            string.IsNullOrWhiteSpace(nzoId) ? null : nzoId,
            result.Success ? null : (result.Error ?? "SABnzbd rejected the request."));
    }

    public async Task<IReadOnlyList<DownloadClientJobStatus>> GetStatusAsync(
        DownloadClientEntry entry,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        var connection = ToConnection(entry);
        var queue = await client.GetQueueAsync(connection, cancellationToken);
        var history = await client.GetHistoryAsync(connection, externalIds, cancellationToken);

        var statuses = new List<DownloadClientJobStatus>();
        foreach (var job in queue.Jobs)
        {
            statuses.Add(
                new DownloadClientJobStatus(
                    job.NzoId,
                    job.Name,
                    queue.Paused ? DownloadClientJobState.Queued : DownloadClientJobState.Downloading,
                    job.Percentage,
                    job.TimeLeft,
                    job.SizeBytes,
                    job.SizeLeftBytes,
                    queue.Jobs.Count == 1 ? queue.BytesPerSecond : null,
                    null,
                    null));
        }

        foreach (var job in history.Jobs)
        {
            if (job.IsCompleted)
            {
                statuses.Add(
                    new DownloadClientJobStatus(
                        job.NzoId, job.Name, DownloadClientJobState.Completed,
                        100, null, job.SizeBytes, 0, null, job.StoragePath, null));
            }
            else if (job.IsFailed)
            {
                statuses.Add(
                    new DownloadClientJobStatus(
                        job.NzoId, job.Name, DownloadClientJobState.Failed,
                        null, null, job.SizeBytes, null, null, job.StoragePath,
                        SabnzbdFailureDescriptions.Describe(job.FailureKind, job.FailureMessage)));
            }
            else
            {
                statuses.Add(
                    new DownloadClientJobStatus(
                        job.NzoId, job.Name, DownloadClientJobState.PostProcessing,
                        99, null, job.SizeBytes, job.SizeBytes, null, job.StoragePath, null));
            }
        }

        return statuses;
    }

    public async Task<bool> DeleteAsync(
        DownloadClientEntry entry,
        string externalId,
        bool deleteFiles,
        CancellationToken cancellationToken)
    {
        var connection = ToConnection(entry);
        var queue = await client.CancelAsync(connection, externalId, deleteFiles, cancellationToken);
        var history = await client.DeleteHistoryAsync(connection, externalId, deleteFiles, cancellationToken);
        return queue.Success || history.Success;
    }

    /// <summary>SABnzbd's queue priorities: 1 is High and -1 Low; null leaves the queue's default for a normal download.</summary>
    private static int? SabnzbdPriority(OperationPriority priority) => priority switch
    {
        OperationPriority.High => 1,
        OperationPriority.Low => -1,
        _ => null
    };

    public static SabnzbdConnection ToConnection(DownloadClientEntry entry) =>
        new(
            new SabnzbdSettings(entry.Settings.BaseUrl),
            entry.Secret ?? throw new InvalidOperationException("SABnzbd API key is not configured."));
}
