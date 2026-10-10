using System.Collections.Concurrent;
using Jularr.Web.Data;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Features.Library;
using Jularr.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Playback;

public enum PlaybackPreparationStatus
{
    None,
    Queued,
    Processing,
    Ready,
    Failed
}

public enum PlaybackPreparationKind
{
    CompatibleRemux,
    DeviceHevcRemux,
    ServerH264Transcode
}

public enum PlaybackRequestedMode
{
    Device,
    Server
}

public sealed record PlaybackPreparationState(
    PlaybackPreparationStatus Status,
    string? Message = null);

public sealed class PlaybackPreparationTracker
{
    private readonly ConcurrentDictionary<(Guid MediaFileId, PlaybackPreparationKind Kind), PlaybackPreparationState>
        states = new();

    public PlaybackPreparationState Get(Guid mediaFileId, PlaybackPreparationKind kind) =>
        states.TryGetValue((mediaFileId, kind), out var state)
            ? state
            : new PlaybackPreparationState(PlaybackPreparationStatus.None);

    public bool TryQueue(Guid mediaFileId, PlaybackPreparationKind kind)
    {
        var key = (mediaFileId, kind);

        while (true)
        {
            var current = Get(mediaFileId, kind);
            if (current.Status is PlaybackPreparationStatus.Queued or PlaybackPreparationStatus.Processing)
            {
                return false;
            }

            var queued = new PlaybackPreparationState(PlaybackPreparationStatus.Queued);

            if (current.Status == PlaybackPreparationStatus.None)
            {
                if (states.TryAdd(key, queued))
                {
                    return true;
                }

                continue;
            }

            if (states.TryUpdate(key, queued, current))
            {
                return true;
            }
        }
    }

    public void MarkProcessing(Guid mediaFileId, PlaybackPreparationKind kind) =>
        states[(mediaFileId, kind)] = new PlaybackPreparationState(PlaybackPreparationStatus.Processing);

    public void MarkReady(Guid mediaFileId, PlaybackPreparationKind kind) =>
        states[(mediaFileId, kind)] = new PlaybackPreparationState(PlaybackPreparationStatus.Ready);

    public void Forget(Guid mediaFileId, PlaybackPreparationKind kind) =>
        states.TryRemove((mediaFileId, kind), out _);

    public void MarkFailed(Guid mediaFileId, PlaybackPreparationKind kind, string message) =>
        states[(mediaFileId, kind)] = new PlaybackPreparationState(
            PlaybackPreparationStatus.Failed,
            message);
}

public enum PlaybackTrackKind
{
    Audio,
    Subtitle
}

public sealed record PlaybackMediaTrack(
    int StreamIndex,
    PlaybackTrackKind Kind,
    string? Codec,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    bool IsText);

// Playback's decision view of the canonical media inventory (MediaInventoryService).
public sealed record PlaybackProbeResult(
    string? VideoCodec,
    string? PixelFormat,
    string? AudioCodec,
    double? DurationSeconds = null,
    IReadOnlyList<PlaybackMediaTrack>? Tracks = null,
    int? VideoHeight = null)
{
    public static PlaybackProbeResult From(MediaTechnicalInfo technical) =>
        new(
            technical.Video?.Codec,
            technical.Video?.PixelFormat,
            technical.AudioStreams.FirstOrDefault()?.Codec,
            technical.DurationSeconds,
            [
                .. technical.Streams.Select(stream => new PlaybackMediaTrack(
                    stream.Index,
                    stream.Kind == MediaStreamKind.Audio
                        ? PlaybackTrackKind.Audio
                        : PlaybackTrackKind.Subtitle,
                    stream.Codec,
                    stream.Language,
                    stream.Title,
                    stream.IsDefault,
                    stream.IsForced,
                    stream.IsText))
            ],
            technical.Video?.Height);
}

public enum PlaybackAudioMode
{
    None,
    Copy,
    Aac
}

public enum PlaybackVideoMode
{
    Copy,
    H264
}

public sealed record PlaybackPreparationPlan(
    bool CanPrepare,
    PlaybackPreparationKind? Kind,
    PlaybackVideoMode VideoMode,
    PlaybackAudioMode AudioMode,
    bool TagHevcAsHvc1,
    string Message)
{
    private static readonly HashSet<string> BrowserH264PixelFormats =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "yuv420p",
            "yuvj420p"
        };

    public static PlaybackPreparationPlan Build(
        PlaybackProbeResult probe,
        PlaybackRequestedMode requestedMode)
    {
        var audioMode = BuildAudioMode(probe.AudioCodec);

        if (string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) &&
            probe.PixelFormat is not null &&
            BrowserH264PixelFormats.Contains(probe.PixelFormat))
        {
            return new PlaybackPreparationPlan(
                true,
                PlaybackPreparationKind.CompatibleRemux,
                PlaybackVideoMode.Copy,
                audioMode,
                false,
                audioMode == PlaybackAudioMode.Aac
                    ? $"H.264 video will be copied; {probe.AudioCodec} audio will be converted to AAC."
                    : "H.264 video and compatible audio will be copied without video re-encoding.");
        }

        if (requestedMode == PlaybackRequestedMode.Device)
        {
            if (string.Equals(probe.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(probe.VideoCodec, "h265", StringComparison.OrdinalIgnoreCase))
            {
                return new PlaybackPreparationPlan(
                    true,
                    PlaybackPreparationKind.DeviceHevcRemux,
                    PlaybackVideoMode.Copy,
                    audioMode,
                    true,
                    audioMode == PlaybackAudioMode.Aac
                        ? $"HEVC video will stay untouched; {probe.AudioCodec} audio will be converted to AAC."
                        : "HEVC video will stay untouched and be remuxed for device decoding.");
            }

            return Unsupported(
                $"Video codec {probe.VideoCodec ?? "unknown"} cannot use the device-only preparation path.");
        }

        if (string.IsNullOrWhiteSpace(probe.VideoCodec))
        {
            return Unsupported("No video stream was detected.");
        }

        return new PlaybackPreparationPlan(
            true,
            PlaybackPreparationKind.ServerH264Transcode,
            PlaybackVideoMode.H264,
            audioMode,
            false,
            $"Video codec {probe.VideoCodec} will be transcoded to H.264 on the server.");
    }

    private static PlaybackAudioMode BuildAudioMode(string? audioCodec) =>
        string.IsNullOrWhiteSpace(audioCodec)
            ? PlaybackAudioMode.None
            : string.Equals(audioCodec, "aac", StringComparison.OrdinalIgnoreCase)
                ? PlaybackAudioMode.Copy
                : PlaybackAudioMode.Aac;

    private static PlaybackPreparationPlan Unsupported(string message) =>
        new(
            false,
            null,
            PlaybackVideoMode.Copy,
            PlaybackAudioMode.None,
            false,
            message);
}

public static class PlaybackCache
{
    public const string RootPath = "/data/playback-cache";

    public static string BuildPath(
        Guid mediaFileId,
        long sizeBytes,
        DateTime sourceUpdatedAt,
        PlaybackPreparationKind kind) =>
        Path.Combine(
            RootPath,
            $"{mediaFileId:N}-{sizeBytes}-{sourceUpdatedAt.Ticks}-{Suffix(kind)}.mp4");

    public static string BuildPattern(Guid mediaFileId, PlaybackPreparationKind kind) =>
        $"{mediaFileId:N}-*-{Suffix(kind)}.mp4";

    private static string Suffix(PlaybackPreparationKind kind) =>
        kind switch
        {
            PlaybackPreparationKind.CompatibleRemux => "compatible",
            PlaybackPreparationKind.DeviceHevcRemux => "device-hevc",
            PlaybackPreparationKind.ServerH264Transcode => "server-h264",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}

public sealed class PlaybackPreparationService(
    AppDbContext db,
    MediaInventoryService mediaInventory,
    PlaybackPreparationTracker tracker,
    MediaProcessRunner processRunner,
    ILogger<PlaybackPreparationService> logger,
    BackgroundJobQueue jobs,
    CanonicalMediaStorageService canonicalStorage,
    IMediaProbeRunner probeRunner,
    PlaybackTranscodingSettingsStore settings,
    PlaybackTranscodeSlots slots,
    PlaybackStreamSessionStore sessions,
    HlsPlaybackSessionManager hls,
    TimeProvider time)
{
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromHours(6);
    private const string VerifiedRecipe = "mobile1080";
    private const int MaxPreparedEntries = 128;
    private static readonly string VerifiedRoot = Path.Combine(PlaybackCache.RootPath, "prepared-v1");


    public async Task PrepareAsync(
        Guid episodeId,
        PlaybackRequestedMode requestedMode,
        CancellationToken cancellationToken)
    {
        var media = await db.MediaFiles
            .AsNoTracking()
            .Where(x => x.EpisodeId == episodeId)
            .OrderBy(x => x.Path)
            .Select(x => new
            {
                x.Id,
                x.Path,
                x.SizeBytes,
                x.LastWriteTimeUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (media is null)
        {
            return;
        }

        var inventory = await mediaInventory.EnsureAnalyzedAsync(media.Id, cancellationToken);
        if (inventory?.Technical is not { } technical)
        {
            return;
        }

        var plan = PlaybackPreparationPlan.Build(PlaybackProbeResult.From(technical), requestedMode);
        if (!plan.CanPrepare || plan.Kind is null)
        {
            return;
        }

        var kind = plan.Kind.Value;
        tracker.MarkProcessing(media.Id, kind);

        var outputPath = PlaybackCache.BuildPath(
            media.Id,
            media.SizeBytes,
            media.LastWriteTimeUtc,
            kind);

        if (File.Exists(outputPath))
        {
            tracker.MarkReady(media.Id, kind);
            return;
        }

        Directory.CreateDirectory(PlaybackCache.RootPath);

        foreach (var stalePath in Directory.EnumerateFiles(
                     PlaybackCache.RootPath,
                     PlaybackCache.BuildPattern(media.Id, kind)))
        {
            if (!string.Equals(stalePath, outputPath, StringComparison.Ordinal))
            {
                File.Delete(stalePath);
            }
        }

        var temporaryPath = outputPath + ".tmp";
        TryDelete(temporaryPath);

        var arguments = new List<string>
        {
            "-v", "error",
            "-nostdin",
            "-y",
            "-i", Path.GetFullPath(media.Path),
            "-map", "0:v:0",
            "-sn",
            "-dn"
        };

        if (plan.VideoMode == PlaybackVideoMode.Copy)
        {
            arguments.AddRange(["-c:v", "copy"]);
            if (plan.TagHevcAsHvc1)
            {
                arguments.AddRange(["-tag:v", "hvc1"]);
            }
        }
        else
        {
            arguments.AddRange([
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "22",
                "-pix_fmt", "yuv420p"
            ]);
        }

        switch (plan.AudioMode)
        {
            case PlaybackAudioMode.Copy:
                arguments.AddRange(["-map", "0:a:0?", "-c:a", "copy"]);
                break;
            case PlaybackAudioMode.Aac:
                arguments.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-b:a", "192k"]);
                break;
        }

        arguments.AddRange(["-movflags", "+faststart", "-f", "mp4", temporaryPath]);

        try
        {
            var result = await processRunner.RunAsync(
                "ffmpeg",
                arguments,
                PreparationTimeout,
                cancellationToken);

            if (result is null ||
                result.ExitCode != 0 ||
                !File.Exists(temporaryPath) ||
                new FileInfo(temporaryPath).Length == 0)
            {
                TryDelete(temporaryPath);
                var message = result?.ErrorSummary;
                tracker.MarkFailed(
                    media.Id,
                    kind,
                    string.IsNullOrWhiteSpace(message)
                        ? "Playback preparation failed."
                        : $"Playback preparation failed: {message}");
                return;
            }

            File.Move(temporaryPath, outputPath);
            tracker.MarkReady(media.Id, kind);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            tracker.MarkFailed(media.Id, kind, "Could not write the playback cache.");
            logger.LogError(
                exception,
                "Could not prepare playback cache for {MediaPath}.",
                media.Path);
        }
    }

    /// <summary>
    /// Owner/manual entry point. The existing bounded Maintenance operation queue is the only
    /// producer scheduler. Duplicate requests for the same canonical source coalesce while queued.
    /// No work is queued when the Admin policy is Off.
    /// </summary>
    public async ValueTask<Guid?> QueueVerifiedAsync(
        long workId,
        Guid? workEpisodeId,
        CancellationToken cancellationToken)
    {
        if (workId <= 0 || workEpisodeId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(workId));
        }

        var policy = settings.Current;
        if (!policy.PreparedRenditionsEnabled || !policy.TranscodingEnabled ||
            !policy.InPreparationWindow(time.GetUtcNow()))
        {
            return null;
        }

        var original = await canonicalStorage.ResolveVideoAsync(workId, workEpisodeId, cancellationToken);
        if (original is null ||
            !tracker.TryQueue(original.StoredFileId, PlaybackPreparationKind.ServerH264Transcode))
        {
            return null;
        }

        try
        {
            return await jobs.QueueAsync(
                new OperationDescriptor(
                    "playback-verified-preparation",
                    "Playback",
                    "Prepare reusable video rendition",
                    Subject: workId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Lane: OperationLane.Maintenance,
                    Retryable: false,
                    Priority: OperationPriority.Low),
                async (operation, services, token) =>
                {
                    try
                    {
                        await services.GetRequiredService<PlaybackPreparationService>()
                            .PrepareVerifiedAsync(workId, workEpisodeId, operation, token);
                    }
                    finally
                    {
                        tracker.Forget(original.StoredFileId, PlaybackPreparationKind.ServerH264Transcode);
                    }
                },
                cancellationToken);
        }
        catch
        {
            tracker.Forget(original.StoredFileId, PlaybackPreparationKind.ServerH264Transcode);
            throw;
        }
    }

    /// <summary>
    /// Conservative V1: prepare only SDR 1080p H.264 with zero subtitle tracks and at most
    /// one AAC/MP3 audio track. Unknown/multiple tracks, HDR and different cuts remain live
    /// fallback: this cannot compromise timestamp, track or resume equivalence.
    /// </summary>
    private async Task PrepareVerifiedAsync(
        long workId,
        Guid? workEpisodeId,
        OperationExecutionContext operation,
        CancellationToken cancellationToken)
    {
        var policy = settings.Current;
        if (!CanPrepareNow(policy))
        {
            throw new InvalidOperationException("Video preparation is not allowed while playback is active or outside the maintenance window.");
        }

        var source = await canonicalStorage.ResolveVideoAsync(workId, workEpisodeId, cancellationToken)
            ?? throw new InvalidOperationException("The canonical source is no longer installed.");
        var entry = await mediaInventory.EnsureAnalyzedAsync(source.StoredFileId, cancellationToken);
        if (entry is not { Status: MediaAnalysisStatus.Succeeded, Technical: { } technical } ||
            entry.SourceFingerprint is not { Length: 64 } sourceFingerprint ||
            technical.Video is not { StreamIndex: 0, Width: > 0, Height: >= 1080 } video ||
            !string.Equals(video.DynamicRange, "SDR", StringComparison.OrdinalIgnoreCase) ||
            technical.DurationSeconds is not (> 120 and < 86400) ||
            technical.SubtitleStreams.Count != 0 ||
            technical.AudioStreams.Count > 1 ||
            (technical.AudioStreams.Count == 1 &&
             (technical.AudioStreams[0].Index != 1 ||
              technical.AudioStreams[0].Codec is not ("aac" or "mp3"))) ||
            string.Equals(video.Codec, "h264", StringComparison.OrdinalIgnoreCase) &&
            video.Height <= 1080)
        {
            throw new InvalidOperationException("The source is not eligible for the safe SDR 1080p recipe.");
        }

        var expectedBytes = (long)Math.Ceiling(technical.DurationSeconds.Value * 750_000d);
        if (expectedBytes <= 0 || expectedBytes > policy.PreparedCacheBudgetBytes)
        {
            throw new InvalidOperationException("The expected output would exceed the prepared cache budget.");
        }

        Directory.CreateDirectory(VerifiedRoot);
        // Count and measure only Jularr's own flat cache, not any source or NAS root.
        long existingBytes = 0;
        var count = 0;
        foreach (var prepared in Directory.EnumerateFiles(VerifiedRoot, "*.mp4", SearchOption.TopDirectoryOnly))
        {
            if (++count > MaxPreparedEntries)
            {
                throw new InvalidOperationException("The prepared cache has reached its bounded entry limit.");
            }

            existingBytes = checked(existingBytes + new FileInfo(prepared).Length);
        }

        if (existingBytes > policy.PreparedCacheBudgetBytes - expectedBytes ||
            !HasReservedFreeSpace(expectedBytes, policy.FreeSpaceFloorBytes))
        {
            throw new InvalidOperationException("The prepared cache has insufficient quota or reserved free disk space.");
        }

        var outputName = $"prepared-{source.StoredFileId:N}-{sourceFingerprint[..24]}-{VerifiedRecipe}.mp4";
        var path = Path.Combine(VerifiedRoot, outputName);
        if (File.Exists(path))
        {
            if ((await canonicalStorage.ResolveVideoCandidatesAsync(
                    workId, workEpisodeId, cancellationToken)).Any(candidate =>
                        candidate.Path == path &&
                        candidate.VersionSource == CanonicalMediaStorageService.PreparedVideoVersionSource))
            {
                return;
            }

            throw new InvalidOperationException("An unregistered prepared output already exists; cleanup is required.");
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var slot = slots.TryAcquire(PlaybackCostClass.SoftwareVideo)
            ?? throw new InvalidOperationException("An interactive transcode has priority over preparation.");
        try
        {
            if (!CanPrepareNow(policy, ownedSlot: true))
            {
                throw new InvalidOperationException("An interactive playback started before preparation.");
            }

            await operation.ReportAsync(0, "Preparing verified SDR video", cancellationToken: cancellationToken);
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-i", Path.GetFullPath(source.Path),
                "-map", "0:v:0", "-map", "0:a:0?",
                "-sn", "-dn",
                "-c:v", "libx264", "-preset", "veryfast",
                "-crf", "22", "-maxrate", "6000k", "-bufsize", "12000k",
                "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-pix_fmt", "yuv420p",
                "-c:a", "copy",
                "-movflags", "+faststart", "-f", "mp4", temporaryPath
            };
            var result = await RunYieldingToPlaybackAsync(args, cancellationToken);
            if (result is null || result.ExitCode != 0 ||
                !File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
            {
                throw new InvalidDataException("FFmpeg failed to produce a complete prepared version.");
            }

            var actualBytes = new FileInfo(temporaryPath).Length;
            if (actualBytes > policy.PreparedCacheBudgetBytes - existingBytes ||
                !HasReservedFreeSpace(0, policy.FreeSpaceFloorBytes))
            {
                throw new InvalidOperationException("The generated rendition exceeds the cache quota or free-space reserve.");
            }

            var decode = await processRunner.RunAsync(
                "ffmpeg",
                ["-hide_banner", "-loglevel", "error", "-nostdin",
                 "-ss", "10", "-i", temporaryPath, "-frames:v", "2", "-an", "-f", "null", "-"],
                TimeSpan.FromMinutes(2), cancellationToken);
            if (decode?.ExitCode != 0)
            {
                throw new InvalidDataException("The prepared video failed representative decoding.");
            }

            // The source is only read. The verified output is moved atomically on the managed
            // cache filesystem before the canonical publisher attaches a Version/Asset/File.
            if (!CanPrepareNow(settings.Current, ownedSlot: true) ||
                !HasReservedFreeSpace(0, settings.Current.FreeSpaceFloorBytes))
            {
                throw new InvalidOperationException("Preparation lost its idle or disk headroom.");
            }

            File.Move(temporaryPath, path, overwrite: false);
            try
            {
                var published = await canonicalStorage.PublishVerifiedPreparedVideoAsync(
                    source, entry, path, VerifiedRoot, VerifiedRecipe, probeRunner, cancellationToken);
                await mediaInventory.EnsureAnalyzedAsync(published.StoredFileId, cancellationToken);
                await operation.ReportAsync(100, "Verified video version available", cancellationToken: cancellationToken);
            }
            catch
            {
                TryDelete(path);
                throw;
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private bool CanPrepareNow(PlaybackTranscodingSettings policy, bool ownedSlot = false) =>
        policy.PreparedRenditionsEnabled &&
        policy.TranscodingEnabled &&
        policy.InPreparationWindow(time.GetUtcNow()) &&
        sessions.ActiveRecentDeliveries() == 0 &&
        hls.ActiveSessions == 0 &&
        slots.Active(PlaybackCostClass.SoftwareVideo) <= (ownedSlot ? 1 : 0) &&
        slots.Active(PlaybackCostClass.HardwareVideo) == 0;

    private static bool HasReservedFreeSpace(long expectedBytes, long floorBytes)
    {
        var available = AdminServerLoad.VolumeSpace(VerifiedRoot).Free;
        return available is { } bytes && bytes >= expectedBytes &&
               bytes - expectedBytes >= floorBytes;
    }

    private async Task<MediaProcessResult?> RunYieldingToPlaybackAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = processRunner.RunAsync("ffmpeg", arguments, PreparationTimeout, active.Token);
        while (!running.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
            if (!CanPrepareNow(settings.Current, ownedSlot: true))
            {
                active.Cancel();
                try
                {
                    await running;
                }
                catch (OperationCanceledException)
                {
                }

                throw new InvalidOperationException("Interactive playback or maintenance policy interrupted preparation.");
            }
        }

        return await running;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
