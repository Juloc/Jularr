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

public sealed record AnimeAcquisitionRunSummary(
    int AnimeCount,
    int Searches,
    int Grabs,
    IReadOnlyList<string> Notes)
{
    public override string ToString() =>
        $"{AnimeCount} anime, {Searches} searches, {Grabs} grabs" +
        (Notes.Count == 0 ? "" : $" — {string.Join(" ", Notes)}");
}

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
    string? Error);

public sealed record AnimeGrabResult(bool Success, string Message, Guid? OperationId = null);

/// <summary>Where a grabbed, not yet finished acquisition stands: downloading, waiting for its import, or imported only in part and waiting for the owner.</summary>
public enum AnimeOpenAcquisitionStage
{
    Downloading,
    Importing,
    NeedsOwner
}

public sealed record AnimeAcquisitionSnapshot(
    SabnzbdAcquisitionStoreState Relations,
    AnimeImportStoreState Imports);

/// <summary>An acquisition that blocks a new grab for its episodes, with the latest attempt, the download Operation and (for <see cref="AnimeOpenAcquisitionStage.NeedsOwner"/>) the importer's reason.</summary>
public sealed record AnimeOpenAcquisition(
    SabnzbdAcquisition Acquisition,
    SabnzbdAcquisitionAttempt Attempt,
    OperationSnapshot Download,
    AnimeOpenAcquisitionStage Stage,
    string? ImportMessage);

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
    SabnzbdAcquisitionStore acquisitions,
    SabnzbdAcquisitionService sabnzbd,
    AnimeImportStore imports,
    AnimeAcquisitionInventory inventory,
    AnimeMonitoring animeMonitoring,
    AcquisitionHistoryService history,
    ILogger<AnimeAcquisitionPipeline> logger,
    TimeProvider clock,
    AcquisitionCore core)
{
    public const string SearchOperationKind = "anime-search";
    public const string GrabOperationKind = "anime-grab";
    public const string OperationCategory = "Acquisition";
    public const string LogModule = "Acquisition";
    public const int MaxSearchesPerAnimePerRun = 6;
    public const int MaxSearchesPerRun = 30;
    public const int MaxLoggedDecisions = 25;

    public async Task<AnimeAcquisitionRunSummary> RunAsync(
        AnimeSearchTrigger trigger,
        string? animeKey,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        await ReconcileAttemptsAsync(cancellationToken);

        var state = await monitoring.LoadAsync(cancellationToken);
        var keys = animeKey is null ? [.. await animeMonitoring.MonitoredKeysAsync(cancellationToken)] : new[] { animeKey };
        if (keys.Length == 0)
        {
            return new(0, 0, 0, ["No anime is monitored."]);
        }

        var hasIndexers = await indexers.HasEnabledIndexerAsync(cancellationToken);
        if (!hasIndexers)
        {
            notes.Add("No indexer is configured; wanted episodes were refreshed but not searched.");
        }

        var snapshot = await observation.GetSnapshotAsync(forceRefresh: true, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var searches = 0;
        var grabs = 0;

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = await inventory.LoadAsync(key, cancellationToken);
            if (target is null)
            {
                notes.Add($"{key}: anime no longer exists.");
                continue;
            }

            if (target.Diagnostic is not null)
            {
                notes.Add($"{target.Anime.Title}: {target.Diagnostic}");
            }

            var view = await animeMonitoring.LoadAsync(key, cancellationToken);
            state = await monitoring.UpdateAsync(
                current => AnimeMonitoringEngine.RefreshWantedForAnime(
                    current,
                    key,
                    unit => AnimeMonitoring.IsUnitMonitored(view, unit),
                    target.Episodes.Select(episode => AnimeAcquisitionInventory.ToInventory(episode, target.Profile)),
                    target.Profile,
                    now),
                cancellationToken);

            if (!hasIndexers)
            {
                continue;
            }

            if (SonarrParallelSafety.GetMode(snapshot.State, key) == AnimeManagementMode.ReadOnlyCoexistence)
            {
                notes.Add($"{target.Anime.Title}: read-only Sonarr coexistence; choose parallel acquisition or Jularr-managed under Sonarr migration to search.");
                continue;
            }

            var wanted = state.Wanted.Values
                .Where(item => item.Key.AnimeKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Key.SeasonNumber)
                .ThenBy(item => item.Key.EpisodeNumber)
                .ToArray();
            // An owner "Search now" ignores the failure backoff; pending/grabbed episodes are
            // still skipped so a manual run never grabs twice.
            var planAt = trigger == AnimeSearchTrigger.Manual ? DateTimeOffset.MaxValue : now;
            var requests = AnimeMonitoringEngine
                .PlanSearches(state, wanted, trigger, planAt)
                .Take(Math.Min(MaxSearchesPerAnimePerRun, MaxSearchesPerRun - searches))
                .ToArray();

            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                searches++;
                var slot = target.Find(request.Key.SeasonNumber, request.Key.EpisodeNumber);
                if (slot is null)
                {
                    continue;
                }

                var wantedEpisode = wanted.First(item => item.Key == request.Key);
                var grabbed = await SearchAndGrabAsync(
                    target,
                    slot,
                    wantedEpisode,
                    wanted,
                    request,
                    snapshot,
                    cancellationToken);
                if (grabbed)
                {
                    grabs++;
                }
            }

            if (searches >= MaxSearchesPerRun)
            {
                notes.Add($"Search limit of {MaxSearchesPerRun} per run reached; remaining wanted episodes wait for the next run.");
                break;
            }
        }

        return new(keys.Length, searches, grabs, notes);
    }

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
        var wanted = scope
            .Select(item => state.Wanted.TryGetValue(item.Key.ToString(), out var existingWanted)
                ? existingWanted
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
            var search = await core.SearchAsync(AnimeReleaseJudge.Plan(target, scope, wanted, episode?.Key, searchTarget, state, snapshot, now), target.Profile, searchOptions, cancellationToken);
            return new(target, episode?.Key, mode, AnimeReleaseJudge.ToCandidates(target, scope, search), search.Search.Warnings, null);
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
    /// Owner grab of one search result. Ownership and duplicate protection always apply; a
    /// profile rejection may be overridden because the owner chose the release explicitly.
    /// </summary>
    public async Task<AnimeGrabResult> GrabAsync(
        string animeKey,
        int? seasonNumber,
        int? episodeNumber,
        ProwlarrAnimeSearchMode mode,
        string releaseIdentity,
        CancellationToken cancellationToken)
    {
        var search = await SearchInteractiveAsync(animeKey, seasonNumber, episodeNumber, mode, cancellationToken);
        if (search is null)
        {
            return new(false, "Anime not found.");
        }

        if (search.Error is not null)
        {
            return new(false, search.Error);
        }

        var candidate = search.Candidates.FirstOrDefault(item =>
            item.Release.Identity.Equals(releaseIdentity, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            return new(false, "The release is no longer offered by Prowlarr; search again.");
        }

        if (candidate.Release.InternalDownloadUri is null)
        {
            return new(false, "The release has no NZB link.");
        }

        var episodes = candidate.CoveredEpisodes.Count > 0
            ? candidate.CoveredEpisodes
            : search.Episode is { } key ? [key] : [];
        if (episodes.Count == 0)
        {
            return new(false, "The release does not cover an episode of this anime.");
        }

        var now = DateTimeOffset.UtcNow;
        var snapshot = await observation.GetSnapshotAsync(forceRefresh: true, cancellationToken);
        var release = candidate.Release.ParsedRelease;
        var ownership = SonarrParallelSafety.CanGrab(
            snapshot,
            new AcquisitionGrabRequest(
                animeKey,
                release.ReleaseKey,
                release.SeasonNumber ?? episodes[0].SeasonNumber,
                release.EpisodeStart ?? episodes[0].EpisodeNumber,
                release.EpisodeEnd ?? episodes[^1].EpisodeNumber,
                release.AbsoluteEpisodeStart ?? episodes[0].AbsoluteEpisodeNumber,
                release.AbsoluteEpisodeEnd ?? episodes[^1].AbsoluteEpisodeNumber),
            now);
        if (!ownership.Allowed)
        {
            return new(false, $"Ownership: {ownership.Reason}");
        }

        var state = await monitoring.LoadAsync(cancellationToken);
        if (state.Attempts.Values.Any(attempt =>
                attempt.ReleaseKey is not null &&
                attempt.ReleaseKey.Equals(release.ReleaseKey, StringComparison.OrdinalIgnoreCase) &&
                attempt.Status is AnimeAcquisitionAttemptStatus.Pending or AnimeAcquisitionAttemptStatus.Grabbed))
        {
            return new(false, "This release is already pending or was already grabbed.");
        }

        if (await FindOpenAcquisitionAsync(animeKey, episodes, cancellationToken) is { } open)
        {
            return new(false, open);
        }

        var operations = new OperationStore(db);
        var operationId = await operations.CreateAsync(
            new OperationDescriptor(
                GrabOperationKind,
                OperationCategory,
                "Anime grab",
                $"{search.Target.Anime.Title} · {SabnzbdAcquisitionService.FormatEpisodes(episodes)} · {candidate.Release.Title}",
                search.Target.Profile.Id,
                OperationLane.Interactive,
                Retryable: false),
            cancellationToken);
        await operations.MarkRunningAsync(operationId, cancellationToken);
        await operations.AppendLogAsync(
            operationId,
            candidate.Decision.Grab ? OperationLogLevel.Information : OperationLogLevel.Warning,
            LogModule,
            candidate.Decision.Grab
                ? $"Owner grab: {Describe(candidate)}"
                : $"Owner override of a rejected release: {Describe(candidate)}",
            cancellationToken);

        var grabbed = await SubmitAsync(search.Target, episodes, [candidate], operationId, cancellationToken);
        return grabbed is null
            ? new(false, "SABnzbd did not accept the release; see the grab operation for details.", operationId)
            : new(true, $"Sent to SABnzbd: {grabbed.Release.Title}", operationId);
    }

    /// <summary>
    /// Brings persisted monitoring attempts in line with the acquisition relation, Operations and
    /// import records, so restarts never leave an episode stuck as pending/grabbed or grab twice.
    /// </summary>
    public async Task ReconcileAttemptsAsync(CancellationToken cancellationToken)
    {
        var state = await monitoring.LoadAsync(cancellationToken);
        if (state.Attempts.Count == 0)
        {
            return;
        }

        var relations = await acquisitions.LoadAsync(cancellationToken);
        var importState = await imports.LoadAsync(cancellationToken);
        var operations = new OperationStore(db);
        var now = DateTimeOffset.UtcNow;
        var updates = new List<Func<AnimeMonitoringState, AnimeMonitoringState>>();
        var exhausted = new HashSet<Guid>();
        var unregistered = new List<SabnzbdAcquisition>();

        foreach (var attempt in state.Attempts.Values)
        {
            var key = attempt.Key;
            switch (attempt.Status)
            {
                case AnimeAcquisitionAttemptStatus.Pending:
                    // The process may have stopped after SABnzbd accepted the release but before
                    // the grab was recorded; the relation store knows, so never search again then.
                    var started = relations.Acquisitions
                        .Where(item =>
                            item.LatestAttempt is not null &&
                            item.Episodes.Any(episode => SameEpisode(episode, key)) &&
                            item.CreatedAtUtc >= (attempt.LastAttemptAtUtc ?? DateTimeOffset.MinValue))
                        .MaxBy(item => item.UpdatedAtUtc);
                    if (started is null)
                    {
                        updates.Add(current => AnimeMonitoringEngine.ClearAttempt(current, key, now, "Search was interrupted before a grab; it will be retried."));
                        break;
                    }

                    var recoveredKey = ReleaseKeyOf(started.LatestAttempt!.ReleaseTitle);
                    updates.Add(current => AnimeMonitoringEngine.MarkGrabbed(current, key, recoveredKey, now));
                    unregistered.Add(started);
                    break;

                case AnimeAcquisitionAttemptStatus.Grabbed:
                    var acquisition = relations.Acquisitions
                        .Where(item => item.Episodes.Any(episode => SameEpisode(episode, key)) && item.LatestAttempt is not null)
                        .MaxBy(item => item.UpdatedAtUtc);
                    if (acquisition is null)
                    {
                        updates.Add(current => AnimeMonitoringEngine.ClearAttempt(current, key, now, "No acquisition exists for the grab; it will be searched again."));
                        break;
                    }

                    var operation = await operations.GetAsync(acquisition.LatestAttempt!.OperationId, cancellationToken);
                    if (operation is null || operation.IsActive)
                    {
                        break;
                    }

                    if (operation.Status == OperationStatus.Succeeded)
                    {
                        var import = importState.Imports.FirstOrDefault(record => record.DownloadOperationId == operation.Id);
                        if (import?.Status == AnimeImportStatus.Imported)
                        {
                            updates.Add(current => AnimeMonitoringEngine.ClearAttempt(current, key, now, "Imported into the library."));
                        }
                        else if (import?.Status is AnimeImportStatus.Failed or AnimeImportStatus.Dismissed)
                        {
                            updates.Add(current => AnimeMonitoringEngine.MarkFailed(current, key, attempt.ReleaseKey, now));
                        }

                        break;
                    }

                    // A failed download advances to the next candidate (SABnzbd monitor); a
                    // cancelled or interrupted one does not, so the episode backs off instead.
                    // A failure of this server or its client leaves the release usable: the episode is searched again later, with no back-off raised.
                    if (operation.Status == OperationStatus.Failed && DownloadOperationDetails.TryParse(operation.Details, out var failedDetails) && SabnzbdFailureKinds.IsInfrastructure(failedDetails?.FailureKind))
                    {
                        updates.Add(current => AnimeMonitoringEngine.MarkUnavailable(current, key, now, ReleaseRequestTracker.UnavailableRetry, "The download could not finish because of a local problem."));
                        exhausted.Add(acquisition.Id);
                        break;
                    }

                    var canAdvance = operation.Status == OperationStatus.Failed &&
                                     acquisition.Attempts.Length < acquisition.MaxAttempts &&
                                     acquisition.PendingCandidates.Any(candidate => !relations.IsBlocked(candidate.ReleaseIdentity));
                    if (!canAdvance)
                    {
                        updates.Add(current => AnimeMonitoringEngine.MarkFailed(current, key, attempt.ReleaseKey, now));
                        exhausted.Add(acquisition.Id);
                    }

                    break;
            }
        }

        if (updates.Count > 0)
        {
            await monitoring.UpdateAsync(
                current => updates.Aggregate(current, (accumulated, update) => update(accumulated)),
                cancellationToken);
        }

        if (exhausted.Count > 0 || unregistered.Count > 0)
        {
            await ownershipStore.UpdateAsync(
                current =>
                {
                    var next = exhausted.Aggregate(current, (accumulated, id) =>
                        accumulated.Jobs.TryGetValue(id.ToString(), out var job) && job.Status == AcquisitionOwnershipStatus.Pending
                            ? SonarrParallelSafety.RegisterJob(accumulated, job with { Status = AcquisitionOwnershipStatus.Failed, UpdatedAtUtc = now })
                            : accumulated);
                    return unregistered.Aggregate(next, (accumulated, item) =>
                        accumulated.Jobs.ContainsKey(item.Id.ToString())
                            ? accumulated
                            : SonarrParallelSafety.RegisterJob(
                                accumulated,
                                new AcquisitionOwnership(
                                    item.Id.ToString(),
                                    item.AnimeKey,
                                    AcquisitionOwner.Jularr,
                                    ReleaseKeyOf(item.LatestAttempt!.ReleaseTitle),
                                    AcquisitionOwnershipStatus.Pending,
                                    now)));
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// Returns why a new grab for these episodes must not start: an earlier acquisition is still
    /// downloading or its completed download waits for (manual) import. The acquisition relation
    /// and Operations are the source of truth, so this also holds after a restart.
    /// </summary>
    public async Task<string?> FindOpenAcquisitionAsync(
        string animeKey,
        IReadOnlyList<AnimeEpisodeKey> episodes,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadAcquisitionSnapshotAsync(cancellationToken);
        var open = (await ListOpenAcquisitionsAsync(snapshot, animeKey, episodes, clock.GetUtcNow().UtcDateTime, cancellationToken)).FirstOrDefault();
        return open is null
            ? null
            : open.Stage == AnimeOpenAcquisitionStage.Downloading
                ? $"'{open.Attempt.ReleaseTitle}' is still downloading for {SabnzbdAcquisitionService.FormatEpisodes(open.Acquisition.Episodes)}."
                : $"'{open.Attempt.ReleaseTitle}' finished downloading and waits for import.";
    }

    /// <summary>The acquisition relations and import records, read once so many questions can be answered from one consistent view.</summary>
    public async Task<AnimeAcquisitionSnapshot> LoadAcquisitionSnapshotAsync(CancellationToken cancellationToken) =>
        new(await acquisitions.LoadAsync(cancellationToken), await imports.LoadAsync(cancellationToken));

    /// <summary>
    /// The acquisitions of this anime that cover one of the episodes and are not finished: still downloading, or downloaded
    /// and waiting for the import (which includes an import the owner has to resolve), newest first. The acquisition relation,
    /// Operations and import records are the source of truth, so the answer also holds after a restart. A download that finished
    /// within <see cref="AnimeImportExecutor.RecoveryWindow"/> of <paramref name="nowUtc"/> without an import record still waits for it.
    /// </summary>
    public async Task<IReadOnlyList<AnimeOpenAcquisition>> ListOpenAcquisitionsAsync(AnimeAcquisitionSnapshot state, string animeKey, IReadOnlyList<AnimeEpisodeKey> episodes, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var candidates = state.Relations.Acquisitions
            .Where(item =>
                item.LatestAttempt is not null &&
                item.AnimeKey.Equals(animeKey, StringComparison.OrdinalIgnoreCase) &&
                item.Episodes.Any(episode => episodes.Any(key => SameEpisode(episode, key))))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var operations = new OperationStore(db);
        var open = new List<AnimeOpenAcquisition>();
        foreach (var acquisition in candidates)
        {
            var attempt = acquisition.LatestAttempt!;
            var operation = await operations.GetAsync(attempt.OperationId, cancellationToken);
            if (operation is null)
            {
                continue;
            }

            if (operation.IsActive)
            {
                open.Add(new AnimeOpenAcquisition(acquisition, attempt, operation, AnimeOpenAcquisitionStage.Downloading, null));
            }
            else if (operation.Status == OperationStatus.Succeeded)
            {
                var import = state.Imports.Imports.FirstOrDefault(record => record.DownloadOperationId == operation.Id);
                var awaitingRecovery = import is null &&
                                       operation.FinishedAtUtc is { } finished &&
                                       finished >= nowUtc - AnimeImportExecutor.RecoveryWindow;
                if (import?.Status == AnimeImportStatus.ManualRequired)
                {
                    open.Add(new AnimeOpenAcquisition(acquisition, attempt, operation, AnimeOpenAcquisitionStage.NeedsOwner, import.Message));
                }
                else if (awaitingRecovery || import?.Status == AnimeImportStatus.Importing)
                {
                    open.Add(new AnimeOpenAcquisition(acquisition, attempt, operation, AnimeOpenAcquisitionStage.Importing, null));
                }
            }
        }

        return open;
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

    public Task UpdateScheduleAsync(
        bool enabled,
        int intervalMinutes,
        CancellationToken cancellationToken) =>
        monitoring.UpdateAsync(
            current => current with
            {
                Schedule = new AnimeMonitoringSchedule(
                    enabled,
                    Math.Clamp(intervalMinutes, AnimeMonitoringSchedule.MinimumIntervalMinutes, AnimeMonitoringSchedule.MaximumIntervalMinutes))
            },
            cancellationToken);

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
        var relations = await acquisitions.LoadAsync(cancellationToken);
        var prowlarrConfigured = await IsProwlarrConfiguredAsync(cancellationToken);
        var roots = await db.LibraryRoots.AsNoTracking().OrderBy(root => root.Name).ToArrayAsync(cancellationToken);
        var recentHistory = await history.ForAnimeAsync(animeId, 15, cancellationToken);
        var wantedEpisodes = await AnimeCanonicalEpisodes.WantedAsync(db, animeKey, cancellationToken);

        state.Anime.TryGetValue(animeKey, out var settings);
        var monitored = (await animeMonitoring.LoadAsync(animeKey, cancellationToken)).IsWorkMonitored;
        var assigned = profileState.ResolveProfileId(MediaAcquisitionKind.Anime, animeId)
            ?? AnimeQualityProfiles.DefaultAnime1080pId;
        var active = await CountActiveDownloadsAsync(relations, animeKey, cancellationToken);
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
        var relations = await acquisitions.LoadAsync(cancellationToken);
        var importState = await imports.LoadAsync(cancellationToken);
        var profileState = await profiles.LoadAsync(cancellationToken);
        var operations = new OperationStore(db);

        var monitoredKeys = await animeMonitoring.MonitoredKeysAsync(cancellationToken);
        var wantedEpisodes = await AnimeCanonicalEpisodes.WantedAsync(db, null, cancellationToken);
        var keys = state.Anime.Keys
            .Concat(monitoredKeys)
            .Concat(wantedEpisodes.Select(item => item.Key.AnimeKey))
            .Concat(relations.Acquisitions.Select(item => item.AnimeKey))
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

        var downloads = new List<AnimeDownloadRow>();
        foreach (var acquisition in relations.Acquisitions.Where(item => item.LatestAttempt is not null).OrderByDescending(item => item.UpdatedAtUtc).Take(50))
        {
            var operation = await operations.GetAsync(acquisition.LatestAttempt!.OperationId, cancellationToken);
            if (operation is null || !operation.IsActive)
            {
                continue;
            }

            downloads.Add(new AnimeDownloadRow(acquisition, acquisition.LatestAttempt, operation));
        }

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
            state.Schedule,
            monitored,
            wanted,
            downloads,
            attention,
            recentImports,
            decisions);
    }

    private async Task<bool> SearchAndGrabAsync(
        AnimeAcquisitionTarget target,
        AnimeAcquisitionEpisode episode,
        AnimeWantedEpisode wanted,
        IReadOnlyList<AnimeWantedEpisode> allWanted,
        AnimeSearchRequest request,
        AcquisitionOwnershipSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var operations = new OperationStore(db);
        var now = DateTimeOffset.UtcNow;
        if (await FindOpenAcquisitionAsync(target.Anime.Key, [episode.Key], cancellationToken) is { } open)
        {
            // The monitoring attempt was lost (for example an older state file), but a download
            // for the episode exists: record it as grabbed instead of searching again.
            await monitoring.UpdateAsync(
                current => AnimeMonitoringEngine.MarkGrabbed(current, episode.Key, "existing-acquisition", now),
                cancellationToken);
            logger.LogInformation("Skipped the search for {Episode}: {Reason}", episode.Key, open);
            return false;
        }

        var operationId = await operations.CreateAsync(
            new OperationDescriptor(
                SearchOperationKind,
                OperationCategory,
                "Anime search",
                $"{target.Anime.Title} · {Label(episode.Key)} · {wanted.Reason}",
                target.Profile.Id,
                OperationLane.Normal,
                Retryable: false),
            cancellationToken);
        await operations.MarkRunningAsync(operationId, cancellationToken);

        var state = await monitoring.UpdateAsync(
            current => AnimeMonitoringEngine.MarkPending(current, request, now),
            cancellationToken);

        try
        {
            var episodeTarget = AnimeReleaseJudge.PlannedTargetFor(target, episode, allWanted);
            var searchOptions = new SearchOptions { ProwlarrIndexerIds = ProwlarrIndexerIdsFor(state, target.Anime.Key) };
            var search = await core.SearchAsync(AnimeReleaseJudge.Plan(target, [episode], allWanted, episode.Key, episodeTarget, state, snapshot, now), target.Profile, searchOptions, cancellationToken);
            foreach (var warning in search.Search.Warnings)
            {
                await operations.AppendLogAsync(operationId, OperationLogLevel.Warning, LogModule, $"{warning.IndexerName}: {warning.Message}{(string.IsNullOrEmpty(warning.Query) ? "" : $" ({warning.Query})")}", cancellationToken);
            }

            var candidates = AnimeReleaseJudge.ToCandidates(target, [episode], search);
            await LogDecisionsAsync(operations, operationId, candidates, cancellationToken);

            var accepted = candidates.Where(candidate => candidate.Decision.Grab).ToArray();
            if (accepted.Length == 0)
            {
                if (candidates.Count == 0 && search.Search.EveryIndexerFailed)
                {
                    // Nothing could be asked, so nothing was found: the outage is not a failed search and does not raise the back-off.
                    const string outage = "No indexer could be searched; the search is repeated soon.";
                    await monitoring.UpdateAsync(current => AnimeMonitoringEngine.MarkUnavailable(current, episode.Key, now, ReleaseRequestTracker.UnavailableRetry, outage), cancellationToken);
                    await operations.MarkSucceededAsync(operationId, outage, CancellationToken.None);
                    return false;
                }

                await monitoring.UpdateAsync(current => AnimeMonitoringEngine.MarkFailed(current, episode.Key, null, now), cancellationToken);
                await operations.MarkSucceededAsync(
                    operationId,
                    $"No accepted release among {candidates.Count} result(s); retried after backoff.",
                    CancellationToken.None);
                return false;
            }

            var episodes = accepted[0].CoveredEpisodes.Count > 0 ? accepted[0].CoveredEpisodes : [episode.Key];
            var grabbed = await SubmitAsync(target, episodes, accepted, operationId, cancellationToken);
            return grabbed is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ProwlarrException or HttpRequestException or TaskCanceledException)
        {
            await monitoring.UpdateAsync(current => AnimeMonitoringEngine.MarkUnavailable(current, episode.Key, now, ReleaseRequestTracker.UnavailableRetry, "The indexers did not answer."), cancellationToken);
            await operations.MarkFailedAsync(operationId, $"Indexer search failed: {exception.Message}", CancellationToken.None);
            return false;
        }
    }

    // Sends the accepted candidates (best first) to SABnzbd, records the grab on the monitoring
    // state and registers the Jularr ownership job for the release that was actually submitted.
    private async Task<AnimeSearchCandidate?> SubmitAsync(
        AnimeAcquisitionTarget target,
        IReadOnlyList<AnimeEpisodeKey> episodes,
        IReadOnlyList<AnimeSearchCandidate> candidates,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operations = new OperationStore(db);
        var now = DateTimeOffset.UtcNow;
        if (await FindOpenAcquisitionAsync(target.Anime.Key, episodes, cancellationToken) is { } open)
        {
            var skipped = $"Not sent to SABnzbd: {open}";
            await operations.AppendLogAsync(operationId, OperationLogLevel.Warning, LogModule, skipped, cancellationToken);
            await operations.MarkSucceededAsync(operationId, skipped, CancellationToken.None);
            return null;
        }

        SabnzbdAcquisitionResult result;
        try
        {
            result = await sabnzbd.StartAsync(
                new SabnzbdAnimeAcquisitionRequest(
                    target.Anime.Key,
                    target.Anime.Title,
                    episodes,
                    target.Profile.Id,
                    candidates
                        .Where(candidate => candidate.Release.InternalDownloadUri is not null)
                        .Select(candidate => new SabnzbdAnimeReleaseCandidate(
                            candidate.Release.Identity,
                            candidate.Release.Title,
                            candidate.Release.InternalDownloadUri!,
                            candidate.Release.Indexer,
                            candidate.Release.ParsedRelease.ReleaseGroup))
                        .ToArray()),
                cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            await monitoring.UpdateAsync(
                current => episodes.Aggregate(current, (accumulated, key) => AnimeMonitoringEngine.MarkUnavailable(accumulated, key, now, ReleaseRequestTracker.UnavailableRetry, "The download client did not accept the release.")),
                cancellationToken);
            await operations.MarkFailedAsync(operationId, $"SABnzbd submission failed: {exception.Message}", CancellationToken.None);
            return null;
        }

        var acquisition = await acquisitions.GetAsync(result.AcquisitionId, cancellationToken);
        var identity = acquisition?.LatestAttempt?.ReleaseIdentity;
        var chosen = candidates.FirstOrDefault(candidate =>
            identity is not null && candidate.Release.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase));

        if (!result.Submitted || chosen is null)
        {
            await monitoring.UpdateAsync(
                current => episodes.Aggregate(current, (accumulated, key) => AnimeMonitoringEngine.MarkFailed(accumulated, key, chosen?.Release.ParsedRelease.ReleaseKey, now)),
                cancellationToken);
            await operations.MarkFailedAsync(operationId, result.Message, CancellationToken.None);
            return null;
        }

        var releaseKey = chosen.Release.ParsedRelease.ReleaseKey;
        var download = result.OperationId is { } downloadId ? await operations.GetAsync(downloadId, cancellationToken) : null;
        await ownershipStore.UpdateAsync(
            current => SonarrParallelSafety.RegisterJob(
                current,
                new AcquisitionOwnership(
                    result.AcquisitionId.ToString(),
                    target.Anime.Key,
                    AcquisitionOwner.Jularr,
                    releaseKey,
                    AcquisitionOwnershipStatus.Pending,
                    now,
                    download?.ExternalId)),
            cancellationToken);
        await monitoring.UpdateAsync(
            current => episodes.Aggregate(current, (accumulated, key) => AnimeMonitoringEngine.MarkGrabbed(accumulated, key, releaseKey, now)),
            cancellationToken);

        foreach (var episodeKey in episodes)
        {
            await history.RecordAsync(
                new AcquisitionHistoryEntry
                {
                    AnimeId = target.Anime.Id,
                    SeasonNumber = episodeKey.SeasonNumber,
                    EpisodeNumber = episodeKey.EpisodeNumber,
                    AbsoluteEpisodeNumber = episodeKey.AbsoluteEpisodeNumber,
                    EventKind = AcquisitionHistoryEventKind.Grabbed,
                    ReleaseTitle = chosen.Release.Title,
                    ReleaseKey = releaseKey,
                    Score = chosen.Score.Score,
                    QualityKey = chosen.Score.QualityKey,
                    Indexer = chosen.Release.Indexer,
                    Reason = chosen.Decision.Reason,
                    OccurredAtUtc = now.UtcDateTime
                },
                cancellationToken);
        }

        var message = $"Sent to SABnzbd: {chosen.Release.Title} for {SabnzbdAcquisitionService.FormatEpisodes(episodes)}.";
        await operations.AppendLogAsync(operationId, OperationLogLevel.Information, LogModule, message, cancellationToken);
        await operations.MarkSucceededAsync(operationId, message, CancellationToken.None);
        logger.LogInformation("Anime acquisition grabbed {Release} for {Anime} {Episodes}.", chosen.Release.Title, target.Anime.Key, SabnzbdAcquisitionService.FormatEpisodes(episodes));
        return chosen;
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

    public static string Label(AnimeEpisodeKey key) =>
        key.AbsoluteEpisodeNumber is { } absolute && (absolute != key.EpisodeNumber || key.SeasonNumber != 1)
            ? $"S{key.SeasonNumber:00}E{key.EpisodeNumber:00} (AniList {absolute})"
            : $"S{key.SeasonNumber:00}E{key.EpisodeNumber:00}";

    // The local slot identifies an episode; the absolute number is derived and may be filled
    // in later by a mapping, so it is not part of the identity here.
    private static bool SameEpisode(AnimeEpisodeKey left, AnimeEpisodeKey right) =>
        left.AnimeKey.Equals(right.AnimeKey, StringComparison.OrdinalIgnoreCase) &&
        left.SeasonNumber == right.SeasonNumber &&
        left.EpisodeNumber == right.EpisodeNumber;

    private static string ReleaseKeyOf(string releaseTitle) =>
        AnimeReleaseParser.Parse(releaseTitle).ReleaseKey is { Length: > 0 } releaseKey
            ? releaseKey
            : releaseTitle;

    // Per-anime restriction only applies to Prowlarr's own indexer aggregation.
    private static int[]? ProwlarrIndexerIdsFor(AnimeMonitoringState state, string animeKey) =>
        state.Anime.TryGetValue(animeKey, out var settings) ? settings.IndexerIds : null;

    private async Task<int> CountActiveDownloadsAsync(
        SabnzbdAcquisitionStoreState relations,
        string animeKey,
        CancellationToken cancellationToken)
    {
        var operations = new OperationStore(db);
        var active = 0;
        foreach (var acquisition in relations.Acquisitions.Where(item =>
                     item.AnimeKey.Equals(animeKey, StringComparison.OrdinalIgnoreCase) && item.LatestAttempt is not null))
        {
            var operation = await operations.GetAsync(acquisition.LatestAttempt!.OperationId, cancellationToken);
            if (operation?.IsActive == true)
            {
                active++;
            }
        }

        return active;
    }
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

public sealed record AnimeDownloadRow(
    SabnzbdAcquisition Acquisition,
    SabnzbdAcquisitionAttempt Attempt,
    OperationSnapshot Operation);

public sealed record AnimeAcquisitionOverview(
    AnimeMonitoringSchedule Schedule,
    IReadOnlyList<AnimeMonitoredRow> Monitored,
    IReadOnlyList<AnimeWantedRow> Wanted,
    IReadOnlyList<AnimeDownloadRow> ActiveDownloads,
    IReadOnlyList<AnimeImportRecord> ImportsNeedingAttention,
    IReadOnlyList<AnimeImportRecord> RecentImports,
    IReadOnlyList<OperationLogEntry> RecentDecisions);
