using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Progress;
using Jularr.Web.Ui;

namespace Jularr.Web.Features.InstantPlay;

/// <summary>The one primary consumer action of a Movie or Series Work (docs/mockups/instant-play, section 3).</summary>
public enum PrimaryActionKind
{
    /// <summary>Nothing valid to offer: only personal and detail actions remain.</summary>
    None,

    /// <summary>The target is local and the profile has nothing to resume (a finished one is a rewatch, see <see cref="PrimaryAction.IsRewatch"/>).</summary>
    Play,

    Continue,

    /// <summary>A Series whose next required episode has not been started: local (<see cref="PrimaryAction.TargetIsLocal"/>) or to be acquired by the playback intent.</summary>
    StartWatching,

    /// <summary>An explicit playback intent for a Movie, or for one chosen episode, whose media is missing and may be acquired instantly.</summary>
    WatchNow,

    /// <summary>The explicit Request action: the media is missing and either needs approval or this instance does not play.</summary>
    Request,

    /// <summary>An equivalent request already exists: its state is shown and no second request is created.</summary>
    ShowRequestState,

    /// <summary>Manager-only instance: the media is imported and managed here, but Jularr offers no player.</summary>
    Available
}

public enum PrimaryActionReason
{
    LocalMedia,
    ResumePoint,
    InstantAcquisition,
    ApprovalRequired,
    AcquisitionNotReady,
    PlaybackDisabled,
    RequestActive,
    AwaitingApproval,
    NotPermitted,
    AcquisitionDisabled,
    MediaTypeDisabled,
    NoProviderIdentity,
    NotReleased,
    MonitoringStopped,
    ExcludedFromRequest,
    NothingToWatch,
    UnknownTarget
}

/// <summary>
/// The resolved action. <see cref="WorkEpisodeId"/> is the target episode of a Series (null for a Movie, or when no single episode is
/// the target); <see cref="TargetIsLocal"/> says whether it can be played right now, so a client opens the player for it or sends the
/// playback intent. <see cref="RequestIsAlternative"/> is set while a playback intent is the primary action of a missing target and the
/// profile may still ask for it through the explicit Request action instead, which stays the one consumer acquisition action (section 12).
/// </summary>
public sealed record PrimaryAction(PrimaryActionKind Kind, PrimaryActionReason Reason, Guid WorkId, Guid? WorkEpisodeId, bool TargetIsLocal = false, bool IsRewatch = false, bool RequestIsAlternative = false);

/// <summary>
/// What the instance, the profile and the policy allow for one media type: the capability chain of the Instant Play contract
/// (instance module -> authorization -> feature and policy state), resolved once so the resolver stays pure.
/// </summary>
/// <param name="AcquisitionReady">Indexers and a download client are configured; only read when it can change the action.</param>
public sealed record InstantPlayPolicy(bool MediaTypeEnabled, bool AcquisitionEnabled, bool PlaybackEnabled, bool CanRequest, bool AutoApproves, bool AcquisitionReady)
{
    public bool AllowsRequest => MediaTypeEnabled && AcquisitionEnabled && CanRequest;

    /// <summary>Missing media may be acquired by one explicit playback intent: this instance plays, the profile's request is approved by policy and acquisition can run.</summary>
    public bool AllowsInstantAcquisition => AllowsRequest && PlaybackEnabled && AutoApproves && AcquisitionReady;
}

/// <summary>
/// The open request of a title (a title has at most one) as far as a playback intent depends on it: which episodes its scope covers, which
/// an Admin excluded, whether monitoring is on and which units a profile already asked to watch. An Admin exclusion beats a playback intent.
/// </summary>
public sealed record OpenRequestFacts(
    AcquisitionRequestStatus Status,
    bool Monitored,
    bool CoversWork,
    IReadOnlySet<Guid> CoveredEpisodeIds,
    IReadOnlySet<Guid> ExcludedEpisodeIds,
    bool WorkPrioritized,
    IReadOnlySet<Guid> PrioritizedEpisodeIds)
{
    public bool AwaitsApproval => Status == AcquisitionRequestStatus.Pending;

    public bool Covers(Guid? workEpisodeId) => workEpisodeId is { } id ? CoveredEpisodeIds.Contains(id) : CoversWork;

    public bool IsExcluded(Guid? workEpisodeId) => workEpisodeId is { } id && ExcludedEpisodeIds.Contains(id);

    /// <summary>Whether a profile already asked to watch the unit, so it is searched and downloaded ahead of the rest.</summary>
    public bool IsPrioritized(Guid? workEpisodeId) => workEpisodeId is { } id ? PrioritizedEpisodeIds.Contains(id) : WorkPrioritized;

    public static OpenRequestFacts ForMovie(AcquisitionRequest request, DateTime nowUtc)
    {
        var payload = VideoRequestPayload.Parse(request.PayloadJson);
        return new OpenRequestFacts(request.Status, payload?.Monitored ?? true, true, new HashSet<Guid>(), new HashSet<Guid>(), payload?.IsPlaybackUnit(null, nowUtc) == true, new HashSet<Guid>());
    }

    /// <summary>The episodes the request's scope includes, by the same <see cref="VideoRequestSelection"/> the executor uses.</summary>
    public static OpenRequestFacts ForSeries(AcquisitionRequest request, VideoRequestSelection selection, IEnumerable<(Guid Id, Guid? SeasonId, DateTime? AiredAt)> episodes, DateTime nowUtc)
    {
        var payload = selection.Payload;
        var all = episodes.ToArray();
        var excludedSeasons = (payload.ExcludedSeasonIds ?? []).ToHashSet();
        var selected = payload.SelectedEpisodeIds.ToHashSet();
        var excluded = (payload.ExcludedEpisodeIds ?? [])
            .Concat(all.Where(x => x.SeasonId is { } season && excludedSeasons.Contains(season) && !selected.Contains(x.Id)).Select(x => x.Id))
            .ToHashSet();
        return new OpenRequestFacts(
            request.Status,
            payload.Monitored,
            false,
            all.Where(x => selection.Includes(x.Id, x.SeasonId, x.AiredAt)).Select(x => x.Id).ToHashSet(),
            excluded,
            false,
            payload.ActivePlaybackMarkers(nowUtc).Select(marker => marker.WorkEpisodeId).OfType<Guid>().ToHashSet());
    }
}

/// <summary>The canonical facts about one Movie or Series Work and one profile that the primary action depends on.</summary>
/// <param name="HasRequestIdentity">The provider identifies the title, so it can be requested.</param>
public abstract record PlaybackFacts(Guid WorkId, bool HasRequestIdentity, OpenRequestFacts? OpenRequest);

/// <param name="IsReleased">Not announced for a later year: the only release knowledge a Work has is its year, so a movie of the current year counts as released.</param>
public sealed record MoviePlaybackFacts(Guid WorkId, bool HasRequestIdentity, OpenRequestFacts? OpenRequest, bool HasMedia, MediaProgressSnapshot? Progress, bool IsReleased = true)
    : PlaybackFacts(WorkId, HasRequestIdentity, OpenRequest);

public sealed record SeriesPlaybackFacts(
    Guid WorkId,
    bool HasRequestIdentity,
    OpenRequestFacts? OpenRequest,
    IReadOnlyList<SeriesUnit> Units,
    IReadOnlyDictionary<Guid, EpisodeProgressState> Progress)
    : PlaybackFacts(WorkId, HasRequestIdentity, OpenRequest);

/// <summary>
/// The one effective primary-action rule of every Movie and Series surface (detail hero, playback intent, Library card): the
/// capability chain of the Instant Play contract applied to the canonical state, with no I/O. A card click never acquires anything;
/// acquisition only starts from an explicit Request or from the playback intent this resolver permits.
/// </summary>
public static class PrimaryActionResolver
{
    /// <param name="explicitEpisodeId">A Series episode the user chose; without one a Series targets <see cref="NextRequiredEpisode"/>.</param>
    public static PrimaryAction Resolve(PlaybackFacts facts, InstantPlayPolicy policy, Guid? explicitEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.MediaTypeEnabled)
        {
            return None(facts, PrimaryActionReason.MediaTypeDisabled);
        }

        return facts switch
        {
            MoviePlaybackFacts movie => ResolveMovie(movie, policy),
            SeriesPlaybackFacts series => ResolveSeries(series, policy, explicitEpisodeId),
            _ => throw new ArgumentOutOfRangeException(nameof(facts))
        };
    }

    private static PrimaryAction ResolveMovie(MoviePlaybackFacts movie, InstantPlayPolicy policy)
    {
        if (movie.HasMedia)
        {
            var (kind, rewatch) = movie.Progress switch
            {
                { IsCompleted: true } => (PrimaryActionKind.Play, true),
                { ResumePositionMs: > 0 } => (PrimaryActionKind.Continue, false),
                _ => (PrimaryActionKind.Play, false)
            };
            return Local(movie, null, kind, rewatch, policy);
        }

        return Missing(movie, null, hasTarget: true, PrimaryActionKind.WatchNow, movie.IsReleased, policy);
    }

    private static PrimaryAction ResolveSeries(SeriesPlaybackFacts series, InstantPlayPolicy policy, Guid? explicitEpisodeId)
    {
        if (explicitEpisodeId is { } chosen)
        {
            if (series.Units.FirstOrDefault(x => x.Id == chosen) is not { } unit)
            {
                return None(series, PrimaryActionReason.UnknownTarget);
            }

            if (!unit.HasMedia)
            {
                return Missing(series, unit.Id, hasTarget: true, PrimaryActionKind.WatchNow, unit.IsReleased, policy);
            }

            var (kind, rewatch) = series.Progress.TryGetValue(unit.Id, out var row) switch
            {
                true when row.IsCompleted => (PrimaryActionKind.Play, true),
                true when row.PositionMs >= VideoProgressService.MinimumResumeMs => (PrimaryActionKind.Continue, false),
                _ => (PrimaryActionKind.Play, false)
            };
            return Local(series, unit.Id, kind, rewatch, policy);
        }

        if (NextRequiredEpisode.Resolve(series.Units, series.Progress) is not { } next)
        {
            return Missing(series, null, hasTarget: false, PrimaryActionKind.StartWatching, released: false, policy);
        }

        if (!next.Unit.HasMedia)
        {
            return Missing(series, next.Unit.Id, hasTarget: true, PrimaryActionKind.StartWatching, next.Unit.IsReleased, policy);
        }

        var seriesKind = next.State switch
        {
            MediaBannerProgressState.NotStarted => PrimaryActionKind.StartWatching,
            MediaBannerProgressState.InProgress => PrimaryActionKind.Continue,
            _ => PrimaryActionKind.Play
        };
        return Local(series, next.Unit.Id, seriesKind, next.State == MediaBannerProgressState.Completed, policy);
    }

    private static PrimaryAction Local(PlaybackFacts facts, Guid? episodeId, PrimaryActionKind kind, bool rewatch, InstantPlayPolicy policy) =>
        policy.PlaybackEnabled
            ? new PrimaryAction(kind, kind == PrimaryActionKind.Continue ? PrimaryActionReason.ResumePoint : PrimaryActionReason.LocalMedia, facts.WorkId, episodeId, TargetIsLocal: true, IsRewatch: rewatch)
            : new PrimaryAction(PrimaryActionKind.Available, PrimaryActionReason.PlaybackDisabled, facts.WorkId, episodeId, TargetIsLocal: true);

    /// <summary>
    /// The target has no local media (<paramref name="hasTarget"/> is false when no episode needs playing now). An open request is
    /// never duplicated and never bypassed; a playback intent may only add the target to one that is already approved, is monitored and has not excluded it.
    /// </summary>
    private static PrimaryAction Missing(PlaybackFacts facts, Guid? episodeId, bool hasTarget, PrimaryActionKind instantKind, bool released, InstantPlayPolicy policy)
    {
        if (facts.OpenRequest is { } open)
        {
            // Admin curation beats a playback intent: monitoring that was turned off or an episode that was unchecked is not searched.
            if (hasTarget && !open.Monitored)
            {
                return None(facts, PrimaryActionReason.MonitoringStopped, episodeId);
            }

            if (hasTarget && open.IsExcluded(episodeId))
            {
                return None(facts, PrimaryActionReason.ExcludedFromRequest, episodeId);
            }

            // A unit the request does not cover yet, or covers without a profile waiting for it, takes the playback intent; it is never
            // taken while the request waits for approval.
            var takesIntent = policy.AllowsInstantAcquisition && hasTarget && released && !open.AwaitsApproval && (!open.Covers(episodeId) || !open.IsPrioritized(episodeId));
            if (takesIntent)
            {
                return new PrimaryAction(instantKind, PrimaryActionReason.InstantAcquisition, facts.WorkId, episodeId);
            }

            return new PrimaryAction(PrimaryActionKind.ShowRequestState, open.AwaitsApproval ? PrimaryActionReason.AwaitingApproval : PrimaryActionReason.RequestActive, facts.WorkId, episodeId);
        }

        if (!policy.AcquisitionEnabled)
        {
            return None(facts, PrimaryActionReason.AcquisitionDisabled, episodeId);
        }

        if (!policy.CanRequest)
        {
            return None(facts, PrimaryActionReason.NotPermitted, episodeId);
        }

        if (!facts.HasRequestIdentity)
        {
            return None(facts, PrimaryActionReason.NoProviderIdentity, episodeId);
        }

        if (policy.AllowsInstantAcquisition && hasTarget && released)
        {
            return new PrimaryAction(instantKind, PrimaryActionReason.InstantAcquisition, facts.WorkId, episodeId, RequestIsAlternative: true);
        }

        var reason = !policy.PlaybackEnabled ? PrimaryActionReason.PlaybackDisabled
            : !policy.AutoApproves ? PrimaryActionReason.ApprovalRequired
            : !hasTarget ? PrimaryActionReason.NothingToWatch
            : !released ? PrimaryActionReason.NotReleased
            : PrimaryActionReason.AcquisitionNotReady;
        return new PrimaryAction(PrimaryActionKind.Request, reason, facts.WorkId, episodeId);
    }

    private static PrimaryAction None(PlaybackFacts facts, PrimaryActionReason reason, Guid? episodeId = null) => new(PrimaryActionKind.None, reason, facts.WorkId, episodeId);
}
