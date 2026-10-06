using System.Text.Json.Serialization;
using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Playback.Decision;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// What a consumer is told about acquisition (docs/mockups/instant-play sections 5, 7, 10 and 21, request-status-details). These are
/// projections of the canonical request and its download, never new backend states, and none of them names a provider, release,
/// download client or file. <c>Downloading</c> and <c>Importing</c> stay Admin words.
/// </summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<ConsumerAcquisitionState>))]
public enum ConsumerAcquisitionState
{
    WaitingForApproval,

    /// <summary>Wanted, search, identity matching and release choice, including trying another acceptable release.</summary>
    LookingForMedia,

    /// <summary>The transfer of the media; <see cref="ConsumerAcquisitionView.ProgressPercent"/> is set only when it is trustworthy.</summary>
    GettingMedia,

    /// <summary>Verification, extraction, import and library registration.</summary>
    Preparing,

    /// <summary>The target is local and this instance plays it.</summary>
    ReadyToWatch,

    /// <summary>The target is local on an instance that does not play (manager-only): imported and managed here.</summary>
    Available,

    /// <summary>Nothing is due now; the request keeps watching for future releases.</summary>
    MonitoringFutureReleases,

    /// <summary>No acceptable release exists yet and the request keeps looking.</summary>
    NotAvailableYet,

    /// <summary>The request ended and nothing of the target is playable or on its way; nothing keeps looking.</summary>
    NotAvailable,

    NeedsAttention,

    Rejected
}

/// <summary>The consumer word for what is being acquired: "Getting episode", "Getting movie" or "Getting media".</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<ConsumerMediaUnit>))]
public enum ConsumerMediaUnit
{
    Episode,
    Movie,
    Media
}

/// <param name="ProgressPercent">Whole percent of the transfer, only while the download reports a total and a transferred size; never estimated.</param>
/// <param name="IsMonitoring">The request also keeps watching for future releases; it coexists with <see cref="ConsumerAcquisitionState.ReadyToWatch"/> and <see cref="ConsumerAcquisitionState.Available"/>.</param>
public sealed record ConsumerAcquisitionView(ConsumerAcquisitionState State, ConsumerMediaUnit MediaUnit, int? ProgressPercent, bool IsMonitoring);

/// <summary>The one mapping from the canonical request and its download operation to the consumer vocabulary.</summary>
public static class ConsumerAcquisitionProjector
{
    /// <param name="targetIsLocal">The unit the consumer waits for can be played (or, on a manager-only instance, is imported) now.</param>
    /// <param name="download">The operation of the request's current download, or null when there is none.</param>
    /// <param name="targetEpisodeId">The episode of a Series the consumer waits for; null for a Movie or the request as a whole.</param>
    public static ConsumerAcquisitionView Project(AcquisitionRequest request, VideoRequestPayload? payload, OperationSnapshot? download, bool targetIsLocal, Guid? targetEpisodeId, bool playbackEnabled, DateTime nowUtc)
    {
        var unit = request.Kind == MediaAcquisitionKind.Movie ? ConsumerMediaUnit.Movie : targetEpisodeId is not null ? ConsumerMediaUnit.Episode : ConsumerMediaUnit.Media;
        var monitoring = request.IsOpen && payload is { Monitored: true, MonitorFuture: true };
        if (targetIsLocal)
        {
            return new ConsumerAcquisitionView(playbackEnabled ? ConsumerAcquisitionState.ReadyToWatch : ConsumerAcquisitionState.Available, unit, null, monitoring);
        }

        var state = request.Status switch
        {
            AcquisitionRequestStatus.Pending => ConsumerAcquisitionState.WaitingForApproval,
            AcquisitionRequestStatus.Approved => ApprovedState(payload, nowUtc),
            AcquisitionRequestStatus.Searching => ConsumerAcquisitionState.LookingForMedia,
            AcquisitionRequestStatus.Downloading => DownloadingState(download),
            AcquisitionRequestStatus.Importing => ConsumerAcquisitionState.Preparing,
            AcquisitionRequestStatus.Completed => ConsumerAcquisitionState.NotAvailable,
            AcquisitionRequestStatus.Rejected => ConsumerAcquisitionState.Rejected,
            _ => ConsumerAcquisitionState.NeedsAttention
        };

        // The request works on one episode at a time: what it transfers or prepares is not the episode a consumer waits for, whose
        // own turn is still ahead, so neither its state nor its percentage may be shown for it.
        if (targetEpisodeId is { } target && payload?.ActiveWorkEpisodeId is { } active && active != target
            && state is ConsumerAcquisitionState.GettingMedia or ConsumerAcquisitionState.Preparing)
        {
            state = ConsumerAcquisitionState.LookingForMedia;
        }

        return new ConsumerAcquisitionView(state, unit, state == ConsumerAcquisitionState.GettingMedia ? ReliableProgress(download) : null, monitoring);
    }

    /// <summary>
    /// An approved request is looking unless its payload says it is waiting: no acceptable release was found and the next search is
    /// scheduled (not available yet), or nothing was due and the next one is scheduled for a future release.
    /// </summary>
    private static ConsumerAcquisitionState ApprovedState(VideoRequestPayload? payload, DateTime nowUtc)
    {
        if (payload?.NextSearchUtc is not { } next || next <= nowUtc)
        {
            return ConsumerAcquisitionState.LookingForMedia;
        }

        return payload.Searches > 0 ? ConsumerAcquisitionState.NotAvailableYet : ConsumerAcquisitionState.MonitoringFutureReleases;
    }

    /// <summary>A transfer that already succeeded is being prepared; one that failed is being replaced by another release or reviewed, which Wanted decides next.</summary>
    private static ConsumerAcquisitionState DownloadingState(OperationSnapshot? download) => download?.Status switch
    {
        OperationStatus.Succeeded => ConsumerAcquisitionState.Preparing,
        OperationStatus.Failed or OperationStatus.Interrupted or OperationStatus.Cancelled => ConsumerAcquisitionState.LookingForMedia,
        _ => ConsumerAcquisitionState.GettingMedia
    };

    /// <summary>Only a transfer that reports both a total and a transferred size not above it has a percentage; a bare percentage, a queued job or a guess has none.</summary>
    public static int? ReliableProgress(OperationSnapshot? download) =>
        download is { Status: OperationStatus.Running, BytesTotal: > 0, BytesCompleted: >= 0 } && download.BytesCompleted <= download.BytesTotal
            ? (int)Math.Min(99, download.BytesCompleted!.Value * 100 / download.BytesTotal!.Value)
            : null;
}

/// <summary>A request of a Movie or Series and the Work it names, for a consumer read.</summary>
public sealed record ConsumerRequestRead(AcquisitionRequest Request, Guid WorkId);

/// <summary>
/// Reads the consumer projection of a request. A pure read: it never advances a request, and a failing operation lookup is not hidden
/// as a made-up state. Who may read a request is decided by the caller (the authorization boundary).
/// </summary>
public sealed class ConsumerAcquisitionQuery(AppDbContext db, AcquisitionAccessStore requests, VideoRequestWorkResolver works, TimeProvider clock)
{
    /// <summary>
    /// The one rule of who may read the consumer state of a request (docs/mockups/instant-play, section 13): its requester, whoever manages
    /// media, and every profile that may request this media type, because one open request per title is shared state. Everyone else is
    /// told nothing about it. The playback intent, the status endpoint and the detail pages all ask this.
    /// </summary>
    public static bool MayRead(AcquisitionRequest request, string profileId, bool canRequest, bool isAdminMedia) =>
        request.RequestedByProfileId == profileId || isAdminMedia || canRequest;

    /// <summary>
    /// The Movie or Series request with its canonical Work, or null when it does not exist, has no Work yet, or <paramref name="workEpisodeId"/>
    /// is not an episode of that Work (a Movie has none).
    /// </summary>
    public async Task<ConsumerRequestRead?> FindAsync(Guid requestId, Guid? workEpisodeId, CancellationToken cancellationToken)
    {
        var request = await requests.GetAsync(requestId, cancellationToken);
        if (request is null || request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
        {
            return null;
        }

        var work = (await works.ResolveAsync([request], cancellationToken)).GetValueOrDefault(request.Id);
        if (work is null || workEpisodeId is { } episodeId && (request.Kind == MediaAcquisitionKind.Movie || !await db.WorkEpisodes.AsNoTracking().AnyAsync(x => x.Id == episodeId && x.WorkId == work.WorkId, cancellationToken)))
        {
            return null;
        }

        return new ConsumerRequestRead(request, work.WorkId);
    }

    /// <param name="workEpisodeId">The episode of a Series the consumer waits for; null for a Movie or when the request as a whole is meant.</param>
    public async Task<ConsumerAcquisitionView> ProjectAsync(AcquisitionRequest request, Guid? workEpisodeId, bool playbackEnabled, CancellationToken cancellationToken)
    {
        var operation = request.OperationId is { } operationId ? await new OperationStore(db).GetAsync(operationId, cancellationToken) : null;
        var work = (await works.ResolveAsync([request], cancellationToken)).GetValueOrDefault(request.Id);
        var local = request.Kind == MediaAcquisitionKind.Movie
            ? work is not null && await HasFileAsync(work.WorkId, null, cancellationToken)
            : workEpisodeId is { } episodeId
                ? work is not null && await HasFileAsync(work.WorkId, episodeId, cancellationToken)
                : request.Status == AcquisitionRequestStatus.Completed && work is not null && await HasAnyEpisodeFileAsync(work.WorkId, cancellationToken);
        return ConsumerAcquisitionProjector.Project(request, VideoRequestPayload.Parse(request.PayloadJson), operation, local, workEpisodeId, playbackEnabled, clock.GetUtcNow().UtcDateTime);
    }

    private Task<bool> HasFileAsync(Guid workId, Guid? workEpisodeId, CancellationToken cancellationToken) =>
        db.MediaAssets.AsNoTracking().AnyAsync(
            asset => asset.WorkId == workId && asset.WorkEpisodeId == workEpisodeId && asset.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id),
            cancellationToken);

    /// <summary>A completed request is only available when something of the Series is actually playable; one that ended by monitoring being turned off is not.</summary>
    private Task<bool> HasAnyEpisodeFileAsync(Guid workId, CancellationToken cancellationToken) =>
        db.MediaAssets.AsNoTracking().AnyAsync(
            asset => asset.WorkId == workId && asset.WorkEpisodeId != null && asset.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id),
            cancellationToken);
}

/// <summary>The catalog keys of the consumer acquisition vocabulary, so every consumer surface names a state the same way.</summary>
public static class ConsumerAcquisitionLabels
{
    /// <summary>A request's stored status in consumer words, for lists and badges that only know the status.</summary>
    public static string StatusKey(AcquisitionRequestStatus status) => StatusKey(AcquisitionAccessNames.Status(status));

    /// <param name="statusName">The stored status name, see <see cref="AcquisitionAccessNames.Status"/>.</param>
    public static string StatusKey(string statusName) => $"requests.consumer.{statusName}";

    /// <summary>A projected state in consumer words; the transfer names what is being got.</summary>
    public static string StateKey(ConsumerAcquisitionView view) => view.State switch
    {
        ConsumerAcquisitionState.WaitingForApproval => "acquisition.state.waitingForApproval",
        ConsumerAcquisitionState.LookingForMedia => "acquisition.state.lookingForMedia",
        ConsumerAcquisitionState.GettingMedia => view.MediaUnit switch
        {
            ConsumerMediaUnit.Episode => "acquisition.state.gettingEpisode",
            ConsumerMediaUnit.Movie => "acquisition.state.gettingMovie",
            _ => "acquisition.state.gettingMedia"
        },
        ConsumerAcquisitionState.Preparing => "acquisition.state.preparing",
        ConsumerAcquisitionState.ReadyToWatch => "acquisition.state.readyToWatch",
        ConsumerAcquisitionState.Available => "acquisition.state.available",
        ConsumerAcquisitionState.MonitoringFutureReleases => "acquisition.state.monitoringFutureReleases",
        ConsumerAcquisitionState.NotAvailableYet => "acquisition.state.notAvailableYet",
        ConsumerAcquisitionState.NotAvailable => "acquisition.state.notAvailable",
        ConsumerAcquisitionState.NeedsAttention => "acquisition.state.needsAttention",
        _ => "acquisition.state.rejected"
    };
}
