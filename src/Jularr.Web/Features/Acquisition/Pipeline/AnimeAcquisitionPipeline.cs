using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Pipeline;

public sealed record AnimeSearchCandidate(
    AcquisitionCandidate Release,
    AnimeReleaseScoreResult Score,
    AnimeAutoGrabDecision Decision,
    IReadOnlyList<AnimeEpisodeKey> CoveredEpisodes);

public sealed record AnimeInteractiveSearch(
    AnimeAcquisitionTarget Target,
    AnimeEpisodeKey? Episode,
    ProwlarrAnimeSearchMode Mode,
    IReadOnlyList<AnimeSearchCandidate> Candidates,
    IReadOnlyList<IndexerSearchWarning> Warnings,
    string? Error,
    IReadOnlyList<ReleaseEvaluation<AnimeMatch>>? Evaluations = null);

public sealed record AnimeGrabResult(bool Success, string Message, Guid? OperationId = null);

/// <summary>
/// The one anime acquisition pipeline: refreshes wanted episodes from the library/AniList
/// inventory, searches Prowlarr, scores releases with the assigned quality profile, checks Sonarr
/// ownership and hands the best accepted release to SABnzbd. Every decision is written to the
/// Operation of that search so the owner can see why a release was accepted or rejected.
/// Callers serialize runs through <see cref="AnimeAcquisitionScheduler"/>.
/// </summary>
public sealed class AnimeAcquisitionPipeline(
    AppDbContext db,
    AnimeMonitoringStore monitoring,
    AnimeQualityProfileStore profiles,
    IndexerSearchCoordinator indexers,
    SonarrObservationService observation,
    AcquisitionOwnershipStore ownershipStore,
    AnimeImportStore imports,
    AnimeAcquisitionInventory inventory,
    AnimeMonitoring animeMonitoring,
    AcquisitionHistoryService history,
    ILogger<AnimeAcquisitionPipeline> logger,
    AcquisitionCore core)
{
    public const string SearchOperationKind = "anime-search";
    public const string OperationCategory = "Acquisition";
    public const string LogModule = "Acquisition";
    public const int MaxLoggedDecisions = 25;

    public async Task<AnimeInteractiveSearch?> SearchInteractiveAsync(
        string animeKey,
        int? seasonNumber,
        int? episodeNumber,
        ProwlarrAnimeSearchMode mode,
        CancellationToken cancellationToken)
    {
        var target = await inventory.LoadAsync(animeKey, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var episode = seasonNumber is { } season && episodeNumber is { } number
            ? target.Find(season, number)
              ?? new AnimeAcquisitionEpisode(new AnimeEpisodeKey(animeKey, season, number), null, null, target.Anime.Title, target.AllTitles)
            : null;
        if (mode == ProwlarrAnimeSearchMode.Episode && episode is null)
        {
            return new(target, null, mode, [], [], "Choose an episode to search.");
        }

        if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
        {
            return new(target, episode?.Key, mode, [], [], "No indexer is configured.");
        }

        var now = DateTimeOffset.UtcNow;
        var state = await monitoring.LoadAsync(cancellationToken);
        var scope = mode switch
        {
            ProwlarrAnimeSearchMode.Episode => [episode!],
            ProwlarrAnimeSearchMode.Season => target.Episodes.Where(item => item.Key.SeasonNumber == (seasonNumber ?? episode?.Key.SeasonNumber ?? 1)).ToArray(),
            _ => target.Episodes.ToArray()
        };
        var queued = (await AnimeCanonicalEpisodes.WantedAsync(db, animeKey, cancellationToken)).ToDictionary(item => (item.Key.SeasonNumber, item.Key.EpisodeNumber));
        var wanted = scope
            .Select(item => queued.TryGetValue((item.Key.SeasonNumber, item.Key.EpisodeNumber), out var existingWanted)
                ? existingWanted with { Key = item.Key }
                : new AnimeWantedEpisode(item.Key, item.HasFile ? AnimeWantedReason.CutoffUnmet : AnimeWantedReason.Missing, now))
            .ToArray();
        var searchTarget = mode switch
        {
            ProwlarrAnimeSearchMode.Episode => AnimeReleaseJudge.SearchTargetFor(episode!),
            ProwlarrAnimeSearchMode.Season => new ProwlarrAnimeSearchTarget(
                scope.FirstOrDefault()?.SearchTitle ?? target.Anime.Title,
                AnimeReleaseJudge.Aliases(scope.FirstOrDefault()?.SearchAliases ?? target.AllTitles, scope.FirstOrDefault()?.SearchTitle ?? target.Anime.Title),
                ProwlarrAnimeSearchMode.Season,
                seasonNumber ?? episode?.Key.SeasonNumber ?? 1),
            _ => new ProwlarrAnimeSearchTarget(target.Anime.Title, AnimeReleaseJudge.Aliases(target.AllTitles, target.Anime.Title), ProwlarrAnimeSearchMode.Anime)
        };

        try
        {
            var searchOptions = new SearchOptions { Purpose = SearchPurpose.Interactive, ProwlarrIndexerIds = ProwlarrIndexerIdsFor(state, animeKey) };
            var snapshot = await observation.GetSnapshotAsync(forceRefresh: false, cancellationToken);
            var search = await core.SearchAsync(AnimeReleaseJudge.Plan(target, scope, wanted, episode?.Key, searchTarget, snapshot, now), target.Profile, searchOptions, cancellationToken);
            return new(target, episode?.Key, mode, AnimeReleaseJudge.ToCandidates(target, scope, search), search.Search.Warnings, null, search.Releases);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ProwlarrException or HttpRequestException or TaskCanceledException)
        {
            return new(target, episode?.Key, mode, [], [], $"Indexer search failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Saves the per-anime acquisition settings: monitoring lives in the monitoring state and the
    /// profile assignment in the quality profile store (the default profile is not stored as an
    /// assignment). Returns null when the anime does not exist.
    /// </summary>
    public async Task<AnimeSettingsUpdate?> UpdateAnimeSettingsAsync(
        Guid animeId,
        bool monitored,
        bool searchOnAdd,
        string? profileId,
        int[] indexerIds,
        CancellationToken cancellationToken,
        Guid? targetRootId = null)
    {
        var animeKey = await db.Anime
            .AsNoTracking()
            .Where(item => item.Id == animeId)
            .Select(item => item.Key)
            .SingleOrDefaultAsync(cancellationToken);
        if (animeKey is null)
        {
            return null;
        }

        var wasMonitored = (await animeMonitoring.LoadAsync(animeKey, cancellationToken)).IsWorkMonitored;
        var profileState = await profiles.LoadAsync(cancellationToken);
        var animeDefaultProfileId = profileState.DefaultProfileIdFor(MediaAcquisitionKind.Anime)
            ?? AnimeQualityProfiles.DefaultAnime1080pId;
        await profiles.AssignAnimeAsync(
            animeId,
            profileId is not null && !profileId.Equals(animeDefaultProfileId, StringComparison.OrdinalIgnoreCase)
                ? profileId
                : null,
            cancellationToken);

        await monitoring.UpdateAsync(
            current =>
            {
                var anime = new Dictionary<string, AnimeMonitorSettings>(current.Anime, StringComparer.OrdinalIgnoreCase);
                var existing = anime.TryGetValue(animeKey, out var found) ? found : null;
                anime[animeKey] = new AnimeMonitorSettings(
                    animeKey,
                    searchOnAdd,
                    indexerIds.Length == 0 ? null : indexerIds.Where(id => id > 0).Distinct().Order().ToArray(),
                    targetRootId);
                return current with { Anime = anime };
            },
            cancellationToken);

        await animeMonitoring.SetMonitoredAsync(animeId, monitored, cancellationToken);
        return new AnimeSettingsUpdate(animeKey, monitored && !wasMonitored);
    }

    public async Task<AnimeAcquisitionPanel?> GetAnimePanelAsync(
        Guid animeId,
        CancellationToken cancellationToken)
    {
        var animeKey = await db.Anime
            .AsNoTracking()
            .Where(item => item.Id == animeId)
            .Select(item => item.Key)
            .SingleOrDefaultAsync(cancellationToken);
        if (animeKey is null)
        {
            return null;
        }

        var state = await monitoring.LoadAsync(cancellationToken);
        var profileState = await profiles.LoadAsync(cancellationToken);
        var ownership = await ownershipStore.LoadAsync(cancellationToken);
        var prowlarrConfigured = await IsProwlarrConfiguredAsync(cancellationToken);
        var roots = await db.LibraryRoots.AsNoTracking().OrderBy(root => root.Name).ToArrayAsync(cancellationToken);
        var recentHistory = await history.ForAnimeAsync(animeId, 15, cancellationToken);
        var wantedEpisodes = await AnimeCanonicalEpisodes.WantedAsync(db, animeKey, cancellationToken);

        state.Anime.TryGetValue(animeKey, out var settings);
        var monitored = (await animeMonitoring.LoadAsync(animeKey, cancellationToken)).IsWorkMonitored;
        var assigned = profileState.ResolveProfileId(MediaAcquisitionKind.Anime, animeId)
            ?? AnimeQualityProfiles.DefaultAnime1080pId;
        var active = await CountActiveDownloadsAsync(animeKey, cancellationToken);
        var lastEvent = recentHistory.FirstOrDefault();

        return new AnimeAcquisitionPanel(
            animeId,
            animeKey,
            SonarrParallelSafety.GetMode(ownership, animeKey),
            settings,
            monitored,
            assigned,
            profileState.Profiles,
            wantedEpisodes.Count,
            active,
            lastEvent is null ? null : $"{lastEvent.OccurredAtUtc:u} · S{lastEvent.SeasonNumber:00}E{lastEvent.EpisodeNumber:00} · {lastEvent.EventKind}: {lastEvent.Reason}",
            prowlarrConfigured,
            roots,
            recentHistory);
    }

    // Unreadable settings (for example a lost Data Protection key) count as not configured here;
    // the run itself reports the underlying error. The name is kept for callers; it now reports
    // whether any indexer (Prowlarr or direct Newznab) is configured.
    public async Task<bool> IsProwlarrConfiguredAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await indexers.HasEnabledIndexerAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning(exception, "Indexer settings could not be read.");
            return false;
        }
    }

    public async Task<AnimeAcquisitionOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var state = await monitoring.LoadAsync(cancellationToken);
        var ownership = await ownershipStore.LoadAsync(cancellationToken);
        var importState = await imports.LoadAsync(cancellationToken);
        var profileState = await profiles.LoadAsync(cancellationToken);
        var operations = new OperationStore(db);

        var monitoredKeys = await animeMonitoring.MonitoredKeysAsync(cancellationToken);
        var wantedEpisodes = await AnimeCanonicalEpisodes.WantedAsync(db, null, cancellationToken);
        var keys = state.Anime.Keys
            .Concat(monitoredKeys)
            .Concat(wantedEpisodes.Select(item => item.Key.AnimeKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var anime = await db.Anime
            .AsNoTracking()
            .Where(item => keys.Contains(item.Key))
            .Select(item => new { item.Id, item.Key, item.Title })
            .ToDictionaryAsync(item => item.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var monitored = monitoredKeys
            .Where(anime.ContainsKey)
            .Select(monitoredKey =>
            {
                var entry = anime[monitoredKey];
                state.Anime.TryGetValue(monitoredKey, out var settings);
                var profile = profileState.ResolveProfileId(MediaAcquisitionKind.Anime, entry.Id)
                    ?? AnimeQualityProfiles.DefaultAnime1080pId;
                return new AnimeMonitoredRow(
                    entry.Id,
                    entry.Key,
                    entry.Title,
                    SonarrParallelSafety.GetMode(ownership, entry.Key),
                    profile,
                    wantedEpisodes.Count(item => item.Key.AnimeKey.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)),
                    settings?.IndexerIds ?? []);
            })
            .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var wanted = wantedEpisodes
            .Select(item => new AnimeWantedRow(
                item.Key,
                anime.TryGetValue(item.Key.AnimeKey, out var entry) ? entry.Title : item.Key.AnimeKey,
                anime.TryGetValue(item.Key.AnimeKey, out var known) ? known.Id : null,
                item.Reason,
                item.BecameWantedAtUtc))
            .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Key.SeasonNumber)
            .ThenBy(row => row.Key.EpisodeNumber)
            .ToArray();

        var downloads = (await operations.ListAsync(new OperationListFilter(View: "active", Kind: AnimeAcquisitionEngine.OperationKind, Limit: 50), cancellationToken))
            .Select(operation => new AnimeDownloadRow(operation.Subject ?? operation.Title, operation))
            .ToArray();

        var attention = importState.Imports
            .Where(record => record.NeedsAttention)
            .OrderByDescending(record => record.UpdatedAtUtc)
            .ToArray();
        var recentImports = importState.Imports
            .Where(record => !record.NeedsAttention)
            .OrderByDescending(record => record.UpdatedAtUtc)
            .Take(10)
            .ToArray();

        var decisions = (await operations.ListLogsAsync(new OperationLogFilter(Module: LogModule, Limit: 60), cancellationToken))
            .Concat(await operations.ListLogsAsync(new OperationLogFilter(Module: AnimeImportExecutor.LogModule, Limit: 40), cancellationToken))
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(80)
            .ToArray();

        return new AnimeAcquisitionOverview(
            monitored,
            wanted,
            downloads,
            attention,
            recentImports,
            decisions);
    }

    internal static async Task LogDecisionsAsync(
        OperationStore operations,
        Guid operationId,
        IReadOnlyList<AnimeSearchCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            await operations.AppendLogAsync(operationId, OperationLogLevel.Information, LogModule, "Prowlarr returned no results.", cancellationToken);
            return;
        }

        foreach (var candidate in candidates.Take(MaxLoggedDecisions))
        {
            await operations.AppendLogAsync(
                operationId,
                candidate.Decision.Grab ? OperationLogLevel.Information : OperationLogLevel.Warning,
                LogModule,
                Describe(candidate),
                cancellationToken);
        }

        if (candidates.Count > MaxLoggedDecisions)
        {
            await operations.AppendLogAsync(
                operationId,
                OperationLogLevel.Information,
                LogModule,
                $"{candidates.Count - MaxLoggedDecisions} further result(s) were rejected or ranked lower.",
                cancellationToken);
        }
    }

    public static string Describe(AnimeSearchCandidate candidate)
    {
        var reasons = candidate.Score.RejectionReasons.Count > 0
            ? " " + string.Join(" ", candidate.Score.RejectionReasons)
            : "";
        return $"{(candidate.Decision.Grab ? "Accepted" : "Rejected")}: {candidate.Release.Title} " +
               $"[{candidate.Score.QualityKey}, score {candidate.Score.Score}, {candidate.Release.Indexer ?? "unknown indexer"}] — {candidate.Decision.Reason}{reasons}";
    }

    public static string FormatEpisodes(IReadOnlyList<AnimeEpisodeKey> episodes)
    {
        if (episodes.Count == 0)
        {
            return "whole series";
        }

        var labels = episodes
            .OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.EpisodeNumber)
            .Select(episode => $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}")
            .ToArray();

        return labels.Length <= 3
            ? string.Join(", ", labels)
            : $"{string.Join(", ", labels.Take(3))} +{labels.Length - 3}";
    }

    public static string Label(AnimeEpisodeKey key) =>
        key.AbsoluteEpisodeNumber is { } absolute && (absolute != key.EpisodeNumber || key.SeasonNumber != 1)
            ? $"S{key.SeasonNumber:00}E{key.EpisodeNumber:00} (AniList {absolute})"
            : $"S{key.SeasonNumber:00}E{key.EpisodeNumber:00}";

    // Per-anime restriction only applies to Prowlarr's own indexer aggregation.
    private static int[]? ProwlarrIndexerIdsFor(AnimeMonitoringState state, string animeKey) =>
        state.Anime.TryGetValue(animeKey, out var settings) ? settings.IndexerIds : null;

    private async Task<int> CountActiveDownloadsAsync(string animeKey, CancellationToken cancellationToken) =>
        (await new OperationStore(db).ListAsync(new OperationListFilter(View: "active", Kind: AnimeAcquisitionEngine.OperationKind, Limit: 200), cancellationToken))
            .Count(operation => DownloadOperationDetails.TryParse(operation.Details, out var details) && string.Equals(details?.TargetKey, animeKey, StringComparison.OrdinalIgnoreCase));
}

public sealed record AnimeSettingsUpdate(
    string AnimeKey,
    bool StartedMonitoring);

public sealed record AnimeAcquisitionPanel(
    Guid AnimeId,
    string AnimeKey,
    AnimeManagementMode Mode,
    AnimeMonitorSettings? Settings,
    bool Monitored,
    string ProfileId,
    IReadOnlyList<AnimeQualityProfile> Profiles,
    int WantedCount,
    int ActiveDownloads,
    string? LastEvent,
    bool ProwlarrConfigured,
    IReadOnlyList<LibraryRoot> Roots,
    IReadOnlyList<AcquisitionHistoryEntry> RecentHistory)
{
    public bool CanAcquire => Mode != AnimeManagementMode.ReadOnlyCoexistence;
}

public sealed record AnimeMonitoredRow(
    Guid AnimeId,
    string AnimeKey,
    string Title,
    AnimeManagementMode Mode,
    string ProfileId,
    int WantedCount,
    int[] IndexerIds);

public sealed record AnimeWantedRow(
    AnimeEpisodeKey Key,
    string Title,
    Guid? AnimeId,
    AnimeWantedReason Reason,
    DateTimeOffset SinceUtc);

public sealed record AnimeDownloadRow(string Title, OperationSnapshot Operation);

public sealed record AnimeAcquisitionOverview(
    IReadOnlyList<AnimeMonitoredRow> Monitored,
    IReadOnlyList<AnimeWantedRow> Wanted,
    IReadOnlyList<AnimeDownloadRow> ActiveDownloads,
    IReadOnlyList<AnimeImportRecord> ImportsNeedingAttention,
    IReadOnlyList<AnimeImportRecord> RecentImports,
    IReadOnlyList<OperationLogEntry> RecentDecisions);
