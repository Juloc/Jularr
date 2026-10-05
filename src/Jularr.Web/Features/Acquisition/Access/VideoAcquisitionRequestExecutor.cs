using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
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
    /// The payload a request carries; a request without a readable one covers the whole Movie, or the whole Series with future
    /// episodes. The executor and the detail pages both read a request through this, so they agree on its scope.
    /// </summary>
    public static VideoRequestPayload Of(AcquisitionRequest request, Guid workId, string title, int? year) =>
        VideoAcquisitionEngine.ReadPayload(request)
        ?? (request.Kind == MediaAcquisitionKind.Movie
            ? new VideoRequestPayload(workId, title, year, VideoRequestScope.WholeWork, [], MonitorFuture: false)
            : new VideoRequestPayload(workId, title, year, VideoRequestScope.AllCurrentAndFuture, [], MonitorFuture: true));
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

    public bool Includes(Guid episodeId, Guid? seasonId, DateTime? airedAt) =>
        payload.Scope switch
        {
            VideoRequestScope.AllCurrentAndFuture => true,
            VideoRequestScope.FutureOnly => airedAt is not null && airedAt > requestCreatedAt,
            VideoRequestScope.Custom =>
                selectedEpisodes.Contains(episodeId)
                || seasonId is { } season && selectedSeasons.Contains(season)
                || (payload.MonitorFuture && airedAt is not null && airedAt > requestCreatedAt),
            _ => false
        };
}

/// <summary>Owns the per-kind generic monitoring stores without registering two ambiguous MonitoringStore instances.</summary>
public sealed class VideoAcquisitionMonitoringStores
{
    private readonly MonitoringStore movie;
    private readonly MonitoringStore tv;

    public VideoAcquisitionMonitoringStores(string dataRoot)
    {
        movie = new MonitoringStore(dataRoot, MediaAcquisitionKind.Movie);
        tv = new MonitoringStore(dataRoot, MediaAcquisitionKind.Tv);
    }

    public MonitoringStore For(MediaAcquisitionKind kind) => kind switch
    {
        MediaAcquisitionKind.Movie => movie,
        MediaAcquisitionKind.Tv => tv,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
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
    VideoAcquisitionMonitoringStores monitoring,
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

        await EnsureMonitoringAsync(request, payload, cancellationToken);

        VideoUnit? unit = null;
        if (request.Kind == MediaAcquisitionKind.Movie)
        {
            if (await HasMovieFileAsync(target.WorkId, cancellationToken))
            {
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Completed,
                    "Movie is already available in the library.",
                    ResultUrl: ResultUrl(payload.Title));
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
                    return new AcquisitionExecution(
                        AcquisitionRequestStatus.Completed,
                        "All requested TV episodes are available.",
                        ResultUrl: ResultUrl(payload.Title));
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
                    ResultUrl: ResultUrl(payload.Title));
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
        var releaseKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in releases)
        {
            releaseKeys.TryAdd(release.Candidate.Identity, release.Score!.Candidate.Release.ReleaseKey);
        }

        var execution = await tracker.ContinueAsync(
            request,
            payload,
            releases.Select(x => new ReleaseRequestCandidate(x.Candidate.Identity, x.Candidate.Title, x.Candidate.InternalDownloadUri!)).ToArray(),
            noReleaseReason,
            async release =>
            {
                progress?.SubmitStarted = true;
                var outcome = await downloads.SubmitAsync(
                    new DownloadSubmissionSpec(
                        OperationKind,
                        request.Kind == MediaAcquisitionKind.Movie ? "Download Movie" : "Download TV",
                        payload.Title,
                        request.RequestedByProfileId,
                        release.DownloadUri,
                        release.Title,
                        request.Kind,
                        MediaTargetKey: unit is null ? $"work:{payload.WorkId:D}" : $"work-episode:{unit.Id:D}"),
                    cancellationToken);

                if (outcome.Accepted && progress is not null)
                {
                    progress.Accepted = true;
                    progress.OperationId = outcome.OperationId;
                }

                if (outcome.Accepted && releaseKeys.TryGetValue(release.Identity, out var releaseKey))
                {
                    await RecordGrabbedAsync(request.Kind, payload, releaseKey, cancellationToken);
                }

                return new ReleaseRequestSubmission(outcome.Accepted, outcome.OperationId, outcome.Message);
            },
            cancellationToken);

        return execution with { ResultUrl = ResultUrl(payload.Title) };
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

    public static VideoRequestPayload? ReadPayload(AcquisitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VideoRequestPayload>(request.PayloadJson, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task PrepareAfterProblemAsync(
        AcquisitionRequest request,
        string problem,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload(request);
        if (payload is null)
        {
            return;
        }

        await RecordFailedAsync(request.Kind, payload, cancellationToken);
        var next = ReleaseRequestTracker.AfterProblem(payload, problem);
        await requestStore.UpdatePayloadAsync(request.Id, next.Serialize(), cancellationToken);
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
        var payload = ReadPayload(request);
        if (payload is null)
        {
            return false;
        }

        await ClearAttemptAsync(request.Kind, payload, cancellationToken);
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
            ResultUrl(reset.Title),
            decidedByProfileId: null,
            cancellationToken);
        return true;
    }

    private async Task<VideoTarget?> ResolveTargetAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        var workType = request.Kind == MediaAcquisitionKind.Movie
            ? WorkMediaType.Movie
            : WorkMediaType.Series;
        var provider = request.Provider.Trim().ToLowerInvariant();
        var externalId = request.ExternalId.Trim();

        return await (
            from identity in db.WorkExternalIdentities.AsNoTracking()
            join work in db.Works.AsNoTracking() on identity.WorkId equals work.Id
            where identity.MediaType == workType
                  && identity.Provider == provider
                  && identity.ExternalId == externalId
                  && work.MediaType == workType
            select new VideoTarget(work.Id, work.CanonicalTitle, work.Year))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task EnsureMonitoringAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var store = monitoring.For(request.Kind);
        var key = WorkKey(payload.WorkId);
        Dictionary<int, bool> seasonOverrides = [];
        Dictionary<string, bool> episodeOverrides = new(StringComparer.OrdinalIgnoreCase);
        var monitored = true;

        if (request.Kind == MediaAcquisitionKind.Tv)
        {
            var episodes = await db.WorkEpisodes.AsNoTracking()
                .Where(x => x.WorkId == payload.WorkId)
                .Select(x => new { x.Id, x.SeasonId, x.SeasonNumber, x.EpisodeNumber, x.AiredAt })
                .ToListAsync(cancellationToken);

            if (payload.Scope == VideoRequestScope.FutureOnly)
            {
                foreach (var episode in episodes.Where(x => x.AiredAt is not null && x.AiredAt <= request.CreatedAt))
                {
                    episodeOverrides[MonitoringEngine.EpisodeOverrideKey(episode.SeasonNumber, episode.EpisodeNumber)] = false;
                }
            }
            else if (payload.Scope == VideoRequestScope.Custom)
            {
                monitored = payload.MonitorFuture;
                var selectedEpisodes = payload.SelectedEpisodeIds.ToHashSet();
                var selectedSeasonIds = (payload.SelectedSeasonIds ?? []).ToHashSet();
                HashSet<int> selectedSeasonNumbers = selectedSeasonIds.Count == 0
                    ? []
                    : (await db.WorkSeasons.AsNoTracking()
                        .Where(x => x.WorkId == payload.WorkId && selectedSeasonIds.Contains(x.Id))
                        .Select(x => x.SeasonNumber)
                        .ToListAsync(cancellationToken))
                    .ToHashSet();
                foreach (var seasonNumber in selectedSeasonNumbers)
                {
                    seasonOverrides[seasonNumber] = true;
                }

                foreach (var episode in episodes)
                {
                    var explicitSelection = selectedEpisodes.Contains(episode.Id) || selectedSeasonNumbers.Contains(episode.SeasonNumber);
                    var wasCurrentAtRequest = episode.AiredAt is null || episode.AiredAt <= request.CreatedAt;
                    if (explicitSelection)
                    {
                        episodeOverrides[MonitoringEngine.EpisodeOverrideKey(episode.SeasonNumber, episode.EpisodeNumber)] = true;
                    }
                    else if (payload.MonitorFuture && wasCurrentAtRequest)
                    {
                        episodeOverrides[MonitoringEngine.EpisodeOverrideKey(episode.SeasonNumber, episode.EpisodeNumber)] = false;
                    }
                }
            }
        }

        await store.UpdateAsync(state =>
        {
            var settings = new Dictionary<string, MonitorSettings>(state.Anime, StringComparer.OrdinalIgnoreCase);
            settings.TryGetValue(key, out var previous);
            settings[key] = new MonitorSettings(
                key,
                monitored,
                SearchOnAdd: true,
                SeasonOverrides: seasonOverrides,
                EpisodeOverrides: episodeOverrides,
                IndexerIds: previous?.IndexerIds,
                TagIds: previous?.TagIds,
                TargetRootId: previous?.TargetRootId);
            return state with { Anime = settings };
        }, cancellationToken);
    }

    private async Task<bool> HasMovieFileAsync(Guid workId, CancellationToken cancellationToken) =>
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
            .OrderBy(x => x.SeasonNumber)
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

    private async Task RecordGrabbedAsync(
        MediaAcquisitionKind kind,
        VideoRequestPayload payload,
        string releaseKey,
        CancellationToken cancellationToken)
    {
        if (ActiveKey(kind, payload) is not { } key)
        {
            return;
        }

        await monitoring.For(kind).UpdateAsync(
            state => MonitoringEngine.MarkGrabbed(state, key, releaseKey, clock.GetUtcNow()),
            cancellationToken);
    }

    private async Task RecordFailedAsync(
        MediaAcquisitionKind kind,
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        if (ActiveKey(kind, payload) is not { } key)
        {
            return;
        }

        await monitoring.For(kind).UpdateAsync(
            state => MonitoringEngine.MarkFailed(state, key, null, clock.GetUtcNow()),
            cancellationToken);
    }

    private async Task ClearAttemptAsync(
        MediaAcquisitionKind kind,
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        if (ActiveKey(kind, payload) is not { } key)
        {
            return;
        }

        await monitoring.For(kind).UpdateAsync(
            state => MonitoringEngine.ClearAttempt(state, key, clock.GetUtcNow(), "Import completed."),
            cancellationToken);
    }

    private static MonitoredUnitKey? ActiveKey(MediaAcquisitionKind kind, VideoRequestPayload payload) =>
        kind == MediaAcquisitionKind.Movie
            ? MonitoredUnitKey.ForItem(WorkKey(payload.WorkId))
            : payload.ActiveSeasonNumber is int season && payload.ActiveEpisodeNumber is int episode
                ? MonitoredUnitKey.ForEpisode(WorkKey(payload.WorkId), season, episode)
                : null;

    private static string WorkKey(Guid workId) => $"work:{workId:D}";

    private static string ResultUrl(string title) =>
        $"/Search?q={Uri.EscapeDataString(title)}";

    private sealed record VideoTarget(Guid WorkId, string Title, int? Year);

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
        var payload = VideoAcquisitionEngine.ReadPayload(request);
        return payload is null || ReleaseRequestTracker.IsSearchDue(payload, nowUtc);
    }

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
