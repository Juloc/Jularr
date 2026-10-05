using System.Text.Json;

namespace Jularr.Web.Features.Playback.Transcoding;

/// <summary>
/// What one delivery costs the server. Each class has its own concurrent-session limit so a
/// cheap remux can never be starved by (or starve) CPU-bound software encodes.
/// </summary>
public enum PlaybackCostClass
{
    SoftwareVideo,
    HardwareVideo,
    Remux,
    AudioOnly
}

/// <summary>
/// The Admin-editable server resource policy of playback (#403): whether the server may
/// transcode, how many sessions each cost class may run, and where and how large the HLS cache
/// may grow. Defaults keep a modest low-core server at one software transcode plus remux.
/// </summary>
public sealed record PlaybackTranscodingSettings(
    bool TranscodingEnabled,
    int SoftwareVideoSessions,
    int HardwareVideoSessions,
    int RemuxSessions,
    int AudioOnlySessions,
    string HlsCachePath,
    long CacheBudgetBytes,
    long FreeSpaceFloorBytes)
{
    public const string DefaultHlsCachePath = "/data/playback-cache/hls";
    public const int MaxSessionsPerClass = 64;
    public const long BytesPerGiB = 1L << 30;
    // The Admin form offers whole GiB (so at least 1); the stored rule only refuses a budget too small to hold a single segment.
    public const long MinCacheBudgetGiB = 1;
    public const long MinCacheBudgetBytes = 1L << 20;
    public const long MaxCacheBudgetGiB = 10_240;
    public const long MaxFreeSpaceFloorGiB = 10_240;

    public static PlaybackTranscodingSettings Default { get; } = new(
        TranscodingEnabled: true,
        SoftwareVideoSessions: 2,
        HardwareVideoSessions: 4,
        RemuxSessions: 6,
        AudioOnlySessions: 8,
        HlsCachePath: DefaultHlsCachePath,
        CacheBudgetBytes: 10 * BytesPerGiB,
        FreeSpaceFloorBytes: 5 * BytesPerGiB);

    public int LimitFor(PlaybackCostClass costClass) =>
        costClass switch
        {
            PlaybackCostClass.SoftwareVideo => SoftwareVideoSessions,
            PlaybackCostClass.HardwareVideo => HardwareVideoSessions,
            PlaybackCostClass.Remux => RemuxSessions,
            PlaybackCostClass.AudioOnly => AudioOnlySessions,
            _ => throw new ArgumentOutOfRangeException(nameof(costClass))
        };
}

/// <summary>One rejected setting: the field name and a stable code the Admin page maps to text.</summary>
public sealed record PlaybackSettingsIssue(string Field, string Code);

public static class PlaybackTranscodingSettingsRules
{
    public const string PathRequired = "path_required";
    public const string PathNotAbsolute = "path_not_absolute";
    public const string PathTraversal = "path_traversal";
    public const string PathInvalid = "path_invalid";
    public const string PathNotWritable = "path_not_writable";
    public const string LimitRange = "limit_range";
    public const string BudgetRange = "budget_range";
    public const string FloorRange = "floor_range";

    /// <summary>
    /// The syntactic rules of the settings. The path may not climb out of where the administrator
    /// pointed it: every segment is checked on the raw text because normalizing first would
    /// silently turn <c>/data/../etc</c> into a valid-looking path.
    /// </summary>
    public static IReadOnlyList<PlaybackSettingsIssue> Validate(PlaybackTranscodingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<PlaybackSettingsIssue>();
        foreach (var costClass in Enum.GetValues<PlaybackCostClass>())
        {
            if (settings.LimitFor(costClass) is < 0 or > PlaybackTranscodingSettings.MaxSessionsPerClass)
            {
                issues.Add(new PlaybackSettingsIssue(costClass.ToString(), LimitRange));
            }
        }

        var budget = settings.CacheBudgetBytes;
        if (budget < PlaybackTranscodingSettings.MinCacheBudgetBytes ||
            budget > PlaybackTranscodingSettings.MaxCacheBudgetGiB * PlaybackTranscodingSettings.BytesPerGiB)
        {
            issues.Add(new PlaybackSettingsIssue(nameof(PlaybackTranscodingSettings.CacheBudgetBytes), BudgetRange));
        }

        var floor = settings.FreeSpaceFloorBytes;
        if (floor < 0 || floor > PlaybackTranscodingSettings.MaxFreeSpaceFloorGiB * PlaybackTranscodingSettings.BytesPerGiB)
        {
            issues.Add(new PlaybackSettingsIssue(nameof(PlaybackTranscodingSettings.FreeSpaceFloorBytes), FloorRange));
        }

        if (ValidatePath(settings.HlsCachePath) is { } pathIssue)
        {
            issues.Add(new PlaybackSettingsIssue(nameof(PlaybackTranscodingSettings.HlsCachePath), pathIssue));
        }

        return issues;
    }

    public static string? ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathRequired;
        }

        var trimmed = path.Trim();
        if (trimmed.Any(character => character == '\0' || char.IsControl(character)) || trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return PathInvalid;
        }

        if (!trimmed.StartsWith('/') && !Path.IsPathFullyQualified(trimmed))
        {
            return PathNotAbsolute;
        }

        if (trimmed.Split('/', '\\').Any(segment => segment == ".."))
        {
            return PathTraversal;
        }

        // A filesystem root would make the cache sweeper treat unrelated top-level folders as sessions.
        var full = Path.GetFullPath(trimmed);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal) ? PathInvalid : null;
    }

    /// <summary>The canonical stored form of a validated path: trimmed, without a trailing separator.</summary>
    public static string NormalizePath(string path) => path.Trim().TrimEnd('/', '\\');
}

/// <summary>Why a save was refused, or the settings that were stored.</summary>
public sealed record PlaybackSettingsSaveResult(PlaybackTranscodingSettings? Saved, IReadOnlyList<PlaybackSettingsIssue> Issues)
{
    public bool Succeeded => Saved is not null;
}

/// <summary>
/// The one canonical store of the playback resource policy. It is durable-but-not-relational
/// configuration, so it uses the same JSON settings-store pattern under <c>/data</c> as the
/// other instance settings rather than an EF table. <see cref="Current"/> is the in-memory view
/// that synchronous consumers (slot admission, the HLS cache) read; it equals the stored value
/// once <see cref="LoadAsync"/> ran at startup and after every successful save.
/// </summary>
public sealed class PlaybackTranscodingSettingsStore
{
    public const string FileName = "transcoding.json";

    private static readonly JsonSerializerOptions s_jsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private PlaybackTranscodingSettings _current = PlaybackTranscodingSettings.Default;

    public PlaybackTranscodingSettingsStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(dataRoot, "playback", FileName);
    }

    public PlaybackTranscodingSettings Current => Volatile.Read(ref _current);

    public async Task<PlaybackTranscodingSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await ReadAsync(cancellationToken);
            Volatile.Write(ref _current, loaded);
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates, proves the cache folder is writable, then stores the settings atomically. Nothing
    /// is stored and <see cref="Current"/> is unchanged when any rule fails.
    /// </summary>
    public async Task<PlaybackSettingsSaveResult> SaveAsync(PlaybackTranscodingSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with { HlsCachePath = PlaybackTranscodingSettingsRules.NormalizePath(settings.HlsCachePath ?? "") };
        var issues = PlaybackTranscodingSettingsRules.Validate(normalized).ToList();
        if (issues.Count == 0 && !IsWritableDirectory(normalized.HlsCachePath))
        {
            issues.Add(new PlaybackSettingsIssue(nameof(PlaybackTranscodingSettings.HlsCachePath), PlaybackTranscodingSettingsRules.PathNotWritable));
        }

        if (issues.Count > 0)
        {
            return new PlaybackSettingsSaveResult(null, issues);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = $"{_path}.tmp-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(Persisted.From(normalized), s_jsonOptions), cancellationToken);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            Volatile.Write(ref _current, normalized);
            return new PlaybackSettingsSaveResult(normalized, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PlaybackTranscodingSettings> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return PlaybackTranscodingSettings.Default;
        }

        Persisted? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<Persisted>(await File.ReadAllTextAsync(_path, cancellationToken), s_jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Playback transcoding settings '{_path}' are invalid JSON.", exception);
        }

        var settings = persisted?.ToSettings() ?? PlaybackTranscodingSettings.Default;
        var issues = PlaybackTranscodingSettingsRules.Validate(settings);
        if (issues.Count > 0)
        {
            var fields = string.Join(", ", issues.Select(issue => $"{issue.Field}: {issue.Code}"));
            throw new InvalidDataException($"Playback transcoding settings '{_path}' are invalid ({fields}).");
        }

        return settings with { HlsCachePath = PlaybackTranscodingSettingsRules.NormalizePath(settings.HlsCachePath) };
    }

    // A marker file is the only way to know the process may really write there, not just list it.
    private static bool IsWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, $".jularr-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(marker, []);
            File.Delete(marker);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // Absent properties keep their default, so a field added in a later build never invalidates an older file.
    private sealed record Persisted(
        bool? TranscodingEnabled,
        int? SoftwareVideoSessions,
        int? HardwareVideoSessions,
        int? RemuxSessions,
        int? AudioOnlySessions,
        string? HlsCachePath,
        long? CacheBudgetBytes,
        long? FreeSpaceFloorBytes)
    {
        public static Persisted From(PlaybackTranscodingSettings settings) =>
            new(
                settings.TranscodingEnabled,
                settings.SoftwareVideoSessions,
                settings.HardwareVideoSessions,
                settings.RemuxSessions,
                settings.AudioOnlySessions,
                settings.HlsCachePath,
                settings.CacheBudgetBytes,
                settings.FreeSpaceFloorBytes);

        public PlaybackTranscodingSettings ToSettings()
        {
            var defaults = PlaybackTranscodingSettings.Default;
            return new PlaybackTranscodingSettings(
                TranscodingEnabled ?? defaults.TranscodingEnabled,
                SoftwareVideoSessions ?? defaults.SoftwareVideoSessions,
                HardwareVideoSessions ?? defaults.HardwareVideoSessions,
                RemuxSessions ?? defaults.RemuxSessions,
                AudioOnlySessions ?? defaults.AudioOnlySessions,
                HlsCachePath ?? defaults.HlsCachePath,
                CacheBudgetBytes ?? defaults.CacheBudgetBytes,
                FreeSpaceFloorBytes ?? defaults.FreeSpaceFloorBytes);
        }
    }
}
