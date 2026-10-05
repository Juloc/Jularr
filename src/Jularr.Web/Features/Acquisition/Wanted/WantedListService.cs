using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Library;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Wanted;

/// <summary>
/// Reads everything Jularr still needs into <see cref="WantedItem"/>s for Admin → Wanted: the
/// approved requests of every media type (their state is the durable request row) and the monitored
/// anime episodes the monitoring engine found missing. Read only; searching and retrying go
/// through <see cref="AcquisitionRequestService"/> and the anime acquisition scheduler.
/// </summary>
public sealed class WantedListService(
    AcquisitionAccessStore requests,
    AnimeMonitoringStore monitoring,
    AnimeQualityProfileStore qualityProfiles,
    AppDbContext db,
    VideoRequestWorkResolver videoWorks,
    IEnumerable<IAcquisitionRequestExecutor> executors,
    TimeProvider clock)
{
    /// <summary>The most requests one read looks at (the request store caps a page anyway).</summary>
    public const int RequestLimit = 5000;

    private static readonly JsonSerializerOptions PayloadOptions = JsonSerializerOptions.Web;

    public async Task<IReadOnlyList<WantedItem>> LoadAsync(CancellationToken cancellationToken)
    {
        var profiles = await qualityProfiles.LoadAsync(cancellationToken);
        var searchable = executors.Select(executor => executor.Kind).ToHashSet();

        var items = new List<WantedItem>();
        var wanted = new Dictionary<Guid, AcquisitionRequest>();
        foreach (var request in await requests.ListAllAsync(RequestLimit, cancellationToken))
        {
            if (FromRequest(request, searchable.Contains(request.Kind), profiles) is { } item)
            {
                items.Add(item);
                wanted[request.Id] = request;
            }
        }

        var rows = await LoadVideoRowsAsync(items, wanted, profiles, cancellationToken);
        rows.AddRange(await LoadMonitoredAsync(profiles, cancellationToken));
        return rows;
    }

    /// <summary>
    /// Movie and TV requests refer to their canonical Work: a Movie is one row, a Series becomes one row per season
    /// that still misses aired episodes the request covers (from WorkSeason/WorkEpisode and the files of the Work), so
    /// no Movie or TV row depends on anime keys. The reads are set-based: one identity query, one episode query and one
    /// file query for all video requests together.
    /// </summary>
    private async Task<List<WantedItem>> LoadVideoRowsAsync(
        List<WantedItem> items,
        Dictionary<Guid, AcquisitionRequest> wanted,
        QualityProfileState profiles,
        CancellationToken cancellationToken)
    {
        var video = items.Where(item => item.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv).ToList();
        if (video.Count == 0)
        {
            return items;
        }

        var works = await videoWorks.ResolveAsync(video.Select(item => wanted[item.RequestId!.Value]), cancellationToken);
        var tvWorks = video.Where(item => item.Kind == MediaAcquisitionKind.Tv && works.ContainsKey(item.RequestId!.Value)).Select(item => works[item.RequestId!.Value].WorkId).Distinct().ToArray();
        var episodes = tvWorks.Length == 0
            ? []
            : await db.WorkEpisodes.AsNoTracking()
                .Where(episode => tvWorks.Contains(episode.WorkId))
                .Select(episode => new { episode.Id, episode.WorkId, episode.SeasonId, episode.SeasonNumber, episode.EpisodeNumber, episode.AiredAt })
                .ToListAsync(cancellationToken);
        var withFiles = tvWorks.Length == 0
            ? []
            : (await db.MediaAssets.AsNoTracking()
                    .Where(asset => tvWorks.Contains(asset.WorkId) && asset.WorkEpisodeId != null && asset.Kind == MediaAssetKind.Video && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id))
                    .Select(asset => asset.WorkEpisodeId!.Value)
                    .Distinct()
                    .ToListAsync(cancellationToken))
                .ToHashSet();

        var now = clock.GetUtcNow().UtcDateTime;
        var rows = new List<WantedItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
            {
                rows.Add(item);
                continue;
            }

            var request = wanted[item.RequestId!.Value];
            if (!works.TryGetValue(request.Id, out var work))
            {
                rows.Add(item with { DetailUrl = null });
                continue;
            }

            var workId = work.WorkId;
            var selection = VideoRequestSelection.For(request, workId);
            var profileId = profiles.ResolveProfileId(item.Kind, workId);
            var videoRow = item with
            {
                WorkId = workId,
                DetailUrl = VideoWorkLinks.DetailPath(item.Kind, workId),
                ProfileId = profileId,
                ProfileName = profileId is null ? null : ProfileName(profiles, profileId),
                CanSearch = item.CanSearch && selection.Payload.Monitored
            };
            if (item.Kind == MediaAcquisitionKind.Movie)
            {
                rows.Add(videoRow);
                continue;
            }

            var payload = selection.Payload;
            var missing = episodes
                .Where(episode => episode.WorkId == workId && !withFiles.Contains(episode.Id) && (episode.AiredAt is null || episode.AiredAt <= now) && selection.Includes(episode.Id, episode.SeasonId, episode.AiredAt))
                .GroupBy(episode => episode.SeasonNumber)
                .OrderBy(season => season.Key)
                .ToArray();
            if (missing.Length == 0)
            {
                rows.Add(videoRow);
                continue;
            }

            // Only the season holding the episode in flight carries the request's search/download state; the rest queue behind it.
            var inFlight = item.Status is WantedStatus.Searching or WantedStatus.Downloading or WantedStatus.Importing;
            foreach (var season in missing)
            {
                rows.Add(videoRow with
                {
                    Id = $"{item.Id}:s{season.Key}",
                    Status = inFlight && payload.ActiveSeasonNumber != season.Key ? WantedStatus.Requested : item.Status,
                    Season = season.Key,
                    Scope = RequestScope.Episodes,
                    UnitId = season.OrderBy(episode => episode.EpisodeNumber).First().Id,
                    Selection = RequestSelectionText.FormatEpisodes(season.Select(episode => new RequestEpisode(season.Key, episode.EpisodeNumber)))
                });
            }
        }

        return rows;
    }

    private async Task<IReadOnlyList<WantedItem>> LoadMonitoredAsync(
        QualityProfileState profiles,
        CancellationToken cancellationToken)
    {
        var state = await monitoring.LoadAsync(cancellationToken);
        if (state.Wanted.Count == 0)
        {
            return [];
        }

        var keys = state.Wanted.Values
            .Select(wanted => wanted.Key.AnimeKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var anime = await db.Anime
            .AsNoTracking()
            .Where(item => keys.Contains(item.Key))
            .Select(item => new { item.Id, item.Key, item.Title })
            .ToDictionaryAsync(item => item.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var ids = anime.Values.Select(item => item.Id).ToArray();
        var covers = ids.Length == 0
            ? new Dictionary<Guid, string?>()
            : await db.AnimeMetadata
                .AsNoTracking()
                .Where(metadata => ids.Contains(metadata.AnimeId))
                .Select(metadata => new { metadata.AnimeId, metadata.CoverImageUrl })
                .ToDictionaryAsync(metadata => metadata.AnimeId, metadata => metadata.CoverImageUrl, cancellationToken);

        var items = new List<WantedItem>(state.Wanted.Count);
        foreach (var wanted in state.Wanted.Values)
        {
            state.Attempts.TryGetValue(wanted.Key.ToString(), out var attempt);
            var known = anime.GetValueOrDefault(wanted.Key.AnimeKey);
            var profileId = profiles.ResolveProfileId(MediaAcquisitionKind.Anime, known?.Id)
                ?? AnimeQualityProfiles.DefaultAnime1080pId;
            items.Add(FromUnit(
                wanted,
                attempt,
                known?.Title,
                known?.Id,
                known is null ? null : AnimeArtworkStore.ResolvePosterUrl(known.Id, covers.GetValueOrDefault(known.Id)),
                profileId,
                ProfileName(profiles, profileId)));
        }

        return items;
    }

    /// <summary>
    /// A request as a wanted row, or null when it is not part of the worklist: still waiting for the
    /// owner, finished or rejected. Requests of a media type without an executor have no automatic
    /// search; the owner adds those by hand.
    /// </summary>
    public static WantedItem? FromRequest(AcquisitionRequest request, bool hasExecutor, QualityProfileState profiles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profiles);

        var state = ReadSearchState(request);
        if (AdminWantedQuery.StatusOfRequest(request.Status, state.Searches) is not { } status)
        {
            return null;
        }

        var isAnime = request.Kind == MediaAcquisitionKind.Anime;
        var options = request.Options;
        var languages = isAnime
            ? options.AudioLanguage is { } audio ? [audio] : []
            : (state.PreferredLanguages ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        var profileId = isAnime
            ? options.QualityProfileId ?? profiles.DefaultProfileIdFor(MediaAcquisitionKind.Anime)
            : null;

        // A request that never searched has no last search; otherwise its last change is its last search.
        var searched = request.Status != AcquisitionRequestStatus.Approved || state.Searches > 0;
        var updated = AsUtc(request.UpdatedAt);
        return new WantedItem(
            $"r:{request.Id:N}",
            WantedSource.Request,
            request.Kind,
            request.Title,
            status,
            AsUtc(request.CreatedAt))
        {
            RequestId = request.Id,
            Volume = state.RequestedVolume,
            ChapterStart = state.RequestedChapterStart,
            ChapterEnd = state.RequestedChapterEnd,
            Scope = isAnime ? options.Scope : RequestScope.WholeSeries,
            Selection = isAnime ? SelectionText(options) : null,
            Languages = languages,
            ProfileId = profileId,
            ProfileName = profileId is null ? null : ProfileName(profiles, profileId),
            LastSearchUtc = searched ? updated : null,
            NextSearchUtc = state.NextSearchUtc is { } next ? AsUtc(next) : null,
            Note = string.IsNullOrWhiteSpace(request.StatusMessage) ? null : request.StatusMessage,
            CoverUrl = request.CoverImageUrl,
            DetailUrl = LocalUrl(request.ResultUrl),
            CanSearch = hasExecutor && request.Status is AcquisitionRequestStatus.Approved or AcquisitionRequestStatus.Failed
        };
    }

    /// <summary>A monitored unit the engine found missing (or below the cutoff) as a wanted row.</summary>
    public static WantedItem FromUnit(
        WantedUnit wanted,
        AcquisitionAttempt? attempt,
        string? title,
        Guid? animeId,
        string? coverUrl,
        string? profileId,
        string? profileName)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        var key = wanted.Key;
        var failed = attempt?.Status == AcquisitionAttemptStatus.Failed;
        return new WantedItem(
            $"m:{key}",
            WantedSource.Monitored,
            MediaAcquisitionKind.Anime,
            title ?? key.AnimeKey,
            AdminWantedQuery.StatusOfAttempt(attempt?.Status),
            wanted.BecameWantedAtUtc.UtcDateTime)
        {
            AnimeKey = key.AnimeKey,
            AnimeId = animeId,
            Season = key.Granularity == MonitoringGranularity.Item ? null : key.SeasonNumber,
            Episode = key.Granularity == MonitoringGranularity.Episode ? key.EpisodeNumber : null,
            IsUpgrade = wanted.Reason == WantedReason.CutoffUnmet,
            ProfileId = profileId,
            ProfileName = profileName,
            LastSearchUtc = attempt?.LastAttemptAtUtc?.UtcDateTime,
            NextSearchUtc = failed ? attempt?.NextRetryAtUtc?.UtcDateTime : null,
            Failures = attempt?.FailureCount ?? 0,
            Attempt = attempt?.Status ?? AcquisitionAttemptStatus.None,
            CoverUrl = coverUrl,
            DetailUrl = animeId is { } id ? $"/Library/Anime/{id:D}" : null,
            CanSearch = animeId is not null
        };
    }

    private static string? ProfileName(QualityProfileState profiles, string profileId) =>
        profiles.Profiles.FirstOrDefault(profile => profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? profileId;

    private static string? SelectionText(AcquisitionRequestOptions options) => options.Scope switch
    {
        RequestScope.Seasons => RequestSelectionText.FormatSeasons(options.Seasons),
        RequestScope.Episodes => RequestSelectionText.FormatEpisodes(options.Episodes),
        _ => null
    };

    /// <summary>Only an address on this server; a request can carry any result address.</summary>
    private static string? LocalUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : null;

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// The Usenet search state of a Manga, Light Novel or Book request and what it asked for; the
    /// payload keeps these fields flat (see <see cref="ReleaseRequestPayload"/>). Other kinds have
    /// none of them, so an unreadable or foreign payload reads as "never searched".
    /// </summary>
    private static RequestSearchState ReadSearchState(AcquisitionRequest request)
    {
        if (request.Kind == MediaAcquisitionKind.Anime || string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            return new RequestSearchState();
        }

        try
        {
            return JsonSerializer.Deserialize<RequestSearchState>(request.PayloadJson, PayloadOptions)
                ?? new RequestSearchState();
        }
        catch (JsonException)
        {
            return new RequestSearchState();
        }
    }

    private sealed record RequestSearchState
    {
        public int Searches { get; init; }

        public DateTime? NextSearchUtc { get; init; }

        public int? RequestedVolume { get; init; }

        public double? RequestedChapterStart { get; init; }

        public double? RequestedChapterEnd { get; init; }

        public IReadOnlyList<string>? PreferredLanguages { get; init; }
    }
}
