using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Monitoring;

// Media-type-agnostic monitoring model. Monitoring used to be anime-only; it is now the shared
// engine every request-independent media type plugs into (anime is the first registration, see
// AnimeAcquisitionRegistration / MediaAcquisitionRegistry.MonitoringGranularityFor). A media type
// tells the engine at which granularity it monitors — the whole item (a movie, book or audiobook),
// a season (a season pack) or a single episode (anime/TV) — and the engine's wanted/search/grab
// logic works the same across all three. The historical Anime* type names remain as compile-time
// aliases (see MonitoringAliases.cs); a field/name rename off the "Anime" prefix is deferred to keep
// the anime pipeline and its JSON state byte-compatible (parity), matching the AnimeImportSettingsState
// precedent.

/// <summary>The level a media type is monitored and searched at.</summary>
public enum MonitoringGranularity
{
    /// <summary>The whole work is one unit: a movie, a book, an audiobook.</summary>
    Item,

    /// <summary>One season is one unit: a season pack for a series.</summary>
    Season,

    /// <summary>One episode is one unit: anime and TV episodes.</summary>
    Episode
}

public enum WantedReason
{
    Missing,
    CutoffUnmet
}

public enum MonitoringSearchTrigger
{
    SearchOnAdd,
    PeriodicMissing,
    Rss,
    Manual
}

public enum AcquisitionAttemptStatus
{
    None,
    Pending,
    Grabbed,
    Failed
}

// Identifies one monitored unit of any media type. AnimeKey is the work/library key (an anime key
// today; for a movie/book/audiobook it is that work's key) — the anime-era field name is kept so the
// serialized state and every acquisition-pipeline caller stay unchanged; a rename is deferred.
// SeasonNumber/EpisodeNumber/AbsoluteEpisodeNumber apply at Season/Episode granularity and are 0/null
// for a whole-item unit. Use the ForItem/ForSeason/ForEpisode factories from generic (non-anime) code
// so the granularity is set correctly; the positional constructor defaults to Episode so existing
// anime call sites (new AnimeEpisodeKey(key, season, episode[, absolute])) are unchanged.
public sealed record MonitoredUnitKey(
    string AnimeKey,
    int SeasonNumber,
    int EpisodeNumber,
    int? AbsoluteEpisodeNumber = null,
    MonitoringGranularity Granularity = MonitoringGranularity.Episode)
{
    public override string ToString() => $"{AnimeKey}:S{SeasonNumber:00}E{EpisodeNumber:00}";

    /// <summary>A whole-item unit (movie, book, audiobook): no season or episode.</summary>
    public static MonitoredUnitKey ForItem(string itemKey) =>
        new(itemKey, 0, 0, null, MonitoringGranularity.Item);

    /// <summary>A season unit (a season pack) of a series.</summary>
    public static MonitoredUnitKey ForSeason(string itemKey, int seasonNumber) =>
        new(itemKey, seasonNumber, 0, null, MonitoringGranularity.Season);

    /// <summary>A single episode unit (anime/TV).</summary>
    public static MonitoredUnitKey ForEpisode(
        string itemKey,
        int seasonNumber,
        int episodeNumber,
        int? absoluteEpisodeNumber = null) =>
        new(itemKey, seasonNumber, episodeNumber, absoluteEpisodeNumber, MonitoringGranularity.Episode);
}

// IndexerIds restricts automatic and interactive searches for this work to the given indexer ids;
// null or empty uses the global indexer selection. TargetRootId is the library root new imports go to
// when the work has no folder yet; null defaults to the work's current root (or the first enabled root).
public sealed record MonitorSettings(
    string AnimeKey,
    bool Monitored,
    bool SearchOnAdd,
    Dictionary<int, bool> SeasonOverrides,
    Dictionary<string, bool> EpisodeOverrides,
    int[]? IndexerIds = null,
    Guid? TargetRootId = null);

// The one scheduler setting for periodic monitoring runs.
public sealed record MonitoringSchedule(
    bool Enabled,
    int IntervalMinutes)
{
    public const int MinimumIntervalMinutes = 5;
    public const int MaximumIntervalMinutes = 24 * 60;

    public static MonitoringSchedule Default { get; } = new(true, 30);

    public TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Clamp(IntervalMinutes, MinimumIntervalMinutes, MaximumIntervalMinutes));
}

public sealed record MonitoredUnitInventory(
    MonitoredUnitKey Key,
    DateTimeOffset? AirsAtUtc,
    bool HasFile,
    ReleaseScoreResult? CurrentFile);

public sealed record WantedUnit(
    MonitoredUnitKey Key,
    WantedReason Reason,
    DateTimeOffset BecameWantedAtUtc);

public sealed record MonitoringSearchRequest(
    MonitoredUnitKey Key,
    WantedReason Reason,
    MonitoringSearchTrigger Trigger);

public sealed record AutoGrabDecision(
    bool Grab,
    string Reason,
    ReleaseScoreResult Candidate);

public sealed record AcquisitionAttempt(
    MonitoredUnitKey Key,
    AcquisitionAttemptStatus Status,
    string? ReleaseKey,
    int FailureCount,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? NextRetryAtUtc);

public sealed record MonitoringHistoryEntry(
    DateTimeOffset AtUtc,
    MonitoredUnitKey Key,
    string Event,
    string Reason);

public sealed record MonitoringState(
    int Version,
    Dictionary<string, MonitorSettings> Anime,
    Dictionary<string, WantedUnit> Wanted,
    Dictionary<string, AcquisitionAttempt> Attempts,
    List<MonitoringHistoryEntry> History)
{
    public MonitoringSchedule Schedule { get; init; } = MonitoringSchedule.Default;

    public static MonitoringState Empty() =>
        new(
            1,
            new Dictionary<string, MonitorSettings>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, WantedUnit>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, AcquisitionAttempt>(StringComparer.OrdinalIgnoreCase),
            []);
}
