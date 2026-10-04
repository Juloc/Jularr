using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
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
/// Durable request/search state for Movie and TV. TV structural selection is expressed only with
/// canonical WorkEpisode ids; future monitoring is a property of the same Scope, never a second
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
    int? ActiveEpisodeNumber = null) : ReleaseRequestPayload;

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
public sealed class VideoAcquisitionEngine(
    AppDbContext db,
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    DownloadClientSubmissionService downloads,
    MediaAcquisitionRegistry registry,
    QualityProfileStore profiles,
    ReleaseRequestTracker tracker,
    AcquisitionAccessStore requestStore,
    AcquisitionRequestService requests,
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

        var payload = ReadPayload(request) ?? DefaultPayload(request, target);
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
                        : "Current requested episodes are available. Monitoring future episodes.",
                    ResultUrl: ResultUrl(payload.Title));
            }

            payload = payload with
            {
                ActiveWorkEpisodeId = unit.Id,
                ActiveSeasonNumber = unit.SeasonNumber,
                ActiveEpisodeNumber = unit.EpisodeNumber,
                NextSearchUtc = null
            };
        }

        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "No Usenet indexer is configured.");
        }

        if (!(await downloadClients.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "No download client is configured.");
        }

        var profile = await profiles.ResolveAsync(request.Kind, target.WorkId, cancellationToken);
        var search = request.Kind == MediaAcquisitionKind.Movie
            ? await SearchMovieAsync(payload, cancellationToken)
            : await SearchTvAsync(payload, unit!, cancellationToken);
        var ranked = Rank(request.Kind, payload.Title, unit, search.Releases, profile);

        var releaseKeys = ranked.ToDictionary(
            x => x.Candidate.Identity,
            x => x.Score.Candidate.Release.ReleaseKey,
            StringComparer.OrdinalIgnoreCase);

        var execution = await tracker.ContinueAsync(
            request,
            payload,
            ranked.Select(x => new ReleaseRequestCandidate(
                    x.Candidate.Identity,
                    x.Candidate.Title,
                    x.Candidate.InternalDownloadUri!))
                .ToArray(),
            FailureMessage(search, request.Kind),
            async release =>
            {
                var outcome = await downloads.SubmitAsync(
                    new DownloadSubmissionSpec(
                        OperationKind,
                        request.Kind == MediaAcquisitionKind.Movie ? "Download Movie" : "Download TV",
                        payload.Title,
                        request.RequestedByProfileId,
                        release.DownloadUri,
                        release.Title,
                        request.Kind,
                        MediaTargetKey: unit is null
                            ? $"work:{payload.WorkId:D}"
                            : $"work-episode:{unit.Id:D}"),
                    cancellationToken);

                if (outcome.Accepted && releaseKeys.TryGetValue(release.Identity, out var releaseKey))
                {
                    await RecordGrabbedAsync(request.Kind, payload, releaseKey, cancellationToken);
                }

                return new ReleaseRequestSubmission(outcome.Accepted, outcome.OperationId, outcome.Message);
            },
            cancellationToken);

        return execution with { ResultUrl = ResultUrl(payload.Title) };
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

    public async Task ContinueAfterProblemAsync(
        AcquisitionRequest request,
        string problem,
        CancellationToken cancellationToken)
    {
        var payload = ReadPayload(request);
        if (payload is null)
        {
            await requests.ContinueAsync(request.Id, cancellationToken);
            return;
        }

        await RecordFailedAsync(request.Kind, payload, cancellationToken);
        var next = ReleaseRequestTracker.AfterProblem(payload, problem);
        await requestStore.UpdatePayloadAsync(request.Id, next.Serialize(), cancellationToken);
        await requests.ContinueAsync(request.Id, cancellationToken);
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
                : "Current requested episodes are imported. Monitoring future episodes.",
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

    private static VideoRequestPayload DefaultPayload(AcquisitionRequest request, VideoTarget target) =>
        request.Kind == MediaAcquisitionKind.Movie
            ? new VideoRequestPayload(
                target.WorkId,
                target.Title,
                target.Year,
                VideoRequestScope.WholeWork,
                [],
                MonitorFuture: false)
            : new VideoRequestPayload(
                target.WorkId,
                target.Title,
                target.Year,
                VideoRequestScope.AllCurrentAndFuture,
                [],
                MonitorFuture: true);

    private async Task EnsureMonitoringAsync(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var store = monitoring.For(request.Kind);
        var key = WorkKey(payload.WorkId);
        Dictionary<string, bool> episodeOverrides = new(StringComparer.OrdinalIgnoreCase);
        var monitored = true;

        if (request.Kind == MediaAcquisitionKind.Tv)
        {
            var episodes = await db.WorkEpisodes.AsNoTracking()
                .Where(x => x.WorkId == payload.WorkId)
                .Select(x => new { x.Id, x.SeasonNumber, x.EpisodeNumber, x.AiredAt })
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
                var selected = payload.SelectedEpisodeIds.ToHashSet();
                foreach (var episode in episodes)
                {
                    var explicitSelection = selected.Contains(episode.Id);
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
                SeasonOverrides: [],
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
        var selected = payload.SelectedEpisodeIds.ToHashSet();

        return episodes
            .Where(x => !x.HasFile)
            .Where(x => IsIncludedTvUnit(request, payload, selected, x))
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
        var selected = payload.SelectedEpisodeIds.ToHashSet();
        var missingIncluded = episodes
            .Where(x => !x.HasFile)
            .Where(x => IsIncludedTvUnit(request, payload, selected, x))
            .ToArray();
        var hasMissingDue = missingIncluded.Any(x => x.AiredAt is null || x.AiredAt <= now);

        if (hasMissingDue)
        {
            return new TvContinuation(true, true, null);
        }

        if (!payload.MonitorFuture)
        {
            return new TvContinuation(false, false, null);
        }

        var nextKnown = missingIncluded
            .Where(x => x.AiredAt > now)
            .Select(x => x.AiredAt)
            .Min();
        return new TvContinuation(
            KeepOpen: true,
            HasMissingDue: false,
            NextSearchUtc: nextKnown ?? now.AddHours(24));
    }

    private async Task<IReadOnlyList<VideoUnit>> LoadTvUnitsAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var episodes = await db.WorkEpisodes.AsNoTracking()
            .Where(x => x.WorkId == workId)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .Select(x => new { x.Id, x.SeasonNumber, x.EpisodeNumber, x.AiredAt })
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
                x.SeasonNumber,
                x.EpisodeNumber,
                x.AiredAt,
                playable.Contains(x.Id)))
            .ToArray();
    }

    private static bool IsIncludedTvUnit(
        AcquisitionRequest request,
        VideoRequestPayload payload,
        HashSet<Guid> selected,
        VideoUnit unit) =>
        payload.Scope switch
        {
            VideoRequestScope.AllCurrentAndFuture => true,
            VideoRequestScope.FutureOnly =>
                unit.AiredAt is not null && unit.AiredAt > request.CreatedAt,
            VideoRequestScope.Custom =>
                selected.Contains(unit.Id)
                || (payload.MonitorFuture
                    && unit.AiredAt is not null
                    && unit.AiredAt > request.CreatedAt),
            _ => false
        };

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

    private IReadOnlyList<RankedVideoRelease> Rank(
        MediaAcquisitionKind kind,
        string title,
        VideoUnit? unit,
        IReadOnlyList<ProwlarrReleaseCandidate> candidates,
        QualityProfile profile)
    {
        var parser = registry.ParserFor(kind);
        var ranked = new List<RankedVideoRelease>();
        foreach (var candidate in candidates)
        {
            if (candidate.InternalDownloadUri is null
                || candidate.Protocol is not null
                   && !candidate.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
                || !parser.TryParse(candidate.Title, out var parsed)
                || !TitleMatches(title, parsed.SeriesTitle))
            {
                continue;
            }

            if (unit is not null && !Covers(parsed, unit))
            {
                continue;
            }

            var score = ReleaseScorer.Score(
                profile,
                new ReleaseCandidate(parsed, candidate.SizeBytes, candidate.Indexer, candidate.Identity));
            if (score.Accepted)
            {
                ranked.Add(new RankedVideoRelease(candidate, score));
            }
        }

        return ranked
            .OrderBy(x => x.Score.QualityRank)
            .ThenByDescending(x => x.Score.Score)
            .ThenByDescending(x => x.Candidate.PublishedAt)
            .ThenBy(x => x.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Covers(ReleaseInfo release, VideoUnit unit)
    {
        if (release.SeasonNumber != unit.SeasonNumber)
        {
            return false;
        }

        if (release.IsSeasonPack)
        {
            return true;
        }

        return release.EpisodeStart is int start
               && release.EpisodeEnd is int end
               && unit.EpisodeNumber >= start
               && unit.EpisodeNumber <= end;
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

    private sealed record VideoUnit(
        Guid Id,
        int SeasonNumber,
        int EpisodeNumber,
        DateTime? AiredAt,
        bool HasFile);

    private sealed record RankedVideoRelease(
        ProwlarrReleaseCandidate Candidate,
        ReleaseScoreResult Score);

    private sealed record TvContinuation(
        bool KeepOpen,
        bool HasMissingDue,
        DateTime? NextSearchUtc);
}

public abstract class VideoWantedRequestHandler(
    VideoAcquisitionEngine engine) : IWantedRequestHandler
{
    public abstract MediaAcquisitionKind Kind { get; }

    public bool IsSearchDue(AcquisitionRequest request, DateTime nowUtc)
    {
        var payload = VideoAcquisitionEngine.ReadPayload(request);
        return payload is null || ReleaseRequestTracker.IsSearchDue(payload, nowUtc);
    }

    public Task ContinueAfterProblemAsync(
        AcquisitionRequest request,
        string problem,
        CancellationToken cancellationToken) =>
        engine.ContinueAfterProblemAsync(request, problem, cancellationToken);

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
    VideoAcquisitionEngine engine) : VideoWantedRequestHandler(engine)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;
}

public sealed class TvWantedRequestHandler(
    VideoAcquisitionEngine engine) : VideoWantedRequestHandler(engine)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Tv;
}
