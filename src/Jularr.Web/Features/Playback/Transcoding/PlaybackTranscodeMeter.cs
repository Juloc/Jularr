using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jularr.Web.Features.Playback.Decision;

namespace Jularr.Web.Features.Playback.Transcoding;

/// <summary>
/// One finished block of ffmpeg's <c>-progress</c> output. <see cref="Speed"/> is ffmpeg's own factor (media produced per second of
/// wall time since the process started, so it already includes startup), <see cref="Fps"/> the encoder output rate and
/// <see cref="OutputSeconds"/> the media time produced so far; each is null when ffmpeg printed <c>N/A</c> or nothing usable.
/// </summary>
public sealed record PlaybackTranscodeSample(double? Speed, double? Fps, double? OutputSeconds);

/// <summary>
/// Reads the key=value lines of <c>ffmpeg -progress</c> (one block per report, ended by <c>progress=continue|end</c>). It owns what
/// belongs to that protocol so everything else on the same pipe stays a diagnostic line for the caller. Unknown, partial and
/// <c>N/A</c> values never throw: a value that cannot be read is simply absent from the sample.
/// </summary>
public sealed partial class FfmpegProgressParser
{
    private static readonly FrozenSet<string> s_keys = new[]
    {
        "frame", "fps", "bitrate", "total_size", "out_time_us", "out_time_ms", "out_time", "dup_frames", "drop_frames", "speed", "progress"
    }.ToFrozenSet(StringComparer.Ordinal);

    private const double MaxFps = 100_000;

    private double? _speed;
    private double? _fps;
    private double? _outputSeconds;

    /// <summary>
    /// Feeds one line. Returns true when the line belongs to the progress protocol (so the caller must not treat it as an error line);
    /// <paramref name="sample"/> is set when the line ended a block.
    /// </summary>
    public bool TryFeed(string line, out PlaybackTranscodeSample? sample)
    {
        sample = null;
        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator].Trim();
        if (!s_keys.Contains(key) && !StreamQualityKey().IsMatch(key))
        {
            return false;
        }

        var value = line[(separator + 1)..].Trim();
        switch (key)
        {
            case "speed":
                _speed = ParseNumber(value.TrimEnd('x'), PlaybackTranscodeMeter.MaxSpeed);
                break;
            case "fps":
                _fps = ParseNumber(value, MaxFps);
                break;
            case "out_time_us" or "out_time_ms":
                // ffmpeg names the millisecond key wrongly: both carry microseconds.
                _outputSeconds ??= long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var micros) ? micros / 1_000_000d : null;
                break;
            case "out_time":
                _outputSeconds ??= TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero ? time.TotalSeconds : null;
                break;
            case "progress":
                sample = new PlaybackTranscodeSample(_speed, _fps, _outputSeconds);
                _speed = null;
                _fps = null;
                _outputSeconds = null;
                break;
        }

        return true;
    }

    private static double? ParseNumber(string value, double max) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 && number <= max ? number : null;

    [GeneratedRegex("^stream_[0-9]+_[0-9]+_q$", RegexOptions.CultureInvariant)]
    private static partial Regex StreamQualityKey();
}

/// <summary>What the measured speed of a running transcode says about whether the server keeps up.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackTranscodeSpeedState>))]
public enum PlaybackTranscodeSpeedState
{
    /// <summary>No encode is running or its progress went silent; nothing is known.</summary>
    Unknown,

    /// <summary>The encode is still within its warm-up or a dip has not lasted long enough to count.</summary>
    Measuring,

    /// <summary>The encode has run at or above the target speed.</summary>
    Sustainable,

    /// <summary>Real time is kept but the safety margin is not: no reason to lower quality, but no room to raise it.</summary>
    BelowTarget,

    /// <summary>The encode has stayed under real time: playback cannot keep up however long it buffers.</summary>
    TooSlow
}

/// <summary>The latest measured state of one session's transcode, as diagnostics and the adaptation read it.</summary>
public sealed record PlaybackTranscodeReading(PlaybackTranscodeSpeedState State, double? Speed, double? Fps, PlaybackHardwareBackend? Backend)
{
    public static PlaybackTranscodeReading None { get; } = new(PlaybackTranscodeSpeedState.Unknown, null, null, null);
}

/// <summary>
/// The measured speed of the transcode behind one playback session; ephemeral like all session telemetry. Only the newest run counts: a
/// restart (seek, re-plan, fallback) calls <see cref="BeginRun"/> and a late sample of the replaced process is discarded. The decision
/// is time-driven by the injected clock and holds no history beyond a few timestamps, so it is bounded and deterministic.
/// </summary>
public sealed class PlaybackTranscodeMeter(TimeProvider time)
{
    /// <summary>Real time: below this an encode falls further behind the player the longer it runs.</summary>
    public const double RealTimeSpeed = 1.0;

    /// <summary>The safety margin admission aims for (#403: 1.15x); only a speed below it blocks raising the quality.</summary>
    public const double TargetSpeed = 1.15;

    public const double MaxSpeed = 10_000;

    /// <summary>ffmpeg's cumulative speed is dominated by startup (probing, first keyframe) until this much media was produced.</summary>
    public const double WarmUpOutputSeconds = 10;

    /// <summary>A speed below a threshold counts only once it stayed there this long, so one slow report never triggers a re-plan.</summary>
    public static readonly TimeSpan SustainedFor = TimeSpan.FromSeconds(10);

    /// <summary>ffmpeg reports at least every second while it works; silence this long means the encode ended or stopped.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time;
    private readonly Lock _gate = new();
    private Run? _current;

    /// <summary>Starts measuring a new encode on <paramref name="backend"/>; everything measured before is dropped.</summary>
    public Run BeginRun(PlaybackHardwareBackend backend)
    {
        lock (_gate)
        {
            _current = new Run(this, backend);
            return _current;
        }
    }

    public PlaybackTranscodeReading Read()
    {
        lock (_gate)
        {
            return _current is null ? PlaybackTranscodeReading.None : _current.Read(_time.GetUtcNow());
        }
    }

    /// <summary>One encode process's measurements; <see cref="Record"/> is what the process reports into.</summary>
    public sealed class Run
    {
        private readonly PlaybackTranscodeMeter _owner;
        private readonly PlaybackHardwareBackend _backend;
        private double? _speed;
        private double? _fps;
        private bool _warm;
        private DateTimeOffset? _lastAt;
        private DateTimeOffset? _belowRealTimeSince;
        private DateTimeOffset? _belowTargetSince;

        internal Run(PlaybackTranscodeMeter owner, PlaybackHardwareBackend backend)
        {
            _owner = owner;
            _backend = backend;
        }

        public void Record(PlaybackTranscodeSample sample)
        {
            ArgumentNullException.ThrowIfNull(sample);
            lock (_owner._gate)
            {
                if (_owner._current != this)
                {
                    return;
                }

                var now = _owner._time.GetUtcNow();
                _lastAt = now;
                _speed = sample.Speed;
                _fps = sample.Fps;
                // A report without a speed (ffmpeg printed N/A) or from the warm-up says nothing about sustainability and ends any dip.
                _warm = sample.OutputSeconds >= WarmUpOutputSeconds && sample.Speed is not null;
                if (sample.Speed is not { } speed || !_warm)
                {
                    _belowRealTimeSince = null;
                    _belowTargetSince = null;
                    return;
                }

                _belowRealTimeSince = speed < RealTimeSpeed ? _belowRealTimeSince ?? now : null;
                _belowTargetSince = speed < TargetSpeed ? _belowTargetSince ?? now : null;
            }
        }

        internal PlaybackTranscodeReading Read(DateTimeOffset now)
        {
            if (_lastAt is not { } lastAt || now - lastAt > StaleAfter)
            {
                return new PlaybackTranscodeReading(PlaybackTranscodeSpeedState.Unknown, null, null, _backend);
            }

            var state = !_warm
                ? PlaybackTranscodeSpeedState.Measuring
                : _belowRealTimeSince is { } slow && now - slow >= SustainedFor
                    ? PlaybackTranscodeSpeedState.TooSlow
                    : _belowTargetSince is { } thin && now - thin >= SustainedFor
                        ? PlaybackTranscodeSpeedState.BelowTarget
                        : _belowTargetSince is not null ? PlaybackTranscodeSpeedState.Measuring : PlaybackTranscodeSpeedState.Sustainable;
            return new PlaybackTranscodeReading(state, _speed, _fps, _backend);
        }
    }
}

/// <summary>A running ffmpeg whose <c>-progress</c> blocks are reported as they finish (on the thread that reads its stderr). Also the seam that lets tests feed canned progress.</summary>
public interface IFfmpegProgressSource
{
    event Action<PlaybackTranscodeSample>? ProgressReported;
}
