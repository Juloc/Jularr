using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

public enum VideoRequestScope
{
    WholeWork,
    AllCurrentAndFuture,
    FutureOnly,
    Custom
}

/// <summary>One profile's wait for one unit: the episode, or the Movie itself when <paramref name="WorkEpisodeId"/> is null.</summary>
public sealed record PlaybackMarker(Guid? WorkEpisodeId, string ProfileId, DateTime AtUtc);

/// <summary>
/// Durable request/search state for Movie and TV. TV structural selection uses canonical WorkSeason
/// and WorkEpisode ids; future monitoring is a property of the same Scope, never a second
/// consumer-facing toggle.
/// </summary>
public sealed record VideoRequestPayload(
    Guid WorkId,
    string Title,
    int? Year,
    VideoRequestScope Scope,
    Guid[] SelectedEpisodeIds,
    bool MonitorFuture,
    Guid? ActiveWorkEpisodeId = null,
    int? ActiveSeasonNumber = null,
    int? ActiveEpisodeNumber = null,
    Guid[]? SelectedSeasonIds = null) : ReleaseRequestPayload
{
    /// <summary>
    /// Admin-owned (see <see cref="Reconcile"/>): false when monitoring was turned off. An unmonitored request is completed by the next
    /// pass and never searches; a request without this field is monitored.
    /// </summary>
    public bool Monitored { get; init; } = true;

    /// <summary>Admin-owned: the moment "future" counts from; null means the request's creation, which <see cref="VideoRequestSelection"/> applies.</summary>
    public DateTime? MonitorFutureFromUtc { get; init; }

    /// <summary>Admin-owned: episodes the selection would include that were unchecked on purpose.</summary>
    public Guid[]? ExcludedEpisodeIds { get; init; }

    /// <summary>Admin-owned: seasons switched off as a whole, so episodes added to them later stay unmonitored whatever the scope.</summary>
    public Guid[]? ExcludedSeasonIds { get; init; }

    /// <summary>Admin-owned: counts every Admin change, so a search that started before one can tell and keep the change's wake-up.</summary>
    public int ScopeRevision { get; init; }

    /// <summary>
    /// Playback intents (Instant Play): the units profiles asked to watch now, each with who asked and when. A marker is searched ahead of
    /// the rest of the request, downloaded at high priority and, for an episode, part of the selection whatever the scope says (so an
    /// approved request can serve a play intent without a second request). It is a transient wait, not saved scope: it lapses after
    /// <see cref="PlaybackTtl"/>, at most <see cref="MaxPlaybackMarkers"/> exist, and the importer drops a marker once its unit has a file.
    /// </summary>
    public PlaybackMarker[]? PlaybackMarkers { get; init; }

    /// <summary>When an intent last reset the back-off of this request, so repeated intents cannot force a search every time.</summary>
    public DateTime? PlaybackResetUtc { get; init; }

    /// <summary>The audio language the requester chose in Language &amp; Edition of the Request dialog: a preference the approver sees, as for an anime request; null for the release default.</summary>
    public string? AudioLanguage { get; init; }

    /// <summary>The subtitle language the requester chose, <see cref="Jularr.Web.Features.Playback.PlaybackLanguages.SubtitlesOff"/> for none, or null for the release default.</summary>
    public string? SubtitleLanguage { get; init; }

    public const int MaxPlaybackMarkers = 16;

    public static readonly TimeSpan PlaybackTtl = TimeSpan.FromHours(2);

    public IEnumerable<PlaybackMarker> ActivePlaybackMarkers(DateTime nowUtc) => (PlaybackMarkers ?? []).Where(marker => nowUtc - marker.AtUtc < PlaybackTtl);

    public bool HasPlaybackIntent(DateTime nowUtc) => ActivePlaybackMarkers(nowUtc).Any();

    /// <summary>Whether a profile is waiting for the unit (<paramref name="workEpisodeId"/> null is the Movie itself).</summary>
    public bool IsPlaybackUnit(Guid? workEpisodeId, DateTime nowUtc) => ActivePlaybackMarkers(nowUtc).Any(marker => marker.WorkEpisodeId == workEpisodeId);

    /// <summary>The payload with the scope a requester chose when editing a request that still waits; the change counts as a scope revision, so a search that started before it can tell.</summary>
    public VideoRequestPayload WithRequesterScope(VideoRequestPayload edited) => this with
    {
        Scope = edited.Scope,
        SelectedSeasonIds = edited.SelectedSeasonIds,
        SelectedEpisodeIds = edited.SelectedEpisodeIds,
        MonitorFuture = edited.MonitorFuture,
        ScopeRevision = ScopeRevision + 1
    };

    /// <summary>The payload once nobody is waiting for a unit any more.</summary>
    public VideoRequestPayload WithoutPlaybackIntent() => this with { PlaybackMarkers = null, PlaybackResetUtc = null };

    /// <summary>Whether the payload stored now still has the Admin scope revision a run read; the guard of a result that ends a request.</summary>
    public static Func<string?, bool> StillAtRevision(int revision) => stored => (Parse(stored)?.ScopeRevision ?? 0) == revision;

    /// <summary>
    /// The one place stored payload JSON becomes a payload. Older or hand-edited rows may omit or null the collections the constructor
    /// declares non-null; they read as empty here, so no consumer ever meets a null and one such row cannot take a page down. JSON that
    /// is not a payload at all reads as null and the caller falls back to <see cref="Default"/>.
    /// </summary>
    public static VideoRequestPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VideoRequestPayload>(json, JsonSerializerOptions.Web) is { } payload
                ? payload with { Title = payload.Title ?? string.Empty, SelectedEpisodeIds = payload.SelectedEpisodeIds ?? [], ExcludedSeasonIds = payload.ExcludedSeasonIds ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A search computes its payload from the request it read at the start and stores it at the end. Admin edits the scope in between
    /// (VideoMonitoringService), so what is stored now wins for every Admin-owned field; when an edit happened meanwhile its wake-up
    /// (search count, next search, last problem) wins too, so the edit is looked at by the next pass instead of waiting out a back-off.
    /// </summary>
    public override ReleaseRequestPayload Reconcile(string? storedJson)
    {
        if (Parse(storedJson) is not { } stored)
        {
            return this;
        }

        var merged = this with
        {
            Scope = stored.Scope,
            SelectedEpisodeIds = stored.SelectedEpisodeIds,
            SelectedSeasonIds = stored.SelectedSeasonIds,
            MonitorFuture = stored.MonitorFuture,
            Monitored = stored.Monitored,
            MonitorFutureFromUtc = stored.MonitorFutureFromUtc,
            ExcludedEpisodeIds = stored.ExcludedEpisodeIds,
            ExcludedSeasonIds = stored.ExcludedSeasonIds,
            ScopeRevision = stored.ScopeRevision,
            PlaybackMarkers = stored.PlaybackMarkers,
            PlaybackResetUtc = stored.PlaybackResetUtc
        };
        return stored.ScopeRevision == ScopeRevision
            ? merged
            : merged with { Searches = stored.Searches, NextSearchUtc = stored.NextSearchUtc, LastProblem = stored.LastProblem };
    }

    /// <summary>
    /// The payload a request carries; a request without a readable one covers the whole Movie, or the whole Series with future
    /// episodes. The executor and the detail pages both read a request through this, so they agree on its scope.
    /// </summary>
    public static VideoRequestPayload Of(AcquisitionRequest request, Guid workId, string title, int? year) =>
        Parse(request.PayloadJson) is { } stored
            ? stored.Title.Length == 0 ? stored with { Title = title } : stored
            : Default(request.Kind, workId, title, year);

    /// <summary>The scope of a title requested without a choice: the whole Movie, or every episode of a Series and every future one.</summary>
    public static VideoRequestPayload Default(MediaAcquisitionKind kind, Guid workId, string title, int? year) =>
        kind == MediaAcquisitionKind.Movie
            ? new VideoRequestPayload(workId, title, year, VideoRequestScope.WholeWork, [], MonitorFuture: false)
            : new VideoRequestPayload(workId, title, year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true);
}

/// <summary>
/// The one owner of which TV episodes a request covers: the executor uses it to decide what to search and the
/// detail page to show each episode's request state, so the two can never disagree about the scope.
/// </summary>
/// <param name="nowUtc">The moment playback markers are judged at: a lapsed marker neither covers an episode nor orders it first; null means now.</param>
public sealed class VideoRequestSelection(VideoRequestPayload payload, DateTime requestCreatedAt, DateTime? nowUtc = null)
{
    /// <summary>The selection of an open request of a Series Work; the request title stands in until the payload says more.</summary>
    public static VideoRequestSelection For(AcquisitionRequest request, Guid workId, DateTime? nowUtc = null) =>
        new(VideoRequestPayload.Of(request, workId, request.Title, null), request.CreatedAt, nowUtc);

    public VideoRequestPayload Payload => payload;

    private readonly HashSet<Guid> selectedEpisodes = payload.SelectedEpisodeIds.ToHashSet();
    private readonly HashSet<Guid> selectedSeasons = (payload.SelectedSeasonIds ?? []).ToHashSet();
    private readonly HashSet<Guid> excludedEpisodes = (payload.ExcludedEpisodeIds ?? []).ToHashSet();
    private readonly HashSet<Guid> excludedSeasons = (payload.ExcludedSeasonIds ?? []).ToHashSet();
    private readonly HashSet<Guid> playbackEpisodes = payload.ActivePlaybackMarkers(nowUtc ?? DateTime.UtcNow).Select(marker => marker.WorkEpisodeId).OfType<Guid>().ToHashSet();

    /// <summary>"Future" is what aired after this moment: the one Admin set on the last scope change, else the creation of the request.</summary>
    private readonly DateTime futureFromUtc = payload.MonitorFutureFromUtc ?? requestCreatedAt;

    /// <summary>
    /// An excluded episode is never included; an episode of an excluded season only when it was switched on by itself. Whatever the scope,
    /// an episode or season the admin selected explicitly is included, so one switch never has to rewrite the scope. An episode a profile
    /// asked to watch now counts even when the scope does not name it, but an Admin exclusion beats that, and turning monitoring off ends
    /// everything.
    /// </summary>
    public bool Includes(Guid episodeId, Guid? seasonId, DateTime? airedAt) =>
        payload.Monitored
        && !excludedEpisodes.Contains(episodeId)
        && (seasonId is not { } excluded || !excludedSeasons.Contains(excluded) || selectedEpisodes.Contains(episodeId))
        && (playbackEpisodes.Contains(episodeId)
            || payload.Scope switch
            {
                VideoRequestScope.AllCurrentAndFuture => true,
                VideoRequestScope.FutureOnly =>
                    selectedEpisodes.Contains(episodeId)
                    || seasonId is { } futureSeason && selectedSeasons.Contains(futureSeason)
                    || airedAt is not null && airedAt > futureFromUtc,
                VideoRequestScope.Custom =>
                    selectedEpisodes.Contains(episodeId)
                    || seasonId is { } season && selectedSeasons.Contains(season)
                    || (payload.MonitorFuture && airedAt is not null && airedAt > futureFromUtc),
                _ => false
            });

    /// <summary>Whether a profile asked to watch the episode now: the executor takes these first and downloads them at high priority.</summary>
    public bool IsPlaybackEpisode(Guid episodeId) => playbackEpisodes.Contains(episodeId);
}

/// <summary>
/// Shared Movie/TV Request -> Wanted -> Usenet execution. It deliberately owns no timer: retries,
/// download state and completed-import dispatch stay in WantedAcquisitionService.
/// </summary>
public sealed partial class VideoAcquisitionEngine(
    AppDbContext db,
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    DownloadClientSubmissionService downloads,
    MediaAcquisitionRegistry registry,
    QualityProfileStore profiles,
    ReleaseRequestTracker tracker,
    AcquisitionAccessStore requestStore,
    VideoRequestWorkResolver works,
    TimeProvider clock)
{
    public const string OperationKind = "video-usenet-download";
    private static readonly IReadOnlyDictionary<string, string> EmptyIds = new Dictionary<string, string>();

    public async Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "Video acquisition only supports Movie and TV.");
        }

        var target = await ResolveTargetAsync(request, cancellationToken);
        if (target is null)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "The canonical Movie/TV Work for this provider identity no longer exists.");
        }

        var payload = VideoRequestPayload.Of(request, target.WorkId, target.Title, target.Year);
        payload = payload with
        {
            WorkId = target.WorkId,
            Title = target.Title,
            Year = target.Year
        };

        if (!payload.Monitored)
        {
            return await MonitoringOffAsync(request.Kind, payload.WorkId, payload.ScopeRevision, cancellationToken);
        }

        VideoUnit? unit = null;
        var scope = VideoUnitScope.Empty;
        if (request.Kind == MediaAcquisitionKind.Movie)
        {
            if (await HasMovieFileAsync(target.WorkId, cancellationToken))
            {
                await DropSatisfiedPlaybackIntentAsync(request, payload, cancellationToken);
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Completed,
                    "Movie is already available in the library.",
                    ResultUrl: VideoWorkLinks.DetailPath(request.Kind, payload.WorkId));
            }
        }
        else
        {
            scope = await FindWantedTvUnitsAsync(request, payload, cancellationToken);
            unit = scope.Wanted.FirstOrDefault();
            if (unit is null)
            {
                var continuation = await TvContinuationAsync(request, payload, cancellationToken);
                if (!continuation.KeepOpen)
                {
                    await DropSatisfiedPlaybackIntentAsync(request, payload, cancellationToken);
                    return new AcquisitionExecution(AcquisitionRequestStatus.Completed, "All requested TV episodes are available.", ResultUrl: VideoWorkLinks.DetailPath(request.Kind, payload.WorkId))
                    {
                        StillApplies = VideoRequestPayload.StillAtRevision(payload.ScopeRevision)
                    };
                }

                var waiting = payload with
                {
                    TriedReleases = [],
                    Searches = 0,
                    LastProblem = null,
                    NextSearchUtc = continuation.NextSearchUtc,
                    ActiveWorkEpisodeId = null,
                    ActiveSeasonNumber = null,
                    ActiveEpisodeNumber = null
                };
                await tracker.SaveAsync(request, waiting, cancellationToken);
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Approved,
                    continuation.HasMissingDue
                        ? "Searching for the next requested TV episode."
                        : "Waiting for the next requested TV episode to become available.",
                    ResultUrl: VideoWorkLinks.DetailPath(request.Kind, payload.WorkId));
            }

            payload = WithActiveUnit(payload, unit);
        }

        var setupProblem = await FindSetupProblemAsync(cancellationToken);
        if (setupProblem != VideoAcquisitionSetupProblem.None)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, SetupProblemMessage(setupProblem));
        }

        var profile = await profiles.ResolveAsync(request.Kind, target.WorkId, cancellationToken);
        var evaluation = await SearchAndEvaluateAsync(request, payload, unit, scope, target.ExternalIds ?? EmptyIds, profile, new SearchOptions { Purpose = SearchPurpose.Automatic }, cancellationToken);

        return await GrabAsync(request, payload, unit, evaluation.Grabbable, FailureMessage(evaluation, request.Kind), cancellationToken);
    }

    /// <summary>
    /// Runs the tracker lifecycle over the given releases (best first) and submits the first untried one through the shared
    /// download-client path. Automatic acquisition passes every ranked release; Manual Search passes the one the owner selected.
    /// </summary>
    private async Task<AcquisitionExecution> GrabAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        VideoUnit? unit,
        IReadOnlyList<VideoReleaseEvaluation> releases,
        string noReleaseReason,
        CancellationToken cancellationToken,
        VideoGrabProgress? progress = null)
    {
        // Admin may have changed monitoring while the indexers were searched; look again before anything is stored or grabbed, so a grab that is
        // dropped never marks its release as tried. A Manual Search grab chose its episode itself, so only Off applies to it. An Off that lands
        // after this look is handled by the download's own lifecycle.
        if (await StopWhenNoLongerWantedAsync(request, payload.WorkId, progress is null ? unit : null, cancellationToken) is { } stopped)
        {
            return stopped;
        }

        var downloadTitle = request.Kind == MediaAcquisitionKind.Movie ? "Download Movie" : "Download TV";
        // A unit a profile asked to watch now overtakes the other queued work, in the download client as well.
        var prioritized = payload.IsPlaybackUnit(unit?.Id, clock.GetUtcNow().UtcDateTime);
        var priority = prioritized ? OperationPriority.High : OperationPriority.Normal;
        var mediaTarget = unit is null ? VideoWorkLinks.WorkTarget(payload.WorkId) : VideoWorkLinks.EpisodeTarget(unit.Id);
        var candidates = releases.Select(x => new ReleaseRequestCandidate(x.Candidate.Identity, x.Candidate.Title, x.Candidate.InternalDownloadUri!)).ToArray();
        var byIdentity = releases.ToDictionary(x => x.Candidate.Identity, x => x.Candidate, StringComparer.Ordinal);
        var execution = await tracker.ContinueAsync(
            request,
            payload,
            candidates,
            noReleaseReason,
            async release =>
            {
                progress?.SubmitStarted = true;

                // A release several indexers returned is one candidate with several sources: when the download client refuses the first
                // source, the next one is offered before the release counts as failed.
                var sources = byIdentity[release.Identity].Sources.Select(source => source.DownloadUri).OfType<Uri>().Distinct().ToArray();
                var outcome = default(DownloadSubmissionOutcome)!;
                foreach (var uri in sources.Length == 0 ? [release.DownloadUri] : sources)
                {
                    var spec = new DownloadSubmissionSpec(OperationKind, downloadTitle, payload.Title, request.RequestedByProfileId, uri, release.Title, request.Kind, MediaTargetKey: mediaTarget, Priority: priority);
                    outcome = await downloads.SubmitAsync(spec, cancellationToken);
                    if (outcome.Accepted)
                    {
                        break;
                    }
                }

                if (outcome.Accepted && progress is not null)
                {
                    progress.Accepted = true;
                    progress.OperationId = outcome.OperationId;
                }

                return new ReleaseRequestSubmission(outcome.Accepted, outcome.OperationId, outcome.Message);
            },
            cancellationToken);

        return execution with { ResultUrl = VideoWorkLinks.DetailPath(request.Kind, payload.WorkId) };
    }

    private static VideoRequestPayload WithActiveUnit(VideoRequestPayload payload, VideoUnit unit) =>
        payload with
        {
            ActiveWorkEpisodeId = unit.Id,
            ActiveSeasonNumber = unit.SeasonNumber,
            ActiveEpisodeNumber = unit.EpisodeNumber,
            NextSearchUtc = null
        };

    /// <summary>The one search + scoring pipeline: automatic acquisition and Manual Search both read their candidates from here.</summary>
    private async Task<VideoSearchEvaluation> SearchAndEvaluateAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        VideoUnit? unit,
        VideoUnitScope scope,
        IReadOnlyDictionary<string, string> externalIds,
        QualityProfile profile,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        var kind = request.Kind;
        var intent = new SearchIntent(kind, payload.Title)
        {
            Year = payload.Year,
            ExternalIds = externalIds,
            Season = unit?.SeasonNumber,
            Episode = unit?.EpisodeNumber
        };
        var parser = registry.ParserFor(kind);
        VideoJudgement Judge(ProwlarrReleaseCandidate release) => VideoReleaseJudge.Judge(parser, kind, payload.Title, payload.Year, unit, scope, release);
        var search = await indexers.SearchAsync(
            intent,
            options with { UsableCount = releases => releases.Count(release => Judge(release).Evidence.Confidence is IdentityConfidence.Exact or IdentityConfidence.Strong) },
            cancellationToken);

        // One selection for the whole result: the same engine ranks what automatic acquisition grabs and what Manual Search lists.
        var judged = search.Releases.GroupBy(release => release.Identity, StringComparer.Ordinal).ToDictionary(group => group.Key, group => (Release: group.First(), Judgement: Judge(group.First())), StringComparer.Ordinal);
        var wantedSince = new DateTimeOffset(DateTime.SpecifyKind(unit?.AiredAt is { } aired && aired > request.CreatedAt ? aired : request.CreatedAt, DateTimeKind.Utc));
        var selection = ReleaseSelectionEngine.Select(
            profile,
            new SelectionContext(clock.GetUtcNow(), wantedSince),
            [.. judged.Select(pair => new SelectionCandidate(
                pair.Key,
                pair.Value.Judgement.Parsed,
                pair.Value.Release.SizeBytes,
                pair.Value.Release.Indexer,
                pair.Value.Release.Sources.FirstOrDefault()?.Priority ?? 0,
                pair.Value.Release.PublishedAt,
                pair.Value.Judgement.Evidence,
                pair.Value.Judgement.Coverage)
            {
                SafetyRejection = pair.Value.Judgement.SafetyRejection
            })]);
        var evaluations = selection.Ranked
            .Select(ranked => new VideoReleaseEvaluation(judged[ranked.Candidate.Id].Release, judged[ranked.Candidate.Id].Judgement.Parsed, judged[ranked.Candidate.Id].Judgement.Match, ranked))
            .ToArray();
        return new VideoSearchEvaluation(profile, search, evaluations, selection);
    }

    /// <summary>
    /// How a request ends when monitoring is turned off: Completed when the title has local media, otherwise Rejected, so a requester never
    /// sees "available" for a title nothing was acquired for.
    /// </summary>
    public async Task<AcquisitionRequestStatus> StatusWhenMonitoringStopsAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken)
    {
        var hasMedia = kind == MediaAcquisitionKind.Movie
            ? await HasMovieFileAsync(workId, cancellationToken)
            : await db.MediaAssets.AsNoTracking()
                .Where(asset => asset.WorkId == workId && asset.WorkEpisodeId != null && asset.Kind == MediaAssetKind.Video)
                .AnyAsync(asset => db.StoredFiles.Any(file => file.MediaAssetId == asset.Id), cancellationToken);
        return hasMedia ? AcquisitionRequestStatus.Completed : AcquisitionRequestStatus.Rejected;
    }

    /// <summary>The result of an Off the run read at <paramref name="scopeRevision"/>; it ends the request only while no Admin edit has followed.</summary>
    private async Task<AcquisitionExecution> MonitoringOffAsync(MediaAcquisitionKind kind, Guid workId, int scopeRevision, CancellationToken cancellationToken) =>
        new(await StatusWhenMonitoringStopsAsync(kind, workId, cancellationToken), VideoMonitoringService.MonitoringTurnedOff, ResultUrl: VideoWorkLinks.DetailPath(kind, workId))
        {
            StillApplies = VideoRequestPayload.StillAtRevision(scopeRevision)
        };

    /// <summary>
    /// Re-reads the request and returns how the execution ends when Admin turned monitoring off meanwhile (completed) or removed the
    /// episode being searched from the selection (back to waiting, so the next pass picks the new selection); null when the search is still wanted.
    /// </summary>
    private async Task<AcquisitionExecution?> StopWhenNoLongerWantedAsync(AcquisitionRequest request, Guid workId, VideoUnit? unit, CancellationToken cancellationToken)
    {
        var fresh = await requestStore.GetAsync(request.Id, cancellationToken) ?? request;
        var selection = VideoRequestSelection.For(fresh, workId, clock.GetUtcNow().UtcDateTime);
        if (!selection.Payload.Monitored)
        {
            return await MonitoringOffAsync(request.Kind, workId, selection.Payload.ScopeRevision, cancellationToken);
        }

        return unit is not null && !selection.Includes(unit.Id, unit.SeasonId, unit.AiredAt)
            ? new AcquisitionExecution(AcquisitionRequestStatus.Approved, "The monitored episodes changed; searching again.", ResultUrl: VideoWorkLinks.DetailPath(request.Kind, workId))
            : null;
    }

    public async Task PrepareAfterProblemAsync(
        AcquisitionRequest request,
        string problem,
        CancellationToken cancellationToken)
    {
        var payload = VideoRequestPayload.Parse(request.PayloadJson);
        if (payload is null)
        {
            return;
        }

        await tracker.SaveAsync(request, ReleaseRequestTracker.AfterProblem(payload, problem), cancellationToken);
    }

    /// <summary>
    /// Returns true when Wanted must keep a TV request open after a successful import. Movie imports
    /// complete normally. A TV season pack may satisfy several units; the next pass re-reads canonical
    /// Assets and therefore skips everything the importer already attached.
    /// </summary>
    public async Task<bool> ContinueAfterCompletedImportAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        var payload = VideoRequestPayload.Parse(request.PayloadJson);
        if (payload is null)
        {
            return false;
        }

        await DropSatisfiedPlaybackIntentAsync(request, payload, cancellationToken);

        if (request.Kind == MediaAcquisitionKind.Movie)
        {
            return false;
        }

        var reset = payload with
        {
            TriedReleases = [],
            Searches = 0,
            LastProblem = null,
            NextSearchUtc = null,
            ActiveWorkEpisodeId = null,
            ActiveSeasonNumber = null,
            ActiveEpisodeNumber = null
        };

        var continuation = await TvContinuationAsync(request, reset, cancellationToken);
        if (!continuation.KeepOpen)
        {
            await tracker.SaveAsync(request, reset, cancellationToken);
            return false;
        }

        reset = reset with { NextSearchUtc = continuation.HasMissingDue ? null : continuation.NextSearchUtc };
        await tracker.SaveAsync(request, reset, cancellationToken);
        await requestStore.UpdateStatusAsync(
            request.Id,
            AcquisitionRequestStatus.Approved,
            continuation.HasMissingDue
                ? "Imported episode(s). Searching for the next requested episode."
                : "Imported requested episodes. Waiting for the next requested TV episode.",
            request.OperationId,
            VideoWorkLinks.DetailPath(request.Kind, reset.WorkId),
            decidedByProfileId: null,
            cancellationToken);
        return true;
    }

    /// <summary>
    /// A playback intent is about units that are not playable yet: once one has a file it leaves the request, so the request stops being
    /// searched ahead of others for it. Only satisfied units go; a unit another profile attached meanwhile stays. This is a separate
    /// compare-and-set write because a search save keeps the stored playback markers (see <see cref="VideoRequestPayload.Reconcile"/>).
    /// </summary>
    private async Task DropSatisfiedPlaybackIntentAsync(AcquisitionRequest request, VideoRequestPayload payload, CancellationToken cancellationToken)
    {
        if (payload.PlaybackMarkers is not { Length: > 0 } markers)
        {
            return;
        }

        var episodeIds = markers.Select(marker => marker.WorkEpisodeId).OfType<Guid>().ToArray();
        var withMedia = episodeIds.Length == 0
            ? []
            : await db.MediaAssets.AsNoTracking()
                .Where(x => x.WorkEpisodeId != null && episodeIds.Contains(x.WorkEpisodeId.Value) && x.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == x.Id))
                .Select(x => x.WorkEpisodeId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
        var movieDone = request.Kind == MediaAcquisitionKind.Movie && markers.Any(marker => marker.WorkEpisodeId is null) && await HasMovieFileAsync(payload.WorkId, cancellationToken);
        if (movieDone || withMedia.Count > 0)
        {
            await requestStore.PatchPayloadAsync(
                request.Id,
                stored => VideoRequestPayload.Parse(stored) is { } current
                    ? (current with { PlaybackMarkers = [.. (current.PlaybackMarkers ?? []).Where(marker => marker.WorkEpisodeId is { } id ? !withMedia.Contains(id) : !movieDone)] }).Serialize()
                    : stored,
                cancellationToken);
        }
    }

    /// <summary>The canonical Work of the request, from the one identity lookup (<see cref="VideoRequestWorkResolver"/>), or null when it no longer exists.</summary>
    private async Task<VideoRequestWork?> ResolveTargetAsync(AcquisitionRequest request, CancellationToken cancellationToken) =>
        (await works.ResolveAsync([request], cancellationToken)).GetValueOrDefault(request.Id);

    public async Task<bool> HasMovieFileAsync(Guid workId, CancellationToken cancellationToken) =>
        await db.MediaAssets.AsNoTracking()
            .Where(x => x.WorkId == workId
                        && x.WorkEpisodeId == null
                        && x.Kind == MediaAssetKind.Video)
            .AnyAsync(
                asset => db.StoredFiles.AsNoTracking()
                    .Any(file => file.MediaAssetId == asset.Id),
                cancellationToken);

    private async Task<VideoUnit?> FindNextTvUnitAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        CancellationToken cancellationToken) =>
        (await FindWantedTvUnitsAsync(request, payload, cancellationToken)).Wanted.FirstOrDefault();

    private async Task<TvContinuation> TvContinuationAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var episodes = await LoadTvUnitsAsync(payload.WorkId, cancellationToken);
        var selection = new VideoRequestSelection(payload, request.CreatedAt, now);
        var missingIncluded = episodes
            .Where(x => !x.HasFile)
            .Where(x => selection.Includes(x.Id, x.SeasonId, x.AiredAt))
            .ToArray();
        var hasMissingDue = missingIncluded.Any(x => x.AiredAt is null || x.AiredAt <= now);

        if (hasMissingDue)
        {
            return new TvContinuation(true, true, null);
        }

        var nextKnown = missingIncluded
            .Where(x => x.AiredAt is DateTime airedAt && airedAt > now)
            .Select(x => x.AiredAt)
            .OrderBy(x => x)
            .FirstOrDefault();
        if (nextKnown is not null)
        {
            return new TvContinuation(true, false, nextKnown);
        }

        return payload.MonitorFuture
            ? new TvContinuation(true, false, now.AddHours(24))
            : new TvContinuation(false, false, null);
    }

    private async Task<IReadOnlyList<VideoUnit>> LoadTvUnitsAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var episodes = await db.WorkEpisodes.AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .Select(x => new { x.Id, x.SeasonId, x.SeasonNumber, x.EpisodeNumber, x.AiredAt })
            .ToListAsync(cancellationToken);

        if (episodes.Count == 0)
        {
            return [];
        }

        var episodeIds = episodes.Select(x => x.Id).ToArray();
        var playable = (await db.MediaAssets.AsNoTracking()
                .Where(x => x.WorkEpisodeId != null
                            && episodeIds.Contains(x.WorkEpisodeId.Value)
                            && x.Kind == MediaAssetKind.Video
                            && db.StoredFiles.Any(file => file.MediaAssetId == x.Id))
                .Select(x => x.WorkEpisodeId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return episodes.Select(x => new VideoUnit(
                x.Id,
                x.SeasonId,
                x.SeasonNumber,
                x.EpisodeNumber,
                x.AiredAt,
                playable.Contains(x.Id)))
            .ToArray();
    }

    /// <summary>What an empty or unusable search means: an indexer problem, nothing found, only another title, or the profile refusing what was found.</summary>
    private static string FailureMessage(VideoSearchEvaluation evaluation, MediaAcquisitionKind kind)
    {
        var search = evaluation.Search;
        var what = kind == MediaAcquisitionKind.Movie ? "Movie" : "TV";
        if (search.Releases.Count == 0)
        {
            return search.EveryIndexerFailed
                ? $"No indexer could be searched ({search.Warnings[0].IndexerName}: {search.Warnings[0].Message})."
                : search.Warnings.Count > 0
                    ? $"No release found ({search.Warnings[0].IndexerName}: {search.Warnings[0].Message})."
                    : "No release found on the indexers.";
        }

        return evaluation.Selection.Outcome switch
        {
            SelectionOutcome.ManualReviewOnly => $"Releases were found, but their identity needs a manual decision (Manual Search).",
            SelectionOutcome.IdentityInvalid => $"Releases were found, but none is the requested {what} title or episode.",
            _ => $"No suitable {what} release matched the requested title and quality profile."
        };
    }

    // The units of a Series that are still wanted, first the ones a profile waits for, then in season and episode order.
    private async Task<VideoUnitScope> FindWantedTvUnitsAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var episodes = await LoadTvUnitsAsync(payload.WorkId, cancellationToken);
        var selection = new VideoRequestSelection(payload, request.CreatedAt, now);
        var wanted = episodes
            .Where(x => !x.HasFile)
            .Where(x => selection.Includes(x.Id, x.SeasonId, x.AiredAt))
            .Where(x => x.AiredAt is null || x.AiredAt <= now)
            .OrderBy(x => selection.IsPlaybackEpisode(x.Id) ? 0 : 1)
            .ThenBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .ToArray();
        return new VideoUnitScope(episodes, wanted);
    }

    private sealed record TvContinuation(
        bool KeepOpen,
        bool HasMissingDue,
        DateTime? NextSearchUtc);
}

public abstract class VideoWantedRequestHandler(
    VideoAcquisitionEngine engine,
    AcquisitionRequestService requests) : IWantedRequestHandler
{
    public abstract MediaAcquisitionKind Kind { get; }

    public bool IsSearchDue(AcquisitionRequest request, DateTime nowUtc)
    {
        var payload = VideoRequestPayload.Parse(request.PayloadJson);
        return payload is null || ReleaseRequestTracker.IsSearchDue(payload, nowUtc);
    }

    public bool HasPlaybackPriority(AcquisitionRequest request, DateTime nowUtc) => VideoRequestPayload.Parse(request.PayloadJson)?.HasPlaybackIntent(nowUtc) == true;

    public async Task ContinueAfterProblemAsync(
        AcquisitionRequest request,
        string problem,
        CancellationToken cancellationToken)
    {
        await engine.PrepareAfterProblemAsync(request, problem, cancellationToken);
        await requests.ContinueAsync(request.Id, cancellationToken);
    }

    public Task<bool> ContinueAfterCompletedImportAsync(
        AcquisitionRequest request,
        CompletedDownloadImportResult result,
        CancellationToken cancellationToken) =>
        engine.ContinueAfterCompletedImportAsync(request, cancellationToken);
}

public sealed class MovieAcquisitionRequestExecutor(
    VideoAcquisitionEngine engine) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;

    public Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken) =>
        engine.ExecuteAsync(request, cancellationToken);
}

public sealed class TvAcquisitionRequestExecutor(
    VideoAcquisitionEngine engine) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Tv;

    public Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken) =>
        engine.ExecuteAsync(request, cancellationToken);
}

public sealed class MovieWantedRequestHandler(
    VideoAcquisitionEngine engine,
    AcquisitionRequestService requests) : VideoWantedRequestHandler(engine, requests)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;
}

public sealed class TvWantedRequestHandler(
    VideoAcquisitionEngine engine,
    AcquisitionRequestService requests) : VideoWantedRequestHandler(engine, requests)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Tv;
}
