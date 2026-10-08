using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;

namespace Jularr.Web.Features.Library;

/// <summary>Where an episode stands on Admin → Media detail: what is in the library and what acquisition is doing about the rest.</summary>
public enum AdminMediaState
{
    Available,
    Missing,
    Searching,
    Downloading,
    Importing,
    Failed
}

/// <summary>How much of a season (or group) is monitored.</summary>
public enum AdminMediaMonitoring
{
    Off,
    Partial,
    On
}

public sealed record AdminMediaTrack(string? Language, string? Codec, int? Channels, bool IsDefault, bool IsForced);

public sealed record AdminMediaSidecar(string Language, string Format, bool Forced, bool Sdh);

/// <summary>One local file of an episode, from the stored media rows and analysis; nothing here touches the disk.</summary>
/// <param name="Analysis">Null when the file has not been analysed yet.</param>
public sealed record AdminMediaFile(
    Guid Id,
    string Name,
    string Location,
    long SizeBytes,
    DateTime AddedUtc,
    MediaAnalysisStatus? Analysis,
    string? Diagnostic,
    string? Container,
    string? VideoCodec,
    string? Quality,
    IReadOnlyList<AdminMediaTrack> Audio,
    IReadOnlyList<AdminMediaTrack> Subtitles);

/// <summary>One canonical episode with its merged operational state and its real local files.</summary>
public sealed record AdminMediaEpisode(
    int Season,
    int Number,
    Guid? Id,
    string Title,
    bool Monitored,
    AdminMediaState State,
    bool UpgradeWanted,
    int Failures,
    string? Quality,
    IReadOnlyList<string> AudioLanguages,
    IReadOnlyList<string> SubtitleLanguages,
    IReadOnlyList<AdminMediaFile> Files,
    IReadOnlyList<AdminMediaSidecar> Sidecars)
{
    public long SizeBytes => Files.Sum(file => file.SizeBytes);

    public bool HasFiles => Files.Count > 0;

    /// <summary>The address part of the episode, for example <c>1x3</c>.</summary>
    public string Key => AdminMediaDetailView.EpisodeKey(Season, Number);
}

/// <summary>A season, the specials, or an AniList entry: a display grouping over canonical episodes.</summary>
/// <param name="Season">The season number of a standard group; null for an AniList group.</param>
/// <param name="Title">The AniList entry title of an AniList group; null for a season or when nothing is matched.</param>
public sealed record AdminMediaGroup(string Key, int? Season, string? Title, IReadOnlyList<AdminMediaEpisode> Episodes)
{
    public int Available => Episodes.Count(episode => episode.HasFiles);

    public int Missing => Episodes.Count - Available;

    public long SizeBytes => Episodes.Sum(episode => episode.SizeBytes);

    public AdminMediaMonitoring Monitoring => AdminMediaDetailView.MonitoringOf(Episodes);

    /// <summary>The audio languages of the group with how many episodes carry each, most common first.</summary>
    public IReadOnlyList<(string Language, int Count)> AudioCoverage =>
        AdminMediaDetailView.Coverage(Episodes.Select(episode => episode.AudioLanguages));
}

public sealed record AdminMediaAcquisition(
    AnimeManagementMode Mode,
    bool Monitored,
    bool SearchOnAdd,
    string ProfileId,
    string ProfileName,
    IReadOnlyList<(string Id, string Name)> Profiles,
    int[] IndexerIds,
    Guid? TargetRootId,
    int WantedCount)
{
    public bool CanAcquire => Mode != AnimeManagementMode.ReadOnlyCoexistence;
}

/// <summary>The provider match and explicit episode ranges of the anime. <paramref name="RangesFailed"/> when they could not be read.</summary>
public sealed record AdminMediaMapping(
    string? Provider,
    string? ExternalId,
    string? Title,
    IReadOnlyList<AnimeEpisodeMetadataMapping> Ranges,
    bool RangesFailed);

public sealed record AdminMediaImport(AnimeImportStatus Status, string? Message, DateTimeOffset UpdatedAtUtc, int FileCount);

/// <summary>Everything Admin → Media detail shows for one anime.</summary>
/// <param name="ActivityFailed">The history or operations could not be read; the rest of the page still shows.</param>
public sealed record AdminMediaDetail(
    Guid Id,
    string Key,
    string Title,
    AnimeMetadata? Metadata,
    string? PosterUrl,
    IReadOnlyList<AdminMediaEpisode> Episodes,
    AdminMediaAcquisition Acquisition,
    AdminMediaMapping Mapping,
    IReadOnlyList<AdminMediaImport> Imports,
    IReadOnlyList<AcquisitionHistoryEntry> History,
    IReadOnlyList<OperationSnapshot> Operations,
    bool ActivityFailed)
{
    public long SizeBytes => Episodes.Sum(episode => episode.SizeBytes);

    public int Available => Episodes.Count(episode => episode.HasFiles);

    public int Missing => Episodes.Count - Available;

    public int Seasons => Episodes.Select(episode => episode.Season).Where(season => season > 0).Distinct().Count();

    public int Monitored => Episodes.Count(episode => episode.Monitored);

    /// <summary>The share of episodes with a file, 0-100.</summary>
    public int CoveragePercent => Episodes.Count == 0 ? 0 : (int)Math.Floor(Available * 100d / Episodes.Count);

    public IReadOnlyList<string> AudioLanguages =>
        [.. Episodes.SelectMany(episode => episode.AudioLanguages).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>Pure decisions behind Admin → Media detail: grouping, monitoring state and the compact technical labels.</summary>
public static class AdminMediaDetailView
{
    public const string AniListView = "anilist";

    /// <summary>Whether the address asks for the AniList grouping; anything else is the standard grouping.</summary>
    public static bool IsAniList(string? view) =>
        string.Equals(view?.Trim(), AniListView, StringComparison.OrdinalIgnoreCase);

    public static string EpisodeKey(int season, int number) => $"{season}x{number}";

    public static AdminMediaMonitoring MonitoringOf(IReadOnlyList<AdminMediaEpisode> episodes)
    {
        var monitored = episodes.Count(episode => episode.Monitored);
        return monitored == 0 ? AdminMediaMonitoring.Off
            : monitored == episodes.Count ? AdminMediaMonitoring.On
            : AdminMediaMonitoring.Partial;
    }

    /// <summary>The standard grouping: one group per season, the specials last.</summary>
    public static IReadOnlyList<AdminMediaGroup> GroupBySeason(IReadOnlyList<AdminMediaEpisode> episodes) =>
        [.. episodes
            .GroupBy(episode => episode.Season)
            .OrderBy(group => group.Key == 0 ? 1 : 0)
            .ThenBy(group => group.Key)
            .Select(group => new AdminMediaGroup($"s{group.Key}", group.Key, null, [.. group.OrderBy(episode => episode.Number)]))];

    /// <summary>
    /// The AniList grouping: episodes of one explicitly mapped AniList entry together, everything else with the
    /// matched entry. Display only; every episode stays the same canonical episode.
    /// </summary>
    public static IReadOnlyList<AdminMediaGroup> GroupByAniList(
        IReadOnlyList<AdminMediaEpisode> episodes,
        IReadOnlyList<AnimeEpisodeMetadataMapping> ranges,
        string? matchedTitle)
    {
        var groups = new List<(string Key, string? Title, List<AdminMediaEpisode> Items)>();
        foreach (var episode in episodes)
        {
            var range = ranges.FirstOrDefault(candidate => candidate.Contains(episode.Season, episode.Number));
            var key = range is null ? "main" : $"a-{range.Provider}-{range.ExternalId}";
            var title = range?.PreferredTitle ?? matchedTitle;
            var index = groups.FindIndex(group => group.Key == key);
            if (index < 0)
            {
                groups.Add((key, title, [episode]));
            }
            else
            {
                groups[index].Items.Add(episode);
            }
        }

        return [.. groups.Select(group => new AdminMediaGroup(group.Key, null, group.Title, group.Items))];
    }

    /// <summary>How many of the given episodes carry each language, most common first, then by code.</summary>
    public static IReadOnlyList<(string Language, int Count)> Coverage(IEnumerable<IReadOnlyList<string>> languagesPerEpisode) =>
        [.. languagesPerEpisode
            .SelectMany(languages => languages.Distinct(StringComparer.Ordinal))
            .GroupBy(language => language, StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(item => item.Item2)
            .ThenBy(item => item.Key, StringComparer.Ordinal)];

    /// <summary>The state of an episode: a file wins; otherwise the last acquisition attempt says what is happening.</summary>
    public static AdminMediaState StateOf(bool hasFile, AcquisitionAttemptStatus? attempt) =>
        hasFile ? AdminMediaState.Available : attempt switch
        {
            AcquisitionAttemptStatus.Pending => AdminMediaState.Searching,
            AcquisitionAttemptStatus.Grabbed => AdminMediaState.Downloading,
            AcquisitionAttemptStatus.Failed => AdminMediaState.Failed,
            _ => AdminMediaState.Missing
        };

    public static string StateName(AdminMediaState state) => state switch
    {
        AdminMediaState.Available => "available",
        AdminMediaState.Searching => "searching",
        AdminMediaState.Downloading => "downloading",
        AdminMediaState.Importing => "importing",
        AdminMediaState.Failed => "failed",
        _ => "missing"
    };

    /// <summary>
    /// The resolution of a video as users know it (<c>1080p</c>); a cropped wide frame still counts by its width.
    /// HDR video carries the dynamic range: <c>2160p HDR10</c>.
    /// </summary>
    public static string? QualityLabel(int? width, int? height, string? dynamicRange)
    {
        var effective = Math.Max(height ?? 0, (width ?? 0) * 9 / 16);
        if (effective <= 0)
        {
            return null;
        }

        var resolution = effective switch
        {
            >= 2160 => "2160p",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720 => "720p",
            >= 576 => "576p",
            >= 480 => "480p",
            _ => $"{effective}p"
        };
        var range = dynamicRange?.Trim();
        return string.IsNullOrEmpty(range) || range.Equals("sdr", StringComparison.OrdinalIgnoreCase)
            ? resolution
            : $"{resolution} {range}";
    }

    /// <summary>The better of two quality labels by resolution; null loses.</summary>
    public static string? BestQuality(string? left, string? right) =>
        Rank(left) >= Rank(right) ? left ?? right : right;

    private static int Rank(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return -1;
        }

        var digits = new string(quality.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    public static string CodecLabel(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => "",
        "h264" or "avc" or "avc1" => "H.264",
        "hevc" or "h265" => "HEVC",
        var other => other.ToUpperInvariant()
    };

    /// <summary>The channel layout of an audio track as it is usually written: 2.0, 5.1, 7.1.</summary>
    public static string ChannelLabel(int? channels) => channels switch
    {
        null or <= 0 => "",
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        var other => $"{other}ch"
    };

    /// <summary>The short text of a track: <c>AAC 2.0</c>.</summary>
    public static string TrackLabel(AdminMediaTrack track) =>
        string.Join(' ', new[] { CodecLabel(track.Codec), ChannelLabel(track.Channels) }.Where(part => part.Length > 0));

    /// <summary>The file name of a stored path, whichever separator it was stored with.</summary>
    public static string FileName(string path)
    {
        var index = path.LastIndexOfAny(['/', '\\']);
        return index < 0 ? path : path[(index + 1)..];
    }

    /// <summary>The folder of a stored path below its library root, for example <c>Frieren/Season 1</c>.</summary>
    public static string Folder(string rootPath, string path)
    {
        var folder = path.LastIndexOfAny(['/', '\\']) is var index and >= 0 ? path[..index] : "";
        var root = rootPath.TrimEnd('/', '\\');
        if (root.Length > 0 && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            folder = folder[root.Length..];
        }

        return folder.Trim('/', '\\').Replace('\\', '/');
    }
}

/// <summary>
/// Changes to the season and episode overrides of the monitoring settings. An override is stored only when it
/// differs from what the unit inherits, so switching back to the inherited value removes it.
/// </summary>
public static class AdminMediaMonitoringEdit
{
    public static MonitorSettings Empty(string animeKey) =>
        new(animeKey, false, true, [], new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Monitors or unmonitors a whole season; single-episode overrides inside it no longer apply.</summary>
    public static MonitorSettings SetSeason(MonitorSettings settings, int season, bool monitored)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var seasons = new Dictionary<int, bool>(settings.SeasonOverrides);
        if (monitored == settings.Monitored)
        {
            seasons.Remove(season);
        }
        else
        {
            seasons[season] = monitored;
        }

        var prefix = MonitoringEngine.EpisodeOverrideKey(season, 0)[..^2];
        var episodes = settings.EpisodeOverrides
            .Where(item => !item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        return settings with { SeasonOverrides = seasons, EpisodeOverrides = episodes };
    }

    /// <summary>Monitors or unmonitors one episode.</summary>
    public static MonitorSettings SetEpisode(MonitorSettings settings, int season, int episode, bool monitored)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var inherited = settings.SeasonOverrides.TryGetValue(season, out var seasonValue) ? seasonValue : settings.Monitored;
        var episodes = new Dictionary<string, bool>(settings.EpisodeOverrides, StringComparer.OrdinalIgnoreCase);
        var key = MonitoringEngine.EpisodeOverrideKey(season, episode);
        if (monitored == inherited)
        {
            episodes.Remove(key);
        }
        else
        {
            episodes[key] = monitored;
        }

        return settings with { EpisodeOverrides = episodes };
    }
}
