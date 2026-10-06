using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>The consumer milestones of a request's timeline; none of them names an internal step (docs/mockups/request-status-details, section 8).</summary>
public enum RequestTimelineMilestone
{
    Requested,
    Approved,
    LookingForMedia,
    GettingMedia,
    Preparing,
    Available,
    MonitoringFutureReleases,
    Rejected,
    Cancelled,
    NeedsAttention
}

public sealed record RequestTimelineEntry(RequestTimelineMilestone Milestone, DateTime AtUtc);

/// <summary>One request of the history: the request, its consumer state and the poster of its title.</summary>
public sealed record RequestRowView(AcquisitionRequest Request, ConsumerAcquisitionView State, string? PosterUrl);

/// <param name="NextReleaseUtc">The next known release of a series the request keeps watching; null for everything else.</param>
/// <param name="OpenUrl">The page of the requested title on this server once something of it is available; null before.</param>
/// <param name="StatusUnavailable">The state of the acquisition could not be read, so <paramref name="State"/> is only what the stored request says.</param>
public sealed record RequestStatusView(AcquisitionRequest Request, ConsumerAcquisitionView State, string? PosterUrl, IReadOnlyList<RequestTimelineEntry> Timeline, DateTime? NextReleaseUtc, string? OpenUrl, bool StatusUnavailable);

/// <summary>
/// Reads what a profile sees of its own requests: the consumer state (the one projection of <see cref="ConsumerAcquisitionQuery"/>), the
/// poster of the title and, for the status surface, the milestones and the next known release. Pure reads that never advance a request and
/// never read another profile's request: the status surface of a request that is not the profile's own does not exist.
/// </summary>
public sealed class RequestStatusQuery(
    AcquisitionAccessStore requests,
    ConsumerAcquisitionQuery projection,
    RequestArtworkResolver artwork,
    IInstanceModuleService modules,
    AppDbContext db,
    TimeProvider clock,
    ILogger<RequestStatusQuery> logger)
{
    /// <summary>The state and poster of every request of one history page, in a fixed number of queries.</summary>
    public async Task<IReadOnlyList<RequestRowView>> RowsAsync(IReadOnlyList<AcquisitionRequest> history, string profileId, CancellationToken cancellationToken)
    {
        var states = await projection.ProjectManyAsync(history, await modules.IsEnabledAsync(InstanceModule.Playback, cancellationToken), cancellationToken);
        var posters = await artwork.ResolvePostersAsync(history, profileId, cancellationToken);
        return [.. history.Select(request => new RequestRowView(request, states[request.Id], posters.GetValueOrDefault(request.Id)))];
    }

    /// <summary>The profile's own request, or null when it does not exist or belongs to another profile; the cheap gate of every action on it.</summary>
    public async Task<AcquisitionRequest?> FindOwnAsync(Guid id, string profileId, CancellationToken cancellationToken) =>
        await requests.GetAsync(id, cancellationToken) is { } request && request.RequestedByProfileId == profileId ? request : null;

    /// <summary>The status of the profile's own request, or null when it does not exist or belongs to another profile.</summary>
    public async Task<RequestStatusView?> GetOwnAsync(Guid id, string profileId, CancellationToken cancellationToken)
    {
        if (await FindOwnAsync(id, profileId, cancellationToken) is not { } request)
        {
            return null;
        }

        // The saved request is the surface's own data; when only the acquisition behind it cannot be read, the surface still shows the request
        // and says so instead of failing, with the state the stored request alone allows.
        RequestRowView row;
        var unavailable = false;
        try
        {
            row = (await RowsAsync([request], profileId, cancellationToken))[0];
        }
        catch (Exception exception) when (exception is DbException or FormatException)
        {
            logger.LogWarning(exception, "The state of request {RequestId} could not be read.", request.Id);
            row = new RequestRowView(request, ConsumerAcquisitionProjector.Project(request, VideoRequestPayload.Parse(request.PayloadJson), null, false, null, false, clock.GetUtcNow().UtcDateTime), null);
            unavailable = true;
        }

        var nextRelease = unavailable ? null : await NextReleaseAsync(request, row.State, cancellationToken);
        return new RequestStatusView(request, row.State, row.PosterUrl, Timeline(request, row.State.State), nextRelease, unavailable ? null : OpenUrl(request, row.State), unavailable);
    }

    /// <summary>
    /// The milestones the stored request still knows: when it was made, when it was decided, and the stage it is in since its last change.
    /// A request keeps no history of the stages in between, so none is invented.
    /// </summary>
    public static IReadOnlyList<RequestTimelineEntry> Timeline(AcquisitionRequest request, ConsumerAcquisitionState state)
    {
        List<RequestTimelineEntry> entries = [new(RequestTimelineMilestone.Requested, request.CreatedAt)];
        if (request.IsCancelled)
        {
            entries.Add(new(RequestTimelineMilestone.Cancelled, request.UpdatedAt));
            return entries;
        }

        if (request.Status == AcquisitionRequestStatus.Rejected)
        {
            entries.Add(new(RequestTimelineMilestone.Rejected, request.DecidedAt ?? request.UpdatedAt));
            return entries;
        }

        if (request.Status == AcquisitionRequestStatus.Pending)
        {
            return entries;
        }

        if (request.DecidedAt is { } decided)
        {
            entries.Add(new(RequestTimelineMilestone.Approved, decided));
        }

        RequestTimelineMilestone? stage = state switch
        {
            ConsumerAcquisitionState.LookingForMedia or ConsumerAcquisitionState.NotAvailableYet => RequestTimelineMilestone.LookingForMedia,
            ConsumerAcquisitionState.GettingMedia => RequestTimelineMilestone.GettingMedia,
            ConsumerAcquisitionState.Preparing => RequestTimelineMilestone.Preparing,
            ConsumerAcquisitionState.ReadyToWatch or ConsumerAcquisitionState.Available => RequestTimelineMilestone.Available,
            ConsumerAcquisitionState.MonitoringFutureReleases => RequestTimelineMilestone.MonitoringFutureReleases,
            ConsumerAcquisitionState.NeedsAttention => RequestTimelineMilestone.NeedsAttention,
            _ => null
        };
        if (stage is { } milestone && request.UpdatedAt > (request.DecidedAt ?? request.CreatedAt))
        {
            entries.Add(new(milestone, request.UpdatedAt));
        }

        return entries;
    }

    /// <summary>
    /// Where a request leads once its title is available: the canonical page of the Movie or Series Work, and for every other media type the result
    /// address its executor recorded when that is a path of this server.
    /// </summary>
    private static string? OpenUrl(AcquisitionRequest request, ConsumerAcquisitionView state)
    {
        if (state.State is not (ConsumerAcquisitionState.ReadyToWatch or ConsumerAcquisitionState.Available))
        {
            return null;
        }

        return request.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv
            ? VideoRequestPayload.Parse(request.PayloadJson) is { WorkId: var workId } && workId != Guid.Empty ? VideoWorkLinks.DetailPath(request.Kind, workId) : null
            : request.LocalResultPath;
    }

    /// <summary>The first episode of the request's series that has not aired yet, while the request watches for future releases.</summary>
    private async Task<DateTime?> NextReleaseAsync(AcquisitionRequest request, ConsumerAcquisitionView state, CancellationToken cancellationToken)
    {
        if (request.Kind != MediaAcquisitionKind.Tv
            || !(state.IsMonitoring || state.State == ConsumerAcquisitionState.MonitoringFutureReleases)
            || VideoRequestPayload.Parse(request.PayloadJson) is not { } payload)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        return await db.WorkEpisodes.AsNoTracking().Where(episode => episode.WorkId == payload.WorkId && episode.AiredAt > now).MinAsync(episode => episode.AiredAt, cancellationToken);
    }
}
