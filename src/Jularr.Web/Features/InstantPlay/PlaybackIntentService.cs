using System.Text.Json.Serialization;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Playback.Decision;

namespace Jularr.Web.Features.InstantPlay;

[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackIntentOutcome>))]
public enum PlaybackIntentOutcome
{
    /// <summary>The target is local and this instance plays it: open the player for <see cref="PrimaryAction.WorkEpisodeId"/>.</summary>
    PlayNow,

    /// <summary>An approved request is acquiring the target, created by this intent or already active.</summary>
    Acquiring,

    /// <summary>An equivalent request waits for approval; its state is shown and approval is not bypassed.</summary>
    AwaitingApproval,

    /// <summary>The profile may not acquire this instantly: the explicit Request action is the way.</summary>
    RequestRequired,

    /// <summary>Nothing valid to start: playback is off, the title cannot be requested or no episode needs watching.</summary>
    NotAvailable,

    /// <summary>The profile already waits for several titles; finish or wait for one before starting another.</summary>
    LimitReached,

    /// <summary>No such Movie or Series, or the episode is not one of it.</summary>
    TargetNotFound
}

/// <param name="Action">What the resolver decided for the target; null only for <see cref="PlaybackIntentOutcome.TargetNotFound"/>.</param>
/// <param name="RequestId">The canonical request the intent attached to or created.</param>
public sealed record PlaybackIntentResult(PlaybackIntentOutcome Outcome, PrimaryAction? Action, Guid? RequestId, ConsumerAcquisitionView? Acquisition);

/// <summary>
/// The explicit playback intent of Instant Play (docs/mockups/instant-play, sections 4, 12 and 13): Play, Continue, Start watching or
/// Watch now. A local target answers with the player target. A missing one is acquired only when the capability chain permits
/// (<see cref="InstantPlayPolicy.AllowsInstantAcquisition"/>), through the one canonical request: an equivalent open request is reused
/// and never duplicated or approved around, otherwise the request is created through <see cref="AcquisitionRequestService"/> for the
/// smallest required unit (a Movie, or exactly one episode, never the whole Series or future monitoring) and the unit is prioritized
/// in the executor and the download client. It is idempotent: a repeat or a concurrent second intent finds the request and changes nothing.
/// "Stop waiting" is a client-side wait and has no server counterpart here: it must never cancel shared acquisition.
/// </summary>
public sealed class PlaybackIntentService(
    VideoPlaybackFactsQuery facts,
    InstantPlayPolicyService policies,
    AcquisitionRequestService requests,
    AcquisitionAccessStore requestStore,
    ConsumerAcquisitionQuery projection,
    CurrentAccountContext account,
    TimeProvider clock)
{
    /// <summary>
    /// Units a profile may wait for at once. A marker orders the executor, Wanted and the download client ahead of other work, so one
    /// profile cannot hold them all.
    /// </summary>
    public const int MaxOutstandingPlaybackMarkers = 3;

    /// <summary>A request whose next search is further away than this is not searching now, so its markers do not occupy a slot.</summary>
    public static readonly TimeSpan ActiveSearchHorizon = TimeSpan.FromMinutes(15);

    /// <summary>The back-off of a request is reset, and a search run inside the intent, at most this often by playback intents.</summary>
    public static readonly TimeSpan ResetInterval = TimeSpan.FromMinutes(10);

    private const int MarkedRequestScanLimit = 100;

    /// <param name="workEpisodeId">A Series episode to watch; null means the next required episode of a Series, or the Movie itself.</param>
    public async Task<PlaybackIntentResult> StartAsync(Guid workId, Guid? workEpisodeId, CancellationToken cancellationToken)
    {
        var state = await facts.GetAsync(account.ProfileId, workId, cancellationToken);
        if (state is null || state.Facts is MoviePlaybackFacts && workEpisodeId is not null)
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.TargetNotFound, null, null, null);
        }

        var policy = await policies.ResolveAsync(state.MediaType, cancellationToken);
        var action = PrimaryActionResolver.Resolve(state.Facts, policy, workEpisodeId);
        if (action.Reason == PrimaryActionReason.UnknownTarget)
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.TargetNotFound, null, null, null);
        }

        if (action.TargetIsLocal && action.Kind != PrimaryActionKind.Available)
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.PlayNow, action, null, null);
        }

        switch (action.Kind)
        {
            case PrimaryActionKind.ShowRequestState:
                // Someone who may not request this media type and did not make the request is told nothing about it.
                return !ConsumerAcquisitionQuery.MayRead(state.OpenRequest!, account.ProfileId, policy.CanRequest, account.Can(JularrPolicies.AdminMedia))
                    ? new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null)
                    : await ReportAsync(state.OpenRequest!, action, policy, cancellationToken);
            case PrimaryActionKind.StartWatching or PrimaryActionKind.WatchNow:
                return await AtOutstandingLimitAsync(cancellationToken)
                    ? new PlaybackIntentResult(PlaybackIntentOutcome.LimitReached, action, null, null)
                    : await AcquireAsync(state, action, policy, cancellationToken);
            case PrimaryActionKind.Request:
                return new PlaybackIntentResult(PlaybackIntentOutcome.RequestRequired, action, null, null);
            default:
                return new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null);
        }
    }

    /// <summary>
    /// Whether the profile already waits for as many units as it may. Only its own live markers count (younger than the marker lifetime) on
    /// requests that are actually searching or downloading: a request in a long back-off because no release exists is not occupying
    /// anything, so it can never lock the profile out for good.
    /// </summary>
    private async Task<bool> AtOutstandingLimitAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var withMarkers = await requestStore.ListOpenWithPlaybackMarkersAsync(MarkedRequestScanLimit, cancellationToken);
        var waiting = withMarkers
            .Select(request => (Request: request, Payload: VideoRequestPayload.Parse(request.PayloadJson)))
            .Where(x => x.Payload is not null && !(x.Request.Status == AcquisitionRequestStatus.Approved && x.Payload.NextSearchUtc > now + ActiveSearchHorizon))
            .Sum(x => x.Payload!.ActivePlaybackMarkers(now).Count(marker => marker.ProfileId == account.ProfileId));
        return waiting >= MaxOutstandingPlaybackMarkers;
    }

    private async Task<PlaybackIntentResult> ReportAsync(AcquisitionRequest request, PrimaryAction action, InstantPlayPolicy policy, CancellationToken cancellationToken)
    {
        var view = await projection.ProjectAsync(request, action.WorkEpisodeId, policy.PlaybackEnabled, cancellationToken);
        var outcome = request.Status switch
        {
            AcquisitionRequestStatus.Pending => PlaybackIntentOutcome.AwaitingApproval,
            _ when request.IsOpen => PlaybackIntentOutcome.Acquiring,
            _ => PlaybackIntentOutcome.NotAvailable
        };
        return new PlaybackIntentResult(outcome, action, request.Id, view);
    }

    private async Task<PlaybackIntentResult> AcquireAsync(VideoPlaybackState state, PrimaryAction action, InstantPlayPolicy policy, CancellationToken cancellationToken)
    {
        var request = state.OpenRequest;
        if (request is null)
        {
            var kind = VideoWorkLinks.AcquisitionKind(state.MediaType);
            var payload = SmallestPayload(state, action);
            AcquisitionSubmission submission;
            try
            {
                submission = await requests.SubmitWithOutcomeAsync(new AcquisitionRequestDraft(kind, TmdbDiscoveryProvider.ProviderKey, state.ProviderId!, state.Title, null, null, payload.Serialize()), cancellationToken);
            }
            catch (AcquisitionAccessDeniedException)
            {
                return new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null);
            }

            request = submission.Request;
            if (!submission.AlreadyRequested)
            {
                return await ReportAsync(request, action, policy, cancellationToken);
            }
        }

        return await AttachAsync(state, request, action, policy, cancellationToken);
    }

    /// <summary>
    /// A request for the title exists, found or won by a concurrent intent: it takes the playback unit instead of a second request. A
    /// request that waits for approval is shown as it is, and one whose monitoring an administrator turned off cannot be searched, so
    /// nothing is promised for it. A unit that is newly covered is an edit of the request like an Admin scope change: the new revision
    /// keeps a search that already decided "everything requested is available" from completing the request over it. The back-off is only
    /// reset, and a search only run inside this request, once per <see cref="ResetInterval"/>; otherwise the Wanted pass takes the unit
    /// first by its priority. A full marker list refuses instead of evicting someone else's wait.
    /// </summary>
    private async Task<PlaybackIntentResult> AttachAsync(VideoPlaybackState state, AcquisitionRequest request, PrimaryAction action, InstantPlayPolicy policy, CancellationToken cancellationToken)
    {
        if (request.Status == AcquisitionRequestStatus.Pending)
        {
            return await ReportAsync(request, action, policy, cancellationToken);
        }

        if (VideoRequestPayload.Parse(request.PayloadJson) is { Monitored: false })
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var newlyCovered = state.Facts.OpenRequest is not { } facts || !facts.Covers(action.WorkEpisodeId);
        var fallback = VideoRequestPayload.Default(request.Kind, action.WorkId, state.Title, state.Year);
        var attached = Attachment.Unchanged;
        if (!await requestStore.PatchPayloadAsync(request.Id, stored => WithAttachedUnit(VideoRequestPayload.Parse(stored) ?? fallback, action.WorkEpisodeId, newlyCovered, now, out attached).Serialize(), cancellationToken))
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null);
        }

        if (attached == Attachment.Full)
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.LimitReached, action, null, null);
        }

        // Only an approved request whose back-off was just reset has anything to search for right now; everything else is picked up by the
        // Wanted pass (priority first) or as soon as the running download is done.
        if (attached == Attachment.Reset && request.Status == AcquisitionRequestStatus.Approved)
        {
            await requests.ContinueAsync(request.Id, cancellationToken);
        }

        return await ReportAsync(await requestStore.GetAsync(request.Id, cancellationToken) ?? request, action, policy, cancellationToken);
    }

    private enum Attachment
    {
        /// <summary>The unit already had a live marker.</summary>
        Unchanged,

        /// <summary>The marker was added; the request keeps its back-off.</summary>
        Marked,

        /// <summary>The marker was added and the back-off reset.</summary>
        Reset,

        /// <summary>All marker slots are in use.</summary>
        Full
    }

    /// <summary>The scope of a request created by an intent: a Movie as a whole, or exactly the target episode and nothing of the rest of the Series.</summary>
    private VideoRequestPayload SmallestPayload(VideoPlaybackState state, PrimaryAction action)
    {
        var marker = new PlaybackMarker(action.WorkEpisodeId, account.ProfileId, clock.GetUtcNow().UtcDateTime);
        var scope = action.WorkEpisodeId is { } episodeId
            ? new VideoRequestPayload(action.WorkId, state.Title, state.Year, VideoRequestScope.Custom, [episodeId], MonitorFuture: false, SelectedSeasonIds: [])
            : VideoRequestPayload.Default(MediaAcquisitionKind.Movie, action.WorkId, state.Title, state.Year);
        return scope with { PlaybackMarkers = [marker] };
    }

    private VideoRequestPayload WithAttachedUnit(VideoRequestPayload payload, Guid? workEpisodeId, bool newlyCovered, DateTime now, out Attachment result)
    {
        var live = payload.ActivePlaybackMarkers(now).ToList();
        if (live.Any(marker => marker.WorkEpisodeId == workEpisodeId))
        {
            result = Attachment.Unchanged;
            return payload;
        }

        if (live.Count >= VideoRequestPayload.MaxPlaybackMarkers)
        {
            result = Attachment.Full;
            return payload;
        }

        var marked = payload with { PlaybackMarkers = [.. live, new PlaybackMarker(workEpisodeId, account.ProfileId, now)], ScopeRevision = payload.ScopeRevision + 1 };
        var reset = newlyCovered && (payload.PlaybackResetUtc is not { } last || now - last >= ResetInterval);
        result = reset ? Attachment.Reset : Attachment.Marked;
        return reset ? marked with { Searches = 0, NextSearchUtc = null, PlaybackResetUtc = now } : marked;
    }
}
