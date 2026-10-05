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
    CurrentAccountContext account)
{
    /// <param name="workEpisodeId">A Series episode to watch; null means the next required episode of a Series, or the Movie itself.</param>
    public async Task<PlaybackIntentResult> StartAsync(Guid workId, Guid? workEpisodeId, CancellationToken cancellationToken)
    {
        var state = await facts.GetAsync(account.ProfileId, workId, cancellationToken);
        if (state is null || !BelongsToWork(state.Facts, workEpisodeId))
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.TargetNotFound, null, null, null);
        }

        var policy = await policies.ResolveAsync(state.MediaType, cancellationToken);
        var action = PrimaryActionResolver.Resolve(state.Facts, policy, workEpisodeId);
        if (action.TargetIsLocal && action.Kind != PrimaryActionKind.Available)
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.PlayNow, action, null, null);
        }

        switch (action.Kind)
        {
            case PrimaryActionKind.ShowRequestState:
                return await ShowOrPrioritizeAsync(state, action, policy, cancellationToken);
            case PrimaryActionKind.StartWatching or PrimaryActionKind.WatchNow:
                return await AcquireAsync(state, action, policy, cancellationToken);
            case PrimaryActionKind.Request:
                return new PlaybackIntentResult(PlaybackIntentOutcome.RequestRequired, action, null, null);
            default:
                return new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null);
        }
    }

    private static bool BelongsToWork(PlaybackFacts facts, Guid? workEpisodeId) => facts switch
    {
        MoviePlaybackFacts => workEpisodeId is null,
        SeriesPlaybackFacts series => workEpisodeId is null || series.Units.Any(x => x.Id == workEpisodeId),
        _ => false
    };

    /// <summary>
    /// An equivalent request covers the target. Its state is shown, but a profile waiting for a unit that is missing and not yet
    /// prioritized still gets it prioritized: the request itself is untouched. Someone who may not request this media type and did not
    /// make the request is told nothing about it.
    /// </summary>
    private async Task<PlaybackIntentResult> ShowOrPrioritizeAsync(VideoPlaybackState state, PrimaryAction action, InstantPlayPolicy policy, CancellationToken cancellationToken)
    {
        var open = state.OpenRequest!;
        if (!policy.AllowsRequest && open.RequestedByProfileId != account.ProfileId && !account.Can(JularrPolicies.AdminMedia))
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null);
        }

        var payload = VideoRequestPayload.Parse(open.PayloadJson);
        var prioritized = action.WorkEpisodeId is { } episodeId ? payload?.PlaybackEpisodeIds?.Contains(episodeId) == true : payload?.PlaybackWork == true;
        var hasTarget = state.MediaType == WorkMediaType.Movie || action.WorkEpisodeId is not null;
        return policy.AllowsInstantAcquisition && hasTarget && !prioritized && open.Status != AcquisitionRequestStatus.Pending
            ? await AttachAsync(state, open, action, policy, cancellationToken)
            : await ReportAsync(open, action, policy, cancellationToken);
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
    /// nothing is promised for it.
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

        var fallback = VideoRequestPayload.Default(request.Kind, action.WorkId, state.Title, state.Year);
        if (!await requestStore.PatchPayloadAsync(request.Id, stored => WithPlaybackUnit(VideoRequestPayload.Parse(stored) ?? fallback, action.WorkEpisodeId).Serialize(), cancellationToken))
        {
            return new PlaybackIntentResult(PlaybackIntentOutcome.NotAvailable, action, null, null);
        }

        // An approved request that is idle (waiting out a back-off or for a future episode) searches for the unit now; one that is
        // already searching, downloading or importing picks it up as soon as it is free.
        if (request.Status == AcquisitionRequestStatus.Approved)
        {
            await requests.ContinueAsync(request.Id, cancellationToken);
        }

        return await ReportAsync(await requestStore.GetAsync(request.Id, cancellationToken) ?? request, action, policy, cancellationToken);
    }

    /// <summary>The scope of a request created by an intent: a Movie as a whole, or exactly the target episode and nothing of the rest of the Series.</summary>
    private static VideoRequestPayload SmallestPayload(VideoPlaybackState state, PrimaryAction action) =>
        action.WorkEpisodeId is { } episodeId
            ? WithPlaybackUnit(new VideoRequestPayload(action.WorkId, state.Title, state.Year, VideoRequestScope.Custom, [episodeId], MonitorFuture: false, SelectedSeasonIds: []), episodeId)
            : WithPlaybackUnit(VideoRequestPayload.Default(MediaAcquisitionKind.Movie, action.WorkId, state.Title, state.Year), null);

    /// <summary>Adds the playback unit without touching the scope; adding the same unit again changes nothing.</summary>
    private static VideoRequestPayload WithPlaybackUnit(VideoRequestPayload payload, Guid? workEpisodeId)
    {
        if (workEpisodeId is not { } episodeId)
        {
            return payload with { PlaybackWork = true };
        }

        var current = payload.PlaybackEpisodeIds ?? [];
        return current.Contains(episodeId) ? payload : payload with { PlaybackEpisodeIds = [.. current.Append(episodeId).TakeLast(VideoRequestPayload.MaxPlaybackEpisodes)] };
    }
}
