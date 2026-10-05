using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback;

public sealed record HlsPlaybackSession(
    Guid SessionId,
    Guid EpisodeId,
    string ProfileId,
    double StartSeconds,
    DateTimeOffset CreatedAtUtc,
    int? AudioStreamIndex = null,
    PlaybackQualityCap QualityCap = PlaybackQualityCap.Auto);

public sealed record HlsPlaybackAsset(
    string Path,
    string ContentType,
    bool EnableRangeProcessing);

/// <summary>What one cache sweep removed.</summary>
public sealed record HlsSweepResult(int ExpiredSessions, int PrunedForPolicy, int OrphanDirectories);

/// <summary>One running ffmpeg of an HLS session; the seam that lets the cache rules be tested without starting ffmpeg.</summary>
public interface IHlsEncoderProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>The last line ffmpeg printed to stderr, bounded; the reason an early exit is reported with.</summary>
    string ErrorSummary { get; }

    void Kill();
}

public delegate IHlsEncoderProcess HlsProcessStarter(IReadOnlyList<string> arguments);

/// <summary>
/// The owner of the HLS cache: the session directories under the configured cache path, their
/// ffmpeg processes and the cache policy (idle expiry, global budget, free-space floor). Cleanup
/// does not depend on playback requests: <see cref="PlaybackServerResourceService"/> calls
/// <see cref="Sweep"/> periodically.
/// </summary>
public sealed class HlsPlaybackSessionManager : IDisposable
{
    public const int SegmentSeconds = 4;
    public const int PlaylistSegments = 12;
    public const int MaxSessionsPerProfile = 2;
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A session untouched for this long may be dropped to make room under the cache budget. A
    /// playing session reads a segment every few seconds, so a minute of silence means a paused
    /// or abandoned player; the idle lifetime itself is far too long to relieve a full cache.
    /// </summary>
    public static readonly TimeSpan BudgetPruneIdleAfter = TimeSpan.FromMinutes(1);

    private static readonly Regex s_segmentPattern =
        new("^segment-[0-9]{5}\\.m4s$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_sessionDirectoryPattern =
        new("^[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();
    private readonly PlaybackTranscodingSettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly HlsProcessStarter _startProcess;
    private readonly Func<string, long?> _freeSpace;
    private readonly ILogger<HlsPlaybackSessionManager> _logger;
    private bool _disposed;

    public HlsPlaybackSessionManager(
        PlaybackTranscodingSettingsStore settings,
        TimeProvider time,
        ILogger<HlsPlaybackSessionManager> logger,
        HlsProcessStarter? startProcess = null,
        Func<string, long?>? freeSpace = null)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
        _startProcess = startProcess ?? FfmpegHlsProcess.Start;
        _freeSpace = freeSpace ?? (path => AdminServerLoad.VolumeSpace(path).Free);
    }

    public int ActiveSessions => _sessions.Count;

    public async Task<HlsPlaybackSession> StartAsync(
        Guid episodeId,
        string profileId,
        string sourcePath,
        double startSeconds,
        CancellationToken cancellationToken,
        int? audioStreamIndex = null,
        PlaybackQualityCap qualityCap = PlaybackQualityCap.Auto,
        IDisposable? lease = null)
    {
        var session = await StartAsync(
            episodeId,
            profileId,
            startSeconds,
            directory => BuildArguments(sourcePath, directory, startSeconds, audioStreamIndex, qualityCap),
            lease,
            cancellationToken);
        return session with { AudioStreamIndex = audioStreamIndex, QualityCap = qualityCap };
    }

    /// <summary>
    /// Starts one bounded HLS session whose ffmpeg arguments the caller builds for the
    /// session directory (a playback plan's remux or transcode). The optional lease (a
    /// transcode slot) is released when the session ends, also when it never starts. A full
    /// cache refuses the session with a <see cref="PlaybackAdmissionRefusedException"/>.
    /// </summary>
    public async Task<HlsPlaybackSession> StartAsync(
        Guid episodeId,
        string profileId,
        double startSeconds,
        Func<string, IReadOnlyList<string>> buildArguments,
        IDisposable? lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildArguments);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            lease?.Dispose();
            throw new ArgumentException("Profile ID is required.", nameof(profileId));
        }

        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            lease?.Dispose();
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        }

        ThrowIfDisposed();
        CleanupExpired();

        Entry entry;
        try
        {
            lock (_gate)
            {
                EvictForProfileCapacity(profileId);

                var policy = _settings.Current;
                Directory.CreateDirectory(policy.HlsCachePath);
                if (PruneToPolicy(policy) is { } refusal)
                {
                    throw new PlaybackAdmissionRefusedException(refusal);
                }

                var sessionId = Guid.NewGuid();
                var directory = Path.Combine(policy.HlsCachePath, sessionId.ToString("N"));
                Directory.CreateDirectory(directory);

                var process = _startProcess(buildArguments(directory));
                var now = _time.GetUtcNow();
                entry = new Entry(sessionId, episodeId, profileId, directory, process, startSeconds, now, lease);
                _sessions[sessionId] = entry;
            }
        }
        catch
        {
            lease?.Dispose();
            throw;
        }

        try
        {
            await WaitForPlaylistAsync(entry, cancellationToken);
        }
        catch
        {
            Remove(entry.SessionId);
            throw;
        }

        return new HlsPlaybackSession(
            entry.SessionId,
            entry.EpisodeId,
            entry.ProfileId,
            entry.StartSeconds,
            entry.CreatedAtUtc);
    }

    /// <summary>Segments kept behind the most recently requested one (32 s at 4 s segments).</summary>
    public const int SegmentsKeptBehind = 8;

    /// <summary>
    /// Deletes the segments well behind the one a player just requested. Players of a
    /// playback plan restart the stream to seek, so only the recent past is ever read again;
    /// this bounds the disk use of an append-only (EVENT) playlist.
    /// </summary>
    public int PruneBehind(Guid sessionId, string profileId, string fileName)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) ||
            !string.Equals(entry.ProfileId, profileId, StringComparison.Ordinal) ||
            !s_segmentPattern.IsMatch(fileName) ||
            !int.TryParse(fileName.AsSpan(8, 5), NumberStyles.None, CultureInfo.InvariantCulture, out var requested))
        {
            return 0;
        }

        var deleted = 0;
        for (var number = requested - SegmentsKeptBehind; number >= 0; number--)
        {
            var path = Path.Combine(
                entry.DirectoryPath,
                $"segment-{number.ToString("00000", CultureInfo.InvariantCulture)}.m4s");
            if (!File.Exists(path))
            {
                break;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        return deleted;
    }

    public bool IsActive(Guid sessionId, string profileId) =>
        _sessions.TryGetValue(sessionId, out var entry) &&
        string.Equals(entry.ProfileId, profileId, StringComparison.Ordinal);

    /// <summary>Ends a session early (a client stopped or switched streams).</summary>
    public void Stop(Guid sessionId, string profileId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry) &&
            string.Equals(entry.ProfileId, profileId, StringComparison.Ordinal))
        {
            Remove(sessionId);
        }
    }

    public HlsPlaybackAsset? GetAsset(
        Guid sessionId,
        Guid episodeId,
        string profileId,
        string fileName)
    {
        ThrowIfDisposed();
        CleanupExpired();

        if (!_sessions.TryGetValue(sessionId, out var entry) ||
            entry.EpisodeId != episodeId ||
            !string.Equals(entry.ProfileId, profileId, StringComparison.Ordinal) ||
            !AllowedAsset(fileName))
        {
            return null;
        }

        var path = Path.Combine(entry.DirectoryPath, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        entry.Touch(_time.GetUtcNow());

        return new HlsPlaybackAsset(
            path,
            ContentType(fileName),
            fileName != "index.m3u8");
    }

    public int CleanupExpired()
    {
        var cutoff = _time.GetUtcNow() - IdleLifetime;
        var removed = 0;
        foreach (var entry in _sessions.Values)
        {
            if (entry.LastAccessUtc < cutoff)
            {
                Remove(entry.SessionId);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// One pass of the background cleanup: expires idle sessions, drops idle sessions while the cache
    /// is over its budget or the volume under its free-space floor, and removes session directories
    /// no live session owns (left behind by a crashed process).
    /// </summary>
    public HlsSweepResult Sweep()
    {
        ThrowIfDisposed();
        var expired = CleanupExpired();
        lock (_gate)
        {
            var before = _sessions.Count;
            var refusal = PruneToPolicy(_settings.Current);
            if (refusal is not null)
            {
                _logger.LogWarning("The HLS cache cannot get under its policy by dropping idle sessions: {Reason}.", refusal);
            }

            return new HlsSweepResult(expired, before - _sessions.Count, RemoveOrphanDirectories(_settings.Current.HlsCachePath));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            Remove(sessionId);
        }
    }

    /// <summary>
    /// HLS fallback always encodes H.264, so the quality cap always applies
    /// here; <paramref name="audioStreamIndex"/> keeps the caller's audio
    /// track selection across the fallback restart.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        string sourcePath,
        string directory,
        double startSeconds,
        int? audioStreamIndex = null,
        PlaybackQualityCap qualityCap = PlaybackQualityCap.Auto)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        }

        if (audioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioStreamIndex));
        }

        var playlistPath = Path.Combine(directory, "index.m3u8");
        var segmentPath = Path.Combine(directory, "segment-%05d.m4s");

        var arguments = new List<string>
        {
            "-v", "error",
            "-nostdin",
            "-y",
            "-fflags", "+genpts"
        };

        if (startSeconds > 0)
        {
            arguments.AddRange([
                "-ss",
                startSeconds.ToString("0.###", CultureInfo.InvariantCulture)
            ]);
        }

        arguments.AddRange([
            "-i", Path.GetFullPath(sourcePath),
            "-map", "0:v:0",
            "-map", LivePlaybackCommand.AudioMap(audioStreamIndex),
            "-sn",
            "-dn",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "22",
            "-pix_fmt", "yuv420p"
        ]);
        arguments.AddRange(PlaybackQuality.EncodeArguments(qualityCap));
        arguments.AddRange([
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})",
            "-c:a", "aac",
            "-b:a", PlaybackQuality.AudioBitrate(qualityCap),
            "-max_muxing_queue_size", "2048",
            "-avoid_negative_ts", "make_zero",
            "-f", "hls",
            "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", PlaylistSegments.ToString(CultureInfo.InvariantCulture),
            "-hls_delete_threshold", "3",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "init.mp4",
            "-hls_flags", "delete_segments+independent_segments",
            "-hls_segment_filename", segmentPath,
            playlistPath
        ]);

        return arguments;
    }

    private static bool AllowedAsset(string fileName) =>
        string.Equals(fileName, "index.m3u8", StringComparison.Ordinal) ||
        string.Equals(fileName, "init.mp4", StringComparison.Ordinal) ||
        s_segmentPattern.IsMatch(fileName);

    private static string ContentType(string fileName) =>
        fileName switch
        {
            "index.m3u8" => "application/vnd.apple.mpegurl",
            "init.mp4" => "video/mp4",
            _ => "video/iso.segment"
        };

    private async Task WaitForPlaylistAsync(
        Entry entry,
        CancellationToken cancellationToken)
    {
        var playlist = Path.Combine(entry.DirectoryPath, "index.m3u8");
        var init = Path.Combine(entry.DirectoryPath, "init.mp4");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(playlist) &&
                File.Exists(init) &&
                new FileInfo(playlist).Length > 0)
            {
                entry.Touch(_time.GetUtcNow());
                return;
            }

            if (entry.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"HLS transcoder exited before producing a playlist: {entry.Process.ErrorSummary}");
            }

            await Task.Delay(75, cancellationToken);
        }

        throw new TimeoutException(
            $"HLS transcoder did not produce its first segment in time: {entry.Process.ErrorSummary}");
    }

    /// <summary>
    /// Drops the oldest idle sessions until the cache is under its budget and the volume above
    /// its free-space floor. Returns the refusal code when idle sessions are not enough: playing
    /// sessions are never dropped. Callers hold <see cref="gate"/>.
    /// </summary>
    private string? PruneToPolicy(PlaybackTranscodingSettings policy)
    {
        while (true)
        {
            var overBudget = MeasureBytes(policy.HlsCachePath) >= policy.CacheBudgetBytes;
            var belowFloor = _freeSpace(policy.HlsCachePath) is { } free && free < policy.FreeSpaceFloorBytes;
            if (!overBudget && !belowFloor)
            {
                return null;
            }

            var idleBefore = _time.GetUtcNow() - BudgetPruneIdleAfter;
            var oldestIdle = _sessions.Values.Where(x => x.LastAccessUtc <= idleBefore).MinBy(x => x.LastAccessUtc);
            if (oldestIdle is null)
            {
                return overBudget ? PlaybackAdmissionCodes.CacheBudgetExhausted : PlaybackAdmissionCodes.CacheFreeSpaceLow;
            }

            Remove(oldestIdle.SessionId);
        }
    }

    private static long MeasureBytes(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
                // A segment ffmpeg or a prune deleted between listing and reading no longer counts.
            }
        }

        return total;
    }

    private int RemoveOrphanDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow() - IdleLifetime;
        var removed = 0;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories().Where(x => s_sessionDirectoryPattern.IsMatch(x.Name)))
        {
            var owned = _sessions.Values.Any(x => string.Equals(Path.GetFileName(x.DirectoryPath), directory.Name, StringComparison.Ordinal));
            if (owned || new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero) >= cutoff)
            {
                continue;
            }

            if (TryDeleteDirectory(directory.FullName))
            {
                removed++;
            }
        }

        return removed;
    }

    private void EvictForProfileCapacity(string profileId)
    {
        while (_sessions.Values.Count(
                   x => string.Equals(
                       x.ProfileId,
                       profileId,
                       StringComparison.Ordinal)) >= MaxSessionsPerProfile)
        {
            var oldest = _sessions.Values
                .Where(x => string.Equals(
                    x.ProfileId,
                    profileId,
                    StringComparison.Ordinal))
                .OrderBy(x => x.LastAccessUtc)
                .FirstOrDefault();

            if (oldest is null)
            {
                break;
            }

            Remove(oldest.SessionId);
        }
    }

    private void Remove(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return;
        }

        try
        {
            if (!entry.Process.HasExited)
            {
                entry.Process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            entry.Process.Dispose();
            entry.Lease?.Dispose();
        }

        TryDeleteDirectory(entry.DirectoryPath);
    }

    private bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not delete the HLS cache directory {Directory}.", path);
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Entry(
        Guid sessionId,
        Guid episodeId,
        string profileId,
        string directoryPath,
        IHlsEncoderProcess process,
        double startSeconds,
        DateTimeOffset createdAtUtc,
        IDisposable? lease)
    {
        private long _lastAccessTicks = createdAtUtc.UtcTicks;

        public IDisposable? Lease { get; } = lease;
        public Guid SessionId { get; } = sessionId;
        public Guid EpisodeId { get; } = episodeId;
        public string ProfileId { get; } = profileId;
        public string DirectoryPath { get; } = directoryPath;
        public IHlsEncoderProcess Process { get; } = process;
        public double StartSeconds { get; } = startSeconds;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset LastAccessUtc => new(Interlocked.Read(ref _lastAccessTicks), TimeSpan.Zero);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastAccessTicks, now.UtcTicks);
    }
}

/// <summary>The real ffmpeg of an HLS session. Arguments are passed as a list, never through a shell.</summary>
public sealed class FfmpegHlsProcess : IHlsEncoderProcess
{
    private const int MaxSummaryLength = 200;

    private readonly Process _process;
    private readonly Lock _gate = new();
    private string _lastLine = "";

    private FfmpegHlsProcess(Process process)
    {
        _process = process;
    }

    public bool HasExited => _process.HasExited;

    public string ErrorSummary
    {
        get
        {
            lock (_gate)
            {
                return _lastLine;
            }
        }
    }

    public static FfmpegHlsProcess Start(IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start HLS transcoding.");
        }

        var wrapper = new FfmpegHlsProcess(process);
        process.ErrorDataReceived += wrapper.OnErrorLine;
        process.BeginErrorReadLine();
        return wrapper;
    }

    public void Kill() => _process.Kill(entireProcessTree: true);

    public void Dispose() => _process.Dispose();

    // ffmpeg prints the failing step last, and stderr must be drained anyway or a full pipe stalls the encoder.
    private void OnErrorLine(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = e.Data.Trim();
        lock (_gate)
        {
            _lastLine = line.Length <= MaxSummaryLength ? line : line[..MaxSummaryLength];
        }
    }
}
