using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
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
    /// Playback intent (Instant Play): the Movie itself is what a profile asked to watch now. It is searched ahead of the rest of the
    /// request and downloaded at high priority; it adds nothing to the scope.
    /// </summary>
    public bool PlaybackWork { get; init; }

    /// <summary>
    /// Playback intent: the episodes a profile asked to watch now, newest last and at most <see cref="MaxPlaybackEpisodes"/>. They are
    /// part of the selection whatever the scope says (so an approved request can serve a play intent without a second request),
    /// searched before the rest and downloaded at high priority. The importer drops an episode from the list once it has a file.
    /// </summary>
    public Guid[]? PlaybackEpisodeIds { get; init; }

    public const int MaxPlaybackEpisodes = 16;

    public bool HasPlaybackIntent => PlaybackWork || PlaybackEpisodeIds is { Length: > 0 };

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
            PlaybackWork = stored.PlaybackWork,
            PlaybackEpisodeIds = stored.PlaybackEpisodeIds
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
public sealed class VideoRequestSelection(VideoRequestPayload payload, DateTime requestCreatedAt)
{
    /// <summary>The selection of an open request of a Series Work; the request title stands in until the payload says more.</summary>
    public static VideoRequestSelection For(AcquisitionRequest request, Guid workId) =>
        new(VideoRequestPayload.Of(request, workId, request.Title, null), request.CreatedAt);

    public VideoRequestPayload Payload => payload;

    private readonly HashSet<Guid> selectedEpisodes = payload.SelectedEpisodeIds.ToHashSet();
    private readonly HashSet<Guid> selectedSeasons = (payload.SelectedSeasonIds ?? []).ToHashSet();
    private readonly HashSet<Guid> excludedEpisodes = (payload.ExcludedEpisodeIds ?? []).ToHashSet();
    private readonly HashSet<Guid> excludedSeasons = (payload.ExcludedSeasonIds ?? []).ToHashSet();
    private readonly HashSet<Guid> playbackEpisodes = (payload.PlaybackEpisodeIds ?? []).ToHashSet();

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
    private static readonly int[] MovieCategories = [2000];

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
        if (request.Kind == MediaAcquisitionKind.Movie)
        {
            if (await HasMovieFileAsync(target.WorkId, cancellationToken))
            {
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Completed,
                    "Movie is already available in the library.",
                    ResultUrl: VideoWorkLinks.DetailPath(request.Kind, payload.WorkId));
            }
        }
        else
        {
            unit = await FindNextTvUnitAsync(request, payload, cancellationToken);
            if (unit is null)
            {
                var continuation = await TvContinuationAsync(request, payload, cancellationToken);
                if (!continuation.KeepOpen)
                {
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
        var evaluation = await SearchAndEvaluateAsync(request.Kind, payload, unit, profile, cancellationToken);
        var ranked = Rank(evaluation.Releases);

        return await GrabAsync(request, payload, unit, ranked, FailureMessage(evaluation.Search, request.Kind), cancellationToken);
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
        var prioritized = unit is null ? payload.PlaybackWork : payload.PlaybackEpisodeIds?.Contains(unit.Id) == true;
        var priority = prioritized ? OperationPriority.High : OperationPriority.Normal;
        var mediaTarget = unit is null ? VideoWorkLinks.WorkTarget(payload.WorkId) : VideoWorkLinks.EpisodeTarget(unit.Id);
        var candidates = releases.Select(x => new ReleaseRequestCandidate(x.Candidate.Identity, x.Candidate.Title, x.Candidate.InternalDownloadUri!)).ToArray();
        var execution = await tracker.ContinueAsync(
            request,
            payload,
            candidates,
            noReleaseReason,
            async release =>
            {
                progress?.SubmitStarted = true;
                var spec = new DownloadSubmissionSpec(OperationKind, downloadTitle, payload.Title, request.RequestedByProfileId, release.DownloadUri, release.Title, request.Kind, MediaTargetKey: mediaTarget, Priority: priority);
                var outcome = await downloads.SubmitAsync(spec, cancellationToken);
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
        MediaAcquisitionKind kind,
        VideoRequestPayload payload,
        VideoUnit? unit,
        QualityProfile profile,
        CancellationToken cancellationToken)
    {
        var search = kind == MediaAcquisitionKind.Movie
            ? await SearchMovieAsync(payload, cancellationToken)
            : await SearchTvAsync(payload, unit!, cancellationToken);
        return new VideoSearchEvaluation(profile, search, Evaluate(kind, payload.Title, unit, search.Releases, profile));
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
        var selection = VideoRequestSelection.For(fresh, workId);
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

        if (request.Kind == MediaAcquisitionKind.Movie)
        {
            return false;
        }

        await DropPlaybackEpisodesWithMediaAsync(request, payload, cancellationToken);

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
    /// A playback intent is about episodes that are not playable yet: once an import gave one a file, it leaves the request's list, so
    /// the request stops being searched ahead of others for it. This is a separate compare-and-set write because a search save keeps
    /// the stored playback list (see <see cref="VideoRequestPayload.Reconcile"/>).
    /// </summary>
    private async Task DropPlaybackEpisodesWithMediaAsync(AcquisitionRequest request, VideoRequestPayload payload, CancellationToken cancellationToken)
    {
        if (payload.PlaybackEpisodeIds is not { Length: > 0 } episodeIds)
        {
            return;
        }

        var withMedia = await db.MediaAssets.AsNoTracking()
            .Where(x => x.WorkEpisodeId != null && episodeIds.Contains(x.WorkEpisodeId.Value) && x.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == x.Id))
            .Select(x => x.WorkEpisodeId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (withMedia.Count > 0)
        {
            await requestStore.PatchPayloadAsync(
                request.Id,
                stored => VideoRequestPayload.Parse(stored) is { } current ? (current with { PlaybackEpisodeIds = [.. (current.PlaybackEpisodeIds ?? []).Except(withMedia)] }).Serialize() : stored,
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
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var episodes = await LoadTvUnitsAsync(payload.WorkId, cancellationToken);
        var selection = new VideoRequestSelection(payload, request.CreatedAt);

        return episodes
            .Where(x => !x.HasFile)
            .Where(x => selection.Includes(x.Id, x.SeasonId, x.AiredAt))
            .Where(x => x.AiredAt is null || x.AiredAt <= now)
            .OrderBy(x => selection.IsPlaybackEpisode(x.Id) ? 0 : 1)
            .ThenBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .FirstOrDefault();
    }

    private async Task<TvContinuation> TvContinuationAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var episodes = await LoadTvUnitsAsync(payload.WorkId, cancellationToken);
        var selection = new VideoRequestSelection(payload, request.CreatedAt);
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

    private async Task<IndexerAnimeSearchResult> SearchMovieAsync(
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var query = payload.Year is int year ? $"{payload.Title} {year}" : payload.Title;
        var result = await indexers.SearchCategoriesAsync(
            [query],
            _ => MovieCategories,
            cancellationToken);
        if (result.Releases.Count == 0)
        {
            result = await indexers.SearchCategoriesAsync([query], _ => [], cancellationToken);
        }

        return result;
    }

    private Task<IndexerAnimeSearchResult> SearchTvAsync(
        VideoRequestPayload payload,
        VideoUnit unit,
        CancellationToken cancellationToken) =>
        indexers.SearchAsync(
            new IndexerAnimeSearchTarget(
                payload.Title,
                [],
                IndexerAnimeSearchMode.Episode,
                unit.SeasonNumber,
                unit.EpisodeNumber),
            cancellationToken);

    /// <summary>
    /// Parses and scores every returned candidate against the requested title and unit. Candidates that cannot be grabbed stay in the
    /// result with the reason, so Manual Search can explain them; automatic acquisition ranks only the grabbable ones. Identity is
    /// decided before the score: a high score never repairs a wrong title, season or episode.
    /// </summary>
    private IReadOnlyList<VideoReleaseEvaluation> Evaluate(
        MediaAcquisitionKind kind,
        string title,
        VideoUnit? unit,
        IReadOnlyList<ProwlarrReleaseCandidate> candidates,
        QualityProfile profile)
    {
        var parser = registry.ParserFor(kind);
        var evaluations = new List<VideoReleaseEvaluation>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.InternalDownloadUri is null)
            {
                evaluations.Add(new VideoReleaseEvaluation(candidate, null, null, VideoIdentityMatch.NoDownload));
            }
            else if (candidate.Protocol is not null && !candidate.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase))
            {
                evaluations.Add(new VideoReleaseEvaluation(candidate, null, null, VideoIdentityMatch.NotUsenet));
            }
            else if (!parser.TryParse(candidate.Title, out var parsed))
            {
                evaluations.Add(new VideoReleaseEvaluation(candidate, null, null, VideoIdentityMatch.Unparseable));
            }
            else
            {
                var identity = !TitleMatches(title, parsed.SeriesTitle) ? VideoIdentityMatch.WrongTitle : unit is null ? VideoIdentityMatch.Matches : Coverage(parsed, unit);
                var score = ReleaseScorer.Score(profile, new ReleaseCandidate(parsed, candidate.SizeBytes, candidate.Indexer, candidate.Identity));
                evaluations.Add(new VideoReleaseEvaluation(candidate, parsed, score, identity));
            }
        }

        return evaluations;
    }

    private static IReadOnlyList<VideoReleaseEvaluation> Rank(IReadOnlyList<VideoReleaseEvaluation> evaluations) =>
        evaluations
            .Where(x => x.IsGrabbable)
            .OrderBy(x => x.Score!.QualityRank)
            .ThenByDescending(x => x.Score!.Score)
            .ThenByDescending(x => x.Candidate.PublishedAt)
            .ThenBy(x => x.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static VideoIdentityMatch Coverage(ReleaseInfo release, VideoUnit unit)
    {
        if (release.SeasonNumber != unit.SeasonNumber)
        {
            return VideoIdentityMatch.WrongSeason;
        }

        if (release.IsSeasonPack)
        {
            return VideoIdentityMatch.ContainsTarget;
        }

        return release.EpisodeStart is int start && release.EpisodeEnd is int end && unit.EpisodeNumber >= start && unit.EpisodeNumber <= end
            ? VideoIdentityMatch.Matches
            : VideoIdentityMatch.WrongEpisode;
    }

    private static bool TitleMatches(string requested, string candidate)
    {
        var wanted = SignificantWords(requested);
        if (wanted.Count == 0)
        {
            return false;
        }

        var actual = SignificantWords(candidate);
        return wanted.All(actual.Contains);
    }

    private static HashSet<string> SignificantWords(string value)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "der", "die", "das", "und"
        };
        return value.Split(
                [' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.ToLowerInvariant())
            .Where(x => x.Length > 1 && !ignored.Contains(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FailureMessage(
        IndexerAnimeSearchResult search,
        MediaAcquisitionKind kind) =>
        search.Releases.Count == 0
            ? search.Warnings.Count > 0
                ? $"No release found ({search.Warnings[0].IndexerName}: {search.Warnings[0].Message})."
                : "No release found on the indexers."
            : kind == MediaAcquisitionKind.Movie
                ? "No suitable Movie release matched the requested title and quality profile."
                : "No suitable TV release matched the requested episode and quality profile.";



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

    public bool HasPlaybackPriority(AcquisitionRequest request) => VideoRequestPayload.Parse(request.PayloadJson)?.HasPlaybackIntent == true;

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
