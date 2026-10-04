using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Video;

/// <summary>
/// Durable request target plus the shared release-request state for Movie and TV. The scope fields stay
/// at the JSON root so <see cref="AcquisitionRequest.Options"/> can read the same payload without a
/// second TV-specific request model.
/// </summary>
public sealed record VideoRequestPayload : ReleaseRequestPayload
{
    public Guid WorkId { get; init; }

    public string Title { get; init; } = "";

    public int? Year { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public RequestScope Scope { get; init; } = RequestScope.WholeSeries;

    public IReadOnlyList<int> Seasons { get; init; } = [];

    public IReadOnlyList<RequestEpisode> Episodes { get; init; } = [];

    public bool MonitorFuture { get; init; }

    public string? AudioLanguage { get; init; }

    public string? SubtitleLanguage { get; init; }

    public string? QualityProfileId { get; init; }

    public bool HasFutureIntent =>
        Scope is RequestScope.WholeSeries or RequestScope.FutureOnly
        || (Scope == RequestScope.Custom && MonitorFuture);

    public AcquisitionRequestOptions ToOptions() =>
        new()
        {
            Scope = Scope,
            Seasons = Seasons,
            Episodes = Episodes,
            MonitorFuture = MonitorFuture,
            AudioLanguage = AudioLanguage,
            SubtitleLanguage = SubtitleLanguage,
            QualityProfileId = QualityProfileId
        };
}

/// <summary>One stable pair of per-kind monitoring stores. The shared monitoring engine remains the owner.</summary>
public sealed class VideoMonitoringStores
{
    private readonly MonitoringStore movie;
    private readonly MonitoringStore tv;

    public VideoMonitoringStores() : this("/data")
    {
    }

    public VideoMonitoringStores(string dataRoot)
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
/// Shared automatic acquisition for canonical Movie/TV Works. It reuses the generic provider identity,
/// monitoring, release parsing/scoring, indexer, download-client and release-request lifecycle; no
/// Movie/TV background loop exists here.
/// </summary>
public sealed class VideoAcquisitionEngine(
    AppDbContext db,
    MediaAcquisitionRegistry registry,
    QualityProfileStore qualityProfiles,
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    DownloadClientSubmissionService downloads,
    ReleaseRequestTracker tracker,
    VideoMonitoringStores monitoringStores,
    TimeProvider clock)
{
    public const string OperationKind = "video-usenet-download";
    public static readonly TimeSpan FuturePollInterval = TimeSpan.FromHours(6);
    private static readonly int[] MovieCategories = [2000];
    private static readonly int[] TvCategories = [5000];
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "Video acquisition only supports Movie and TV.");
        }

        var workType = request.Kind == MediaAcquisitionKind.Movie
            ? WorkMediaType.Movie
            : WorkMediaType.Series;
        var work = await ResolveWorkAsync(request, workType, cancellationToken);
        if (work is null)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "The canonical Work for this provider identity no longer exists.");
        }

        var payload = await ReadPayloadAsync(request, work, cancellationToken);
        if (payload.WorkId != work.Id)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "The request payload points at a different canonical Work and requires review.");
        }

        if (payload.QualityProfileId is { Length: > 0 } profileId)
        {
            await qualityProfiles.AssignWorkAsync(work.Id, profileId, cancellationToken);
        }

        var profile = await qualityProfiles.ResolveAsync(request.Kind, work.Id, cancellationToken);
        var store = monitoringStores.For(request.Kind);
        var inventory = await BuildInventoryAsync(request.Kind, work.Id, cancellationToken);
        var now = clock.GetUtcNow();
        var state = await ApplyScopeAsync(store, request.Kind, work.Id, payload, inventory, now, cancellationToken);
        state = MonitoringEngine.RefreshWantedForAnime(
            state,
            WorkKey(work.Id),
            inventory,
            profile,
            now);
        await store.SaveAsync(state, cancellationToken);

        var wanted = state.Wanted.Values
            .Where(x => x.Key.AnimeKey.Equals(WorkKey(work.Id), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Key.SeasonNumber)
            .ThenBy(x => x.Key.EpisodeNumber)
            .ToArray();

        if (wanted.Length == 0)
        {
            if (request.Kind == MediaAcquisitionKind.Tv && payload.HasFutureIntent)
            {
                var monitoring = payload with
                {
                    TriedReleases = [],
                    Searches = 0,
                    NextSearchUtc = now.UtcDateTime + FuturePollInterval,
                    LastProblem = null
                };
                await tracker.SaveAsync(request, monitoring, cancellationToken);
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Approved,
                    "Monitoring future episodes.",
                    ResultUrl: "/Library");
            }

            await tracker.SaveAsync(
                request,
                payload with { NextSearchUtc = null, LastProblem = null },
                cancellationToken);
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Completed,
                "The requested video scope is already available.",
                ResultUrl: "/Library");
        }

        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "No Usenet indexer is configured.");
        }

        if (!(await downloadClients.LoadAllAsync(cancellationToken))
            .Any(x => x.Enabled && x.Type == DownloadClientType.Sabnzbd))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "SABnzbd is not configured.");
        }

        var accepted = await SearchAcceptedAsync(request.Kind, payload, wanted, state, profile, cancellationToken);
        var reason = accepted.Count == 0
            ? "No release matched the canonical title, requested scope and quality profile."
            : "No untried matching release is available.";

        return await tracker.ContinueAsync(
            request,
            payload,
            accepted,
            reason,
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
                        MediaTargetKey: WorkKey(work.Id)),
                    cancellationToken);
                return new ReleaseRequestSubmission(
                    outcome.Accepted,
                    outcome.OperationId,
                    outcome.Message);
            },
            cancellationToken);
    }

    public async Task<AcquisitionExecution> ContinueAfterSuccessfulTvImportAsync(
        AcquisitionRequest request,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var workType = WorkMediaType.Series;
        var work = await ResolveWorkAsync(request, workType, cancellationToken);
        if (work is null)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "The canonical TV Work no longer exists after import.",
                ResultUrl: "/Library");
        }

        var payload = await ReadPayloadAsync(request, work, cancellationToken);
        await tracker.SaveAsync(
            request,
            payload with
            {
                TriedReleases = [],
                Searches = 0,
                NextSearchUtc = nowUtc,
                LastProblem = null
            },
            cancellationToken);
        return new AcquisitionExecution(
            AcquisitionRequestStatus.Approved,
            payload.HasFutureIntent
                ? "Imported requested episodes; monitoring and checking the remaining scope."
                : "Imported requested episodes; checking the remaining requested scope.",
            ResultUrl: "/Library");
    }

    public static VideoRequestPayload ReadPersistedPayload(AcquisitionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            try
            {
                return JsonSerializer.Deserialize<VideoRequestPayload>(
                           request.PayloadJson,
                           PayloadJsonOptions)
                       ?? new VideoRequestPayload();
            }
            catch (JsonException)
            {
            }
        }

        return new VideoRequestPayload();
    }

    private async Task<Work?> ResolveWorkAsync(
        AcquisitionRequest request,
        WorkMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var provider = request.Provider.Trim().ToLowerInvariant();
        var externalId = request.ExternalId.Trim();
        return await (
                from identity in db.WorkExternalIdentities.AsNoTracking()
                join work in db.Works.AsNoTracking() on identity.WorkId equals work.Id
                where identity.Provider == provider
                      && identity.ExternalId == externalId
                      && identity.MediaType == mediaType
                      && work.MediaType == mediaType
                select work)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<VideoRequestPayload> ReadPayloadAsync(
        AcquisitionRequest request,
        Work work,
        CancellationToken cancellationToken)
    {
        var persisted = ReadPersistedPayload(request);
        var options = request.Options.Validate();
        var aliases = (await db.WorkTitles
                .AsNoTracking()
                .Where(x => x.WorkId == work.Id)
                .Select(x => x.Value)
                .ToListAsync(cancellationToken))
            .Append(work.CanonicalTitle)
            .Append(request.Title)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        return persisted with
        {
            WorkId = persisted.WorkId == Guid.Empty ? work.Id : persisted.WorkId,
            Title = string.IsNullOrWhiteSpace(persisted.Title) ? work.CanonicalTitle : persisted.Title,
            Year = persisted.Year ?? work.Year,
            Aliases = persisted.Aliases.Count == 0 ? aliases : persisted.Aliases,
            Scope = options.Scope,
            Seasons = options.Seasons,
            Episodes = options.Episodes,
            MonitorFuture = options.MonitorFuture,
            AudioLanguage = options.AudioLanguage,
            SubtitleLanguage = options.SubtitleLanguage,
            QualityProfileId = options.QualityProfileId
        };
    }

    private async Task<IReadOnlyList<MonitoredUnitInventory>> BuildInventoryAsync(
        MediaAcquisitionKind kind,
        Guid workId,
        CancellationToken cancellationToken)
    {
        var fileEpisodeIds = await (
                from asset in db.MediaAssets.AsNoTracking()
                join file in db.StoredFiles.AsNoTracking() on (Guid?)asset.Id equals file.MediaAssetId
                where asset.WorkId == workId && asset.Kind == MediaAssetKind.Video
                select asset.WorkEpisodeId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (kind == MediaAcquisitionKind.Movie)
        {
            return
            [
                new MonitoredUnitInventory(
                    MonitoredUnitKey.ForItem(WorkKey(workId)),
                    null,
                    fileEpisodeIds.Any(x => x is null),
                    null)
            ];
        }

        var withFiles = fileEpisodeIds
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();
        var episodes = await db.WorkEpisodes
            .AsNoTracking()
            .Where(x => x.WorkId == workId && !x.IsSpecial)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.EpisodeNumber)
            .Select(x => new
            {
                x.Id,
                x.SeasonNumber,
                x.EpisodeNumber,
                x.AbsoluteNumber,
                x.AiredAt
            })
            .ToListAsync(cancellationToken);

        return episodes
            .Select(x => new MonitoredUnitInventory(
                MonitoredUnitKey.ForEpisode(
                    WorkKey(workId),
                    x.SeasonNumber,
                    x.EpisodeNumber,
                    x.AbsoluteNumber),
                x.AiredAt.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(x.AiredAt.Value, DateTimeKind.Utc))
                    : null,
                withFiles.Contains(x.Id),
                null))
            .ToArray();
    }

    private static async Task<MonitoringState> ApplyScopeAsync(
        MonitoringStore store,
        MediaAcquisitionKind kind,
        Guid workId,
        VideoRequestPayload payload,
        IReadOnlyList<MonitoredUnitInventory> inventory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = WorkKey(workId);
        return await store.UpdateAsync(
            state =>
            {
                var existing = state.Anime.TryGetValue(key, out var found) ? found : null;
                if (kind == MediaAcquisitionKind.Movie)
                {
                    var movie = existing ?? EmptySettings(key, monitored: true);
                    var map = new Dictionary<string, MonitorSettings>(state.Anime, StringComparer.OrdinalIgnoreCase)
                    {
                        [key] = movie with { Monitored = true, SearchOnAdd = true }
                    };
                    return state with { Anime = map };
                }

                var settings = MergeTvScope(existing, key, payload, inventory, now);
                var anime = new Dictionary<string, MonitorSettings>(state.Anime, StringComparer.OrdinalIgnoreCase)
                {
                    [key] = settings
                };
                return state with { Anime = anime };
            },
            cancellationToken);
    }

    private static MonitorSettings MergeTvScope(
        MonitorSettings? existing,
        string key,
        VideoRequestPayload payload,
        IReadOnlyList<MonitoredUnitInventory> inventory,
        DateTimeOffset now)
    {
        var current = existing ?? EmptySettings(key, monitored: false);
        var seasonOverrides = new Dictionary<int, bool>(current.SeasonOverrides);
        var episodeOverrides = new Dictionary<string, bool>(
            current.EpisodeOverrides,
            StringComparer.OrdinalIgnoreCase);

        bool WasAlreadyMonitored(MonitoredUnitInventory unit) =>
            existing is not null
            && MonitoringEngine.IsMonitored(
                MonitoringState.Empty() with
                {
                    Anime = new Dictionary<string, MonitorSettings>(StringComparer.OrdinalIgnoreCase)
                    {
                        [key] = existing
                    }
                },
                unit.Key);

        var selectedSeasons = payload.Seasons.ToHashSet();
        var selectedEpisodes = payload.Episodes
            .Select(x => MonitoringEngine.EpisodeOverrideKey(x.Season, x.Number))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var monitorByDefault = payload.Scope switch
        {
            RequestScope.WholeSeries => true,
            RequestScope.FutureOnly => true,
            RequestScope.Custom when payload.MonitorFuture => true,
            _ => current.Monitored
        };

        foreach (var unit in inventory)
        {
            var episodeKey = MonitoringEngine.EpisodeOverrideKey(
                unit.Key.SeasonNumber,
                unit.Key.EpisodeNumber);
            var selected = selectedSeasons.Contains(unit.Key.SeasonNumber)
                           || selectedEpisodes.Contains(episodeKey);
            var future = unit.AirsAtUtc is DateTimeOffset air && air > now;

            switch (payload.Scope)
            {
                case RequestScope.WholeSeries:
                    episodeOverrides.Remove(episodeKey);
                    break;

                case RequestScope.FutureOnly:
                    if (future || WasAlreadyMonitored(unit))
                    {
                        if (future)
                        {
                            episodeOverrides.Remove(episodeKey);
                        }
                    }
                    else
                    {
                        episodeOverrides[episodeKey] = false;
                    }
                    break;

                case RequestScope.Custom:
                    if (selected)
                    {
                        episodeOverrides[episodeKey] = true;
                    }
                    else if (payload.MonitorFuture && future)
                    {
                        episodeOverrides.Remove(episodeKey);
                    }
                    else if (payload.MonitorFuture && !WasAlreadyMonitored(unit))
                    {
                        episodeOverrides[episodeKey] = false;
                    }
                    break;

                case RequestScope.Seasons:
                    if (selectedSeasons.Contains(unit.Key.SeasonNumber))
                    {
                        episodeOverrides[episodeKey] = true;
                    }
                    break;

                case RequestScope.Episodes:
                    if (selectedEpisodes.Contains(episodeKey))
                    {
                        episodeOverrides[episodeKey] = true;
                    }
                    break;
            }
        }

        return current with
        {
            Monitored = monitorByDefault,
            SearchOnAdd = true,
            SeasonOverrides = seasonOverrides,
            EpisodeOverrides = episodeOverrides
        };
    }

    private async Task<IReadOnlyList<ReleaseRequestCandidate>> SearchAcceptedAsync(
        MediaAcquisitionKind kind,
        VideoRequestPayload payload,
        IReadOnlyList<WantedUnit> wanted,
        MonitoringState state,
        QualityProfile profile,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (ReleaseRequestCandidate Candidate, long Rank)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var unit in wanted)
        {
            var queries = BuildQueries(kind, payload, unit.Key);
            var search = await indexers.SearchCategoriesAsync(
                queries,
                _ => kind == MediaAcquisitionKind.Movie ? MovieCategories : TvCategories,
                cancellationToken);

            foreach (var release in search.Releases)
            {
                if (release.InternalDownloadUri is null
                    || !string.Equals(release.Protocol, "usenet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parsed = registry.ParserFor(kind).Parse(release.Title);
                if (!MatchesCanonicalTitle(parsed.SeriesTitle, payload.Aliases)
                    || (kind == MediaAcquisitionKind.Movie
                        && payload.Year is int year
                        && parsed.AirDate is DateOnly releaseDate
                        && releaseDate.Year != year))
                {
                    continue;
                }

                var scored = ReleaseScorer.Score(
                    profile,
                    new ReleaseCandidate(parsed, release.SizeBytes, release.Indexer, release.Identity));
                var decision = MonitoringEngine.EvaluateCandidate(
                    profile,
                    unit,
                    scored,
                    currentFile: null,
                    state);
                if (!decision.Grab)
                {
                    continue;
                }

                var candidate = new ReleaseRequestCandidate(
                    release.Identity,
                    release.Title,
                    release.InternalDownloadUri);
                var rank = (long)scored.QualityRank * 100_000L - scored.Score;
                if (!result.TryGetValue(release.Identity, out var previous) || rank < previous.Rank)
                {
                    result[release.Identity] = (candidate, rank);
                }
            }
        }

        return result.Values
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Candidate)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildQueries(
        MediaAcquisitionKind kind,
        VideoRequestPayload payload,
        MonitoredUnitKey unit)
    {
        var titles = payload.Aliases
            .Prepend(payload.Title)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        if (kind == MediaAcquisitionKind.Movie)
        {
            return titles
                .Select(title => payload.Year is int year
                    ? $"{title} {year.ToString(CultureInfo.InvariantCulture)}"
                    : title)
                .ToArray();
        }

        var suffix = $"S{unit.SeasonNumber:00}E{unit.EpisodeNumber:00}";
        return titles.Select(title => $"{title} {suffix}").ToArray();
    }

    private static bool MatchesCanonicalTitle(
        string? releaseTitle,
        IReadOnlyList<string> aliases)
    {
        var normalized = NormalizeTitle(releaseTitle);
        return normalized.Length > 0
               && aliases.Select(NormalizeTitle)
                   .Where(x => x.Length > 0)
                   .Any(x => x == normalized);
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        var builder = new StringBuilder(title.Length);
        foreach (var rune in title.Trim().Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLowerInvariant());
            }
        }

        return builder.ToString();
    }

    private static MonitorSettings EmptySettings(string key, bool monitored) =>
        new(
            key,
            monitored,
            SearchOnAdd: true,
            new Dictionary<int, bool>(),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    private static string WorkKey(Guid workId) => workId.ToString("D");
}

public sealed class MovieAcquisitionRequestExecutor(VideoAcquisitionEngine engine) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;

    public Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken) =>
        engine.ExecuteAsync(request, cancellationToken);
}

public sealed class TvAcquisitionRequestExecutor(VideoAcquisitionEngine engine) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Tv;

    public Task<AcquisitionExecution> ExecuteAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken) =>
        engine.ExecuteAsync(request, cancellationToken);
}

public abstract class VideoWantedRequestHandler(
    AcquisitionAccessStore store,
    AcquisitionRequestService requests,
    VideoAcquisitionEngine engine)
    : ReleaseRequestWantedHandler(store, requests)
{
    protected VideoAcquisitionEngine Engine { get; } = engine;

    protected override ReleaseRequestPayload ReadPayload(AcquisitionRequest request) =>
        VideoAcquisitionEngine.ReadPersistedPayload(request);
}

public sealed class MovieWantedRequestHandler(
    AcquisitionAccessStore store,
    AcquisitionRequestService requests,
    VideoAcquisitionEngine engine)
    : VideoWantedRequestHandler(store, requests, engine)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Movie;
}

public sealed class TvWantedRequestHandler(
    AcquisitionAccessStore store,
    AcquisitionRequestService requests,
    VideoAcquisitionEngine engine)
    : VideoWantedRequestHandler(store, requests, engine)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Tv;

    public override Task<AcquisitionExecution?> AfterCompletedImportAsync(
        AcquisitionRequest request,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        ContinueAsync(request, nowUtc, cancellationToken);

    private async Task<AcquisitionExecution?> ContinueAsync(
        AcquisitionRequest request,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await Engine.ContinueAfterSuccessfulTvImportAsync(request, nowUtc, cancellationToken);
}
