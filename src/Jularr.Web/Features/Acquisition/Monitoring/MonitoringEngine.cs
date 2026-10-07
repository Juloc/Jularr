using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.Acquisition.Monitoring;

/// <summary>
/// The media-type-agnostic monitoring engine. It decides what is wanted, plans searches, evaluates
/// candidates for auto-grab and records attempts/history for any media type, at whole-item, season
/// or episode granularity (see <see cref="MonitoringGranularity"/>). Anime plugs in as one media
/// type (episode granularity) with no behavioural change; movies/books/audiobooks plug in at item
/// granularity and series at season granularity. The engine is pure: every method returns a new
/// <see cref="MonitoringState"/> and never touches storage.
/// </summary>
public static class MonitoringEngine
{
    public static IReadOnlyList<WantedUnit> GetWanted(
        MonitoringState state,
        IEnumerable<MonitoredUnitInventory> inventory,
        QualityProfile profile,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(profile);

        var wanted = new List<WantedUnit>();

        foreach (var unit in inventory)
        {
            if (!IsMonitored(state, unit.Key))
            {
                continue;
            }

            if (unit.AirsAtUtc is DateTimeOffset airsAt && airsAt > now)
            {
                continue;
            }

            if (!unit.HasFile)
            {
                wanted.Add(new WantedUnit(
                    unit.Key,
                    WantedReason.Missing,
                    now));
                continue;
            }

            if (unit.CurrentFile is not null &&
                profile.UpgradeAllowed &&
                !IsCutoffMet(profile, unit.CurrentFile))
            {
                wanted.Add(new WantedUnit(
                    unit.Key,
                    WantedReason.CutoffUnmet,
                    now));
            }
        }

        return wanted;
    }

    public static MonitoringState RefreshWanted(
        MonitoringState state,
        IEnumerable<MonitoredUnitInventory> inventory,
        QualityProfile profile,
        DateTimeOffset now)
    {
        var computed = GetWanted(state, inventory, profile, now);
        var wanted = new Dictionary<string, WantedUnit>(StringComparer.OrdinalIgnoreCase);
        var history = state.History.ToList();

        foreach (var item in computed)
        {
            var id = item.Key.ToString();
            if (state.Wanted.TryGetValue(id, out var existing) && existing.Reason == item.Reason)
            {
                wanted[id] = existing;
                continue;
            }

            wanted[id] = item;
            history.Add(new MonitoringHistoryEntry(
                now,
                item.Key,
                "wanted",
                item.Reason.ToString()));
        }

        foreach (var previous in state.Wanted.Values)
        {
            if (wanted.ContainsKey(previous.Key.ToString()))
            {
                continue;
            }

            history.Add(new MonitoringHistoryEntry(
                now,
                previous.Key,
                "wanted-cleared",
                previous.Reason.ToString()));
        }

        TrimHistory(history);
        return state with
        {
            Wanted = wanted,
            History = history
        };
    }

    // Same as RefreshWanted, but only the wanted entries of one work are recomputed; entries of
    // other works are kept untouched so single-work runs never clear them.
    public static MonitoringState RefreshWantedForAnime(
        MonitoringState state,
        string animeKey,
        IEnumerable<MonitoredUnitInventory> inventory,
        QualityProfile profile,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animeKey);

        var others = state.Wanted
            .Where(pair => !pair.Value.Key.AnimeKey.Equals(animeKey, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var scoped = state with
        {
            Wanted = state.Wanted
                .Where(pair => pair.Value.Key.AnimeKey.Equals(animeKey, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };

        var refreshed = RefreshWanted(scoped, inventory, profile, now);
        foreach (var pair in refreshed.Wanted)
        {
            others[pair.Key] = pair.Value;
        }

        return refreshed with { Wanted = others };
    }

    public static IReadOnlyList<MonitoringSearchRequest> PlanSearches(
        MonitoringState state,
        IEnumerable<WantedUnit> wanted,
        MonitoringSearchTrigger trigger,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(wanted);

        var requests = new List<MonitoringSearchRequest>();
        foreach (var item in wanted)
        {
            if (!state.Anime.TryGetValue(item.Key.AnimeKey, out var settings))
            {
                continue;
            }

            if (trigger == MonitoringSearchTrigger.SearchOnAdd && !settings.SearchOnAdd)
            {
                continue;
            }

            if (state.Attempts.TryGetValue(item.Key.ToString(), out var attempt))
            {
                if (attempt.Status is AcquisitionAttemptStatus.Pending or AcquisitionAttemptStatus.Grabbed)
                {
                    continue;
                }

                if (attempt.NextRetryAtUtc is DateTimeOffset retryAt && retryAt > now)
                {
                    continue;
                }
            }

            requests.Add(new MonitoringSearchRequest(item.Key, item.Reason, trigger));
        }

        return requests;
    }

    public static AutoGrabDecision EvaluateCandidate(
        QualityProfile profile,
        WantedUnit wanted,
        ReleaseScoreResult candidate,
        ReleaseScoreResult? currentFile,
        MonitoringState state,
        AcquisitionOwnershipSnapshot? ownership = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(state);

        if (!candidate.Accepted)
        {
            return new(false, "Candidate is rejected by the assigned quality profile.", candidate);
        }

        if (FindGrabBlock(wanted, candidate.Candidate.Release, state, ownership, now) is { } blocked)
        {
            return new(false, blocked, candidate);
        }

        if (wanted.Reason == WantedReason.Missing)
        {
            return new(true, "Accepted candidate satisfies a missing monitored unit.", candidate);
        }

        if (currentFile is null)
        {
            return new(false, "Upgrade decision requires the current file score.", candidate);
        }

        return ReleaseScorer.IsUpgrade(profile, currentFile, candidate)
            ? new(true, "Accepted candidate is an upgrade over the current file.", candidate)
            : new(false, "Candidate is not an upgrade over the current file.", candidate);
    }

    /// <summary>
    /// Why a release must not be grabbed for the wanted unit whatever its quality: it is for another unit, it is already pending or grabbed, or
    /// another owner (Sonarr running in parallel) holds it. Null when nothing blocks it. The shared selection engine treats this as a hard
    /// rejection, not as a preference.
    /// </summary>
    public static string? FindGrabBlock(
        WantedUnit wanted,
        ReleaseInfo release,
        MonitoringState state,
        AcquisitionOwnershipSnapshot? ownership = null,
        DateTimeOffset? now = null)
    {
        if (!MatchesUnit(wanted.Key, release))
        {
            return "Candidate does not match the wanted unit.";
        }

        if (state.Attempts.Values.Any(attempt =>
                attempt.ReleaseKey is not null &&
                attempt.ReleaseKey.Equals(release.ReleaseKey, StringComparison.OrdinalIgnoreCase) &&
                attempt.Status is AcquisitionAttemptStatus.Pending or AcquisitionAttemptStatus.Grabbed))
        {
            return "Release is already pending or was already grabbed.";
        }

        if (ownership is null)
        {
            return null;
        }

        var key = wanted.Key;
        var ownershipDecision = SonarrParallelSafety.CanGrab(
            ownership,
            new AcquisitionGrabRequest(
                key.AnimeKey,
                release.ReleaseKey,
                release.SeasonNumber ?? key.SeasonNumber,
                release.EpisodeStart ?? key.EpisodeNumber,
                release.EpisodeEnd ?? key.EpisodeNumber,
                release.AbsoluteEpisodeStart ?? key.AbsoluteEpisodeNumber,
                release.AbsoluteEpisodeEnd ?? key.AbsoluteEpisodeNumber),
            now ?? DateTimeOffset.UtcNow);
        return ownershipDecision.Allowed ? null : $"Ownership: {ownershipDecision.Reason}";
    }

    public static MonitoringState MarkPending(
        MonitoringState state,
        MonitoringSearchRequest request,
        DateTimeOffset now)
    {
        var attempts = CloneAttempts(state);
        var key = request.Key.ToString();
        attempts[key] = attempts.TryGetValue(key, out var existing)
            ? existing with
            {
                Status = AcquisitionAttemptStatus.Pending,
                LastAttemptAtUtc = now,
                NextRetryAtUtc = null
            }
            : new AcquisitionAttempt(
                request.Key,
                AcquisitionAttemptStatus.Pending,
                null,
                0,
                now,
                null);

        return WithHistory(state, attempts, request.Key, now, "search-pending", request.Reason.ToString());
    }

    public static MonitoringState MarkGrabbed(
        MonitoringState state,
        MonitoredUnitKey key,
        string releaseKey,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseKey);

        var attempts = CloneAttempts(state);
        var id = key.ToString();
        var existing = attempts.TryGetValue(id, out var found)
            ? found
            : new AcquisitionAttempt(key, AcquisitionAttemptStatus.None, null, 0, null, null);

        attempts[id] = existing with
        {
            Status = AcquisitionAttemptStatus.Grabbed,
            ReleaseKey = releaseKey,
            LastAttemptAtUtc = now,
            NextRetryAtUtc = null
        };

        return WithHistory(state, attempts, key, now, "grabbed", releaseKey);
    }

    public static MonitoringState MarkFailed(
        MonitoringState state,
        MonitoredUnitKey key,
        string? releaseKey,
        DateTimeOffset now,
        TimeSpan? baseDelay = null,
        int maxExponent = 5)
    {
        var attempts = CloneAttempts(state);
        var id = key.ToString();
        var existing = attempts.TryGetValue(id, out var found)
            ? found
            : new AcquisitionAttempt(key, AcquisitionAttemptStatus.None, null, 0, null, null);

        var failureCount = checked(existing.FailureCount + 1);
        var delay = baseDelay ?? TimeSpan.FromMinutes(5);
        var exponent = Math.Min(Math.Max(failureCount - 1, 0), maxExponent);
        var multiplier = Math.Pow(2, exponent);
        var retryDelay = TimeSpan.FromTicks((long)Math.Min(
            delay.Ticks * multiplier,
            TimeSpan.FromHours(6).Ticks));

        attempts[id] = existing with
        {
            Status = AcquisitionAttemptStatus.Failed,
            ReleaseKey = releaseKey ?? existing.ReleaseKey,
            FailureCount = failureCount,
            LastAttemptAtUtc = now,
            NextRetryAtUtc = now + retryDelay
        };

        return WithHistory(
            state,
            attempts,
            key,
            now,
            "failed",
            $"Retry after {retryDelay}.");
    }

    public static MonitoringState ClearAttempt(
        MonitoringState state,
        MonitoredUnitKey key,
        DateTimeOffset now,
        string reason)
    {
        var attempts = CloneAttempts(state);
        attempts.Remove(key.ToString());
        return WithHistory(state, attempts, key, now, "attempt-cleared", reason);
    }

    // A series-folder rename changes the library key; monitoring settings, wanted units, attempts
    // and history follow it. Returns the same instance when nothing changed.
    public static MonitoringState RekeyAnime(
        MonitoringState state,
        string oldKey,
        string newKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newKey);

        bool IsOld(string key) => key.Equals(oldKey, StringComparison.OrdinalIgnoreCase);
        MonitoredUnitKey Map(MonitoredUnitKey key) => IsOld(key.AnimeKey) ? key with { AnimeKey = newKey } : key;

        if (oldKey.Equals(newKey, StringComparison.Ordinal) ||
            (!state.Anime.Values.Any(item => IsOld(item.AnimeKey)) &&
             !state.Wanted.Values.Any(item => IsOld(item.Key.AnimeKey)) &&
             !state.Attempts.Values.Any(item => IsOld(item.Key.AnimeKey)) &&
             !state.History.Any(item => IsOld(item.Key.AnimeKey))))
        {
            return state;
        }

        var anime = state.Anime.Values
            .Select(item => IsOld(item.AnimeKey) ? item with { AnimeKey = newKey } : item)
            .ToDictionary(item => item.AnimeKey, StringComparer.OrdinalIgnoreCase);
        var wanted = state.Wanted.Values
            .Select(item => item with { Key = Map(item.Key) })
            .ToDictionary(item => item.Key.ToString(), StringComparer.OrdinalIgnoreCase);
        var attempts = state.Attempts.Values
            .Select(item => item with { Key = Map(item.Key) })
            .ToDictionary(item => item.Key.ToString(), StringComparer.OrdinalIgnoreCase);
        var history = state.History
            .Select(item => item with { Key = Map(item.Key) })
            .ToList();

        return state with
        {
            Anime = anime,
            Wanted = wanted,
            Attempts = attempts,
            History = history
        };
    }

    public static bool IsMonitored(MonitoringState state, MonitoredUnitKey key)
    {
        if (!state.Anime.TryGetValue(key.AnimeKey, out var settings))
        {
            return false;
        }

        // Whole-item units carry no season/episode, so overrides never apply: the work's own
        // monitored flag is the answer.
        if (key.Granularity == MonitoringGranularity.Item)
        {
            return settings.Monitored;
        }

        if (key.Granularity == MonitoringGranularity.Episode)
        {
            var episodeKey = EpisodeOverrideKey(key.SeasonNumber, key.EpisodeNumber);
            if (settings.EpisodeOverrides.TryGetValue(episodeKey, out var episodeOverride))
            {
                return episodeOverride;
            }
        }

        if (settings.SeasonOverrides.TryGetValue(key.SeasonNumber, out var seasonOverride))
        {
            return seasonOverride;
        }

        return settings.Monitored;
    }

    public static bool IsCutoffMet(
        QualityProfile profile,
        ReleaseScoreResult current) =>
        Selection.UpgradePolicy.IsCutoffMet(profile, current.QualityKey);

    public static string EpisodeOverrideKey(int season, int episode) =>
        $"S{season:00}E{episode:00}";

    // Whether a parsed release satisfies the wanted unit, per its granularity:
    //  - Item: the media type's search already found this release for the work, so any accepted
    //    release matches (a movie/book/audiobook has no season/episode numbering to check).
    //  - Season: the release must be a season pack for the same season.
    //  - Episode: the release's episode range (or absolute range) must cover the episode.
    private static bool MatchesUnit(MonitoredUnitKey key, ReleaseInfo release) => key.Granularity switch
    {
        MonitoringGranularity.Item => true,
        MonitoringGranularity.Season =>
            release.SeasonNumber == key.SeasonNumber &&
            (release.IsSeasonPack || release.EpisodeStart is null),
        _ => MatchesEpisode(key, release)
    };

    private static bool MatchesEpisode(MonitoredUnitKey key, ReleaseInfo release)
    {
        // A season pack names no episode range: it covers every episode of its season.
        if (release.IsSeasonPack && release.EpisodeStart is null && release.SeasonNumber is int packSeason)
        {
            return packSeason == key.SeasonNumber;
        }

        if (release.SeasonNumber is int season &&
            release.EpisodeStart is int start &&
            release.EpisodeEnd is int end)
        {
            return season == key.SeasonNumber &&
                   key.EpisodeNumber >= start &&
                   key.EpisodeNumber <= end;
        }

        if (key.AbsoluteEpisodeNumber is int absolute &&
            release.AbsoluteEpisodeStart is int absoluteStart &&
            release.AbsoluteEpisodeEnd is int absoluteEnd)
        {
            return absolute >= absoluteStart && absolute <= absoluteEnd;
        }

        return false;
    }

    private static Dictionary<string, AcquisitionAttempt> CloneAttempts(MonitoringState state) =>
        new(state.Attempts, StringComparer.OrdinalIgnoreCase);

    private static MonitoringState WithHistory(
        MonitoringState state,
        Dictionary<string, AcquisitionAttempt> attempts,
        MonitoredUnitKey key,
        DateTimeOffset now,
        string eventName,
        string reason)
    {
        var history = state.History.ToList();
        history.Add(new MonitoringHistoryEntry(now, key, eventName, reason));

        TrimHistory(history);

        return state with
        {
            Attempts = attempts,
            History = history
        };
    }

    private static void TrimHistory(List<MonitoringHistoryEntry> history)
    {
        const int maxHistory = 2_000;
        if (history.Count > maxHistory)
        {
            history.RemoveRange(0, history.Count - maxHistory);
        }
    }
}
