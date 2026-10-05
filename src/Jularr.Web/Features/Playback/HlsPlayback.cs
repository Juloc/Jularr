using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
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
public sealed record HlsSweepResult(int ExpiredSessions, int PrunedForPolicy, int OrphanDirectories, int CrashedSessions = 0);

/// <summary>One running ffmpeg of an HLS session; the seam that lets the cache rules be tested without starting ffmpeg.</summary>
public interface IHlsEncoderProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>The exit code once the process has exited, otherwise null.</summary>
    int? ExitCode { get; }

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
    /// A session untouched for this long may be dropped to make room under the cache budget. A playing
    /// session reads a segment every few seconds and a player may buffer ahead for up to two minutes (the
    /// largest buffer preset), so shorter silence is not proof of an abandoned player; the idle lifetime
    /// itself is far too long to relieve a full cache.
    /// </summary>
    public static readonly TimeSpan BudgetPruneIdleAfter = TimeSpan.FromMinutes(2);

    /// <summary>How many involuntarily ended sessions are remembered so a late request can be told why.</summary>
    private const int MaxRememberedEndings = 128;

    private static readonly Regex s_segmentPattern =
        new("^segment-[0-9]{5}\\.m4s$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();
    private readonly Lock _endGate = new();
    private readonly Dictionary<Guid, HlsSessionEndReason> _endReasons = [];
    private readonly Queue<Guid> _endOrder = [];
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
        var session = await StartAsync(episodeId, profileId, startSeconds, directory => BuildArguments(sourcePath, directory, startSeconds, audioStreamIndex, qualityCap), lease, cancellationToken);
        return session with { AudioStreamIndex = audioStreamIndex, QualityCap = qualityCap };
    }

    /// <summary>
    /// Starts one bounded HLS session whose ffmpeg arguments the caller builds for the
    /// session directory (a playback plan's remux or transcode). The optional lease (a
    /// transcode slot) is released when the session ends, also when it never starts. A full
    /// cache refuses the session with a <see cref="PlaybackAdmissionRefusedException"/>. <paramref name="onProgress"/> receives the
    /// measured progress of the encoder while it runs.
    /// </summary>
    public async Task<HlsPlaybackSession> StartAsync(
        Guid episodeId,
        string profileId,
        double startSeconds,
        Func<string, IReadOnlyList<string>> buildArguments,
        IDisposable? lease,
        CancellationToken cancellationToken,
        Action<PlaybackTranscodeSample>? onProgress = null)
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

        Entry entry;
        try
        {
            ThrowIfDisposed();
            ReapExitedProcesses();
            CleanupExpired();
            lock (_gate)
            {
                // Admission first: a refused start must not cost the profile its own running session.
                var policy = _settings.Current;
                Directory.CreateDirectory(policy.HlsCachePath);
                if (!PlaybackCacheOwnership.IsOwnedRoot(policy.HlsCachePath))
                {
                    throw new PlaybackAdmissionRefusedException(PlaybackAdmissionCodes.CacheFolderNotOwned);
                }

                PlaybackCacheOwnership.MarkRoot(policy.HlsCachePath);
                if (PruneToPolicy(policy, terminateRunning: false) is { } refusal)
                {
                    throw new PlaybackAdmissionRefusedException(refusal);
                }

                EvictForProfileCapacity(profileId);
                var sessionId = Guid.NewGuid();
                var directory = Path.Combine(policy.HlsCachePath, sessionId.ToString("N"));
                Directory.CreateDirectory(directory);

                IHlsEncoderProcess process;
                try
                {
                    process = _startProcess(buildArguments(directory));
                    if (onProgress is not null && process is IFfmpegProgressSource progressSource)
                    {
                        progressSource.ProgressReported += onProgress;
                    }
                }
                catch
                {
                    TryDeleteDirectory(directory);
                    throw;
                }

                entry = new Entry(sessionId, episodeId, profileId, directory, process, startSeconds, _time.GetUtcNow(), lease);
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

    /// <summary>Whether the profile has a session of this id whose encoder has not crashed. A pure read: crashed sessions are removed by <see cref="ReapExitedProcesses"/>.</summary>
    public bool IsActive(Guid sessionId, string profileId) =>
        _sessions.TryGetValue(sessionId, out var entry) &&
        string.Equals(entry.ProfileId, profileId, StringComparison.Ordinal) &&
        !HasCrashed(entry);

    /// <summary>Why a session that was ended without its player asking (idle, cache policy, encoder crash) is gone; null when unknown or when it was ended normally.</summary>
    public HlsSessionEndReason? EndReason(Guid sessionId)
    {
        // A crashed encoder is reported as such at once, before the next reap has recorded it: a pure read of the entry.
        if (_sessions.TryGetValue(sessionId, out var live) && HasCrashed(live))
        {
            return HlsSessionEndReason.EncoderExited;
        }

        lock (_endGate)
        {
            return _endReasons.TryGetValue(sessionId, out var reason) ? reason : null;
        }
    }

    /// <summary>Ends a session early (a client stopped or switched streams).</summary>
    public void Stop(Guid sessionId, string profileId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry) &&
            string.Equals(entry.ProfileId, profileId, StringComparison.Ordinal))
        {
            Remove(sessionId, HlsSessionEndReason.Stopped);
        }
    }

    public HlsPlaybackAsset? GetAsset(
        Guid sessionId,
        Guid episodeId,
        string profileId,
        string fileName)
    {
        ThrowIfDisposed();

        if (!_sessions.TryGetValue(sessionId, out var entry) ||
            entry.EpisodeId != episodeId ||
            !string.Equals(entry.ProfileId, profileId, StringComparison.Ordinal) ||
            !AllowedAsset(fileName) ||
            HasCrashed(entry))
        {
            return null;
        }

        var path = Path.Combine(entry.DirectoryPath, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        // Serving a segment is the one access that keeps a session alive.
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
                Remove(entry.SessionId, HlsSessionEndReason.Idle);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Ends sessions whose ffmpeg exited with an error and releases the slot of every session whose ffmpeg
    /// exited at all: a finished remux stays readable but no longer occupies an encoder slot.
    /// </summary>
    public int ReapExitedProcesses() => _sessions.Values.Count(ReapIfCrashed);

    /// <summary>
    /// One pass of the background cleanup: ends crashed sessions and expires idle ones, then enforces the
    /// cache policy on what is running (idle sessions first, then at most one running session, the largest,
    /// so a runaway remux cannot outgrow the budget or the free-space floor), and removes session
    /// directories no live session owns (left behind by a crashed process).
    /// </summary>
    public HlsSweepResult Sweep()
    {
        ThrowIfDisposed();
        var crashed = ReapExitedProcesses();
        var expired = CleanupExpired();
        lock (_gate)
        {
            var policy = _settings.Current;

            // Orphans first: leftovers of a crashed process count against the budget, and ending a healthy session
            // because of them would be wrong when deleting them already clears the policy.
            var orphans = RemoveOrphanDirectories(policy.HlsCachePath) + RemoveRetiredRootOrphans();
            var before = _sessions.Count;
            if (PruneToPolicy(policy, terminateRunning: true) is { } refusal)
            {
                _logger.LogWarning("The HLS cache is over its policy and ending sessions cannot fix it: {Reason}.", refusal);
            }

            return new HlsSweepResult(expired, before - _sessions.Count, orphans, crashed);
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
        var started = _time.GetTimestamp();

        while (_time.GetElapsedTime(started) < PlaybackDeliveryCommand.FirstOutputTimeout)
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
    /// Ends sessions until the cache is under its budget and the volume above its free-space floor.
    /// Idle sessions go first, oldest first. With <paramref name="terminateRunning"/> one running
    /// session, the largest, may follow (a runaway remux must not outgrow the budget); admission never
    /// ends a running session. Returns the refusal code when that is not enough. The cache is measured
    /// once; every ended session is subtracted from that measurement. Callers hold the gate.
    /// </summary>
    private string? PruneToPolicy(PlaybackTranscodingSettings policy, bool terminateRunning)
    {
        var usage = MeasureCache(policy.HlsCachePath);
        foreach (var session in _sessions.Values.Where(x => !IsUnder(policy.HlsCachePath, x.DirectoryPath)))
        {
            // A session started before the cache folder was changed still fills its old folder.
            usage.Add(session.SessionId, SumBytes(new DirectoryInfo(session.DirectoryPath)));
        }

        var terminated = false;
        while (true)
        {
            var free = _freeSpace(policy.HlsCachePath);
            var overBudget = usage.TotalBytes >= policy.CacheBudgetBytes;
            var belowFloor = free is { } available && available < policy.FreeSpaceFloorBytes;
            if (!overBudget && !belowFloor)
            {
                return null;
            }

            var idleBefore = _time.GetUtcNow() - BudgetPruneIdleAfter;
            var victim = _sessions.Values.Where(x => x.LastAccessUtc <= idleBefore).MinBy(x => x.LastAccessUtc);
            if (victim is null && terminateRunning && !terminated)
            {
                // Only a session whose own bytes clear the condition is worth ending: a volume that is full of other
                // data, or orphans that have not aged out yet, cannot be fixed by killing a healthy stream.
                var largest = _sessions.Values.MaxBy(x => usage.BytesOf(x.SessionId));
                if (largest is not null && ClearsPolicy(policy, usage, free, usage.BytesOf(largest.SessionId)))
                {
                    victim = largest;
                    terminated = true;
                }
            }

            if (victim is null)
            {
                return overBudget ? PlaybackAdmissionCodes.CacheBudgetExhausted : PlaybackAdmissionCodes.CacheFreeSpaceLow;
            }

            usage.Forget(victim.SessionId);
            Remove(victim.SessionId, overBudget ? HlsSessionEndReason.CacheBudget : HlsSessionEndReason.CacheFreeSpace);
        }
    }

    // Whether freeing this many bytes would put the cache under its budget and the volume over its floor.
    private static bool ClearsPolicy(PlaybackTranscodingSettings policy, CacheUsage usage, long? free, long bytes) =>
        bytes > 0 &&
        usage.TotalBytes - bytes < policy.CacheBudgetBytes &&
        (free is not { } available || available + bytes >= policy.FreeSpaceFloorBytes);

    /// <summary>One walk over the cache folder: bytes per session directory, and everything else lumped together. Entries that vanish mid-walk are skipped.</summary>
    private static CacheUsage MeasureCache(string root)
    {
        var usage = new CacheUsage();
        try
        {
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory)
                {
                    var bytes = SumBytes(directory);
                    if (PlaybackCacheOwnership.IsSessionDirectoryName(directory.Name) && Guid.TryParseExact(directory.Name, "N", out var sessionId))
                    {
                        usage.Add(sessionId, bytes);
                    }
                    else
                    {
                        usage.AddOther(bytes);
                    }
                }
                else if (entry is FileInfo file)
                {
                    usage.AddOther(SafeLength(file));
                }
            }
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException)
        {
            // The folder (or a session directory) vanished while it was measured: what was counted so far stands.
        }

        return usage;
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that vanished or cannot be read adds nothing to the measurement; the next pass sees the real state.
            return 0;
        }
    }

    private static long SumBytes(DirectoryInfo directory)
    {
        long total = 0;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                total += SafeLength(file);
            }
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // The directory was deleted while it was walked (a stopped session); what was counted so far stands.
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
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            // The ownership policy (shared with Admin Storage cleanup) decides what may be deleted at all.
            var owned = _sessions.Values.Any(x => string.Equals(Path.GetFileName(x.DirectoryPath), directory.Name, StringComparison.Ordinal));
            if (owned || !PlaybackCacheOwnership.IsDeletableSession(root, directory.FullName) || new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero) >= cutoff)
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

    // A changed cache folder retires the old one (the settings store remembers it): its running sessions end on their own, and
    // its leftovers are swept once they age out. The folder is forgotten when nothing of Jularr's is left in it.
    private int RemoveRetiredRootOrphans()
    {
        var removed = 0;
        foreach (var root in _settings.RetiredRoots)
        {
            removed += RemoveOrphanDirectories(root);
            var stillUsed = _sessions.Values.Any(x => IsUnder(root, x.DirectoryPath)) || (Directory.Exists(root) && Directory.EnumerateDirectories(root).Any(x => PlaybackCacheOwnership.IsDeletableSession(root, x)));
            if (!stillUsed)
            {
                _settings.ForgetRetiredRoot(root);
            }
        }

        return removed;
    }

    private static bool IsUnder(string root, string path)
    {
        var parent = Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(parent, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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

            Remove(oldest.SessionId, HlsSessionEndReason.Replaced);
        }
    }

    private static bool HasCrashed(Entry entry) => entry.Process.HasExited && entry.Process.ExitCode is not (null or 0);

    /// <summary>Ends a session whose ffmpeg exited with an error; returns true when it was removed. A clean exit only frees the encoder slot.</summary>
    private bool ReapIfCrashed(Entry entry)
    {
        if (!entry.Process.HasExited)
        {
            return false;
        }

        entry.ReleaseLease();
        if (!HasCrashed(entry))
        {
            return false;
        }

        Remove(entry.SessionId, HlsSessionEndReason.EncoderExited);
        return true;
    }

    /// <summary>Kills the encoder, frees its slot and deletes its files. A reason is remembered so a late request can be answered with it.</summary>
    private void Remove(Guid sessionId, HlsSessionEndReason? reason = null)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
        {
            return;
        }

        if (reason is not null)
        {
            RememberEnding(sessionId, reason.Value);
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
            // The process exited between the check and the kill: it is gone, which is what ending the session wants.
        }
        finally
        {
            entry.Process.Dispose();
            entry.ReleaseLease();
        }

        TryDeleteDirectory(entry.DirectoryPath);
    }

    private void RememberEnding(Guid sessionId, HlsSessionEndReason reason)
    {
        lock (_endGate)
        {
            if (_endReasons.TryAdd(sessionId, reason))
            {
                _endOrder.Enqueue(sessionId);
            }

            while (_endOrder.Count > MaxRememberedEndings)
            {
                _endReasons.Remove(_endOrder.Dequeue());
            }
        }
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

        private IDisposable? _lease = lease;

        public Guid SessionId { get; } = sessionId;
        public Guid EpisodeId { get; } = episodeId;
        public string ProfileId { get; } = profileId;
        public string DirectoryPath { get; } = directoryPath;
        public IHlsEncoderProcess Process { get; } = process;
        public double StartSeconds { get; } = startSeconds;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset LastAccessUtc => new(Interlocked.Read(ref _lastAccessTicks), TimeSpan.Zero);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastAccessTicks, now.UtcTicks);

        /// <summary>Frees the encoder slot once; safe to call from any path that notices the encoder is gone.</summary>
        public void ReleaseLease() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

/// <summary>Why a session ended without its player asking; the status endpoint tells a late request. Serialized as snake_case.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<HlsSessionEndReason>))]
public enum HlsSessionEndReason
{
    Idle,
    CacheBudget,
    CacheFreeSpace,
    EncoderExited,
    Stopped,
    Replaced
}

/// <summary>Bytes of the HLS cache folder, per session directory and in total, measured once per pass.</summary>
internal sealed class CacheUsage
{
    private readonly Dictionary<Guid, long> _perSession = [];
    private long _other;

    public long TotalBytes => _other + _perSession.Values.Sum();

    public long BytesOf(Guid sessionId) => _perSession.GetValueOrDefault(sessionId);

    public void Add(Guid sessionId, long bytes) => _perSession[sessionId] = bytes;

    public void AddOther(long bytes) => _other += bytes;

    public void Forget(Guid sessionId) => _perSession.Remove(sessionId);
}

/// <summary>The real ffmpeg of an HLS session. Arguments are passed as a list, never through a shell.</summary>
public sealed class FfmpegHlsProcess : IHlsEncoderProcess, IFfmpegProgressSource
{
    private const int MaxSummaryLength = 200;

    private readonly Process _process;
    private readonly Lock _gate = new();
    private readonly FfmpegProgressParser _progress = new();
    private string _lastLine = "";

    private FfmpegHlsProcess(Process process)
    {
        _process = process;
    }

    // A disposed or never-started process is gone: reading its state must not throw into a sweep or a request.
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

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

    public event Action<PlaybackTranscodeSample>? ProgressReported;

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

    // ffmpeg prints the failing step last, and stderr must be drained anyway or a full pipe stalls the encoder. Progress blocks share the pipe
    // and must never replace the failing step as the summary.
    private void OnErrorLine(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = e.Data.Trim();
        if (_progress.TryFeed(line, out var sample))
        {
            if (sample is not null)
            {
                ProgressReported?.Invoke(sample);
            }

            return;
        }

        lock (_gate)
        {
            _lastLine = line.Length <= MaxSummaryLength ? line : line[..MaxSummaryLength];
        }
    }
}
