using System.Text.Json.Serialization;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>What the player was doing when it reported; only a player that plays or waits for media counts as using the session.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackClientState>))]
public enum PlaybackClientState
{
    Playing,
    Buffering,
    Paused
}

/// <summary>
/// One telemetry report as a client sends it. Every field is nullable only so a missing one is a precise validation
/// failure instead of a silent zero; <see cref="PlaybackTelemetryRules.TryValidate"/> turns it into a <see cref="PlaybackTelemetry"/>.
/// <see cref="ThroughputKbps"/> is the rate at which the client received media, null when it could not measure one.
/// </summary>
public sealed record PlaybackTelemetryUpdate(
    long? Sequence,
    PlaybackClientState? State,
    double? BufferAheadSeconds,
    int? ThroughputKbps,
    int? StallCount,
    long? StallTotalMs,
    double? PositionSeconds);

public sealed record PlaybackTelemetry(
    long Sequence,
    DateTimeOffset ReportedAtUtc,
    PlaybackClientState State,
    double BufferAheadSeconds,
    int? ThroughputKbps,
    int StallCount,
    long StallTotalMs,
    double PositionSeconds);

/// <summary>What storing a report did: nothing (a repeat or an older one), a recorded report, or a recorded report that shows the player is really playing or waiting.</summary>
public enum PlaybackTelemetryOutcome
{
    Ignored,
    Recorded,
    ShowsProgress
}

/// <summary>The runtime evidence a replaced session leaves for the next plan: the buffer it last reported and the stalls of the last minute.</summary>
public sealed record PlaybackTelemetryEvidence(double? BufferSeconds, int RecentStalls);

public static class PlaybackTelemetryRules
{
    /// <summary>A report is a few dozen bytes of JSON; anything larger is not one.</summary>
    public const int MaxBodyBytes = 1024;

    public const double MaxBufferAheadSeconds = 3600;
    public const int MaxThroughputKbps = 10_000_000;
    public const int MaxStallCount = 100_000;
    public const long MaxStallTotalMs = 7L * 24 * 60 * 60 * 1000;
    public const double MaxPositionSeconds = 7 * 24 * 60 * 60;

    /// <summary>Validates one report; false when a value is missing, not finite or outside what a real player can observe.</summary>
    public static bool TryValidate(PlaybackTelemetryUpdate update, DateTimeOffset receivedAtUtc, out PlaybackTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(update);
        telemetry = null!;
        if (update is not { Sequence: long sequence and >= 0, State: PlaybackClientState state, BufferAheadSeconds: double buffer } || !Enum.IsDefined(state))
        {
            return false;
        }

        if (update is not { StallCount: int stallCount and >= 0 and <= MaxStallCount, StallTotalMs: long stallTotalMs and >= 0 and <= MaxStallTotalMs, PositionSeconds: double position })
        {
            return false;
        }

        if (!double.IsFinite(buffer) || buffer is < 0 or > MaxBufferAheadSeconds || !double.IsFinite(position) || position is < 0 or > MaxPositionSeconds)
        {
            return false;
        }

        if (update.ThroughputKbps is < 0 or > MaxThroughputKbps)
        {
            return false;
        }

        telemetry = new PlaybackTelemetry(sequence, receivedAtUtc, state, buffer, update.ThroughputKbps, stallCount, stallTotalMs, position);
        return true;
    }
}

/// <summary>
/// The ephemeral telemetry of one playback session: the latest report plus the few stall-count changes needed to tell the
/// stalls of the last minute from older ones. Nothing here is persisted; it lives and dies with the session.
/// </summary>
public sealed class PlaybackSessionTelemetry
{
    /// <summary>Stalls within this window decide a step down (the existing two-stall rule).</summary>
    public static readonly TimeSpan RecentStallWindow = TimeSpan.FromSeconds(60);

    /// <summary>A reported buffer older than this no longer describes the player.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    /// <summary>A session whose last report is older than this leaves no evidence for the next plan; the request's own hints decide.</summary>
    public static readonly TimeSpan EvidenceFreshFor = TimeSpan.FromMinutes(2);

    /// <summary>Waiting for media with no position advance extends the session for at most this long; a dead stream cannot hold it forever.</summary>
    public static readonly TimeSpan MaxBufferingExtension = TimeSpan.FromMinutes(2);

    /// <summary>Backward seeks that may re-anchor the progress mark within <see cref="RewindWindow"/>; more are not a viewer rewinding.</summary>
    private const int MaxRewindsPerWindow = 3;

    private static readonly TimeSpan RewindWindow = TimeSpan.FromMinutes(10);

    // The position moves at most at the fastest offered speed, twice over for the timing jitter of reports; a larger step since the previous
    // report is a seek (or a made-up position), not playback.
    private static readonly double s_maxPlaybackRate = PlayerDesign.PlaybackSpeeds.Max() * 2;
    private const double PositionSlackSeconds = 1;

    // The 60 second window and the endpoint's rate limit already keep this far below the cap; it only bounds a flood within one instant.
    private const int MaxStallChanges = 128;

    /// <summary>How far back the recent reports reach: the 60 s window of a step up plus the cooldown's margin; older ones decide nothing.</summary>
    private static readonly TimeSpan s_historyWindow = TimeSpan.FromMinutes(3);

    // Reports come every 5 s (about 36 in the window); the cap only bounds a client that reports faster than it should.
    private const int MaxHistory = 64;

    private readonly Lock _gate = new();
    private readonly List<(DateTimeOffset At, int Count)> _stallChanges = [];
    private readonly Queue<DateTimeOffset> _rewinds = new();
    private readonly List<PlaybackTelemetry> _history = [];
    private PlaybackTelemetry? _latest;
    private double _progressMark;
    private TimeSpan _bufferingExtension;

    public PlaybackTelemetry? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>
    /// Stores the report when it is newer than the stored one. Reports may arrive twice or out of order; the invariant is that
    /// the highest sequence wins and anything not newer is ignored, so repeating a request changes nothing.
    /// A recorded report <see cref="PlaybackTelemetryOutcome.ShowsProgress"/> only when the player is not paused and either played
    /// forward at a believable speed beyond the progress mark, or waited for media (stall time growing no faster than the clock) for
    /// less than <see cref="MaxBufferingExtension"/> in total since it last played. A step that is no playback speed is a seek: it only
    /// moves the mark, a backward one at most <c>MaxRewindsPerWindow</c> times per window, so a viewer can rewind but a client
    /// alternating positions cannot keep a session alive; a loop of identical reports shows no progress either.
    /// </summary>
    public PlaybackTelemetryOutcome Apply(PlaybackTelemetry report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            var previous = _latest;
            if (previous is not null && report.Sequence <= previous.Sequence)
            {
                return PlaybackTelemetryOutcome.Ignored;
            }

            if (report.StallCount != (_stallChanges.Count == 0 ? 0 : _stallChanges[^1].Count))
            {
                _stallChanges.Add((report.ReportedAtUtc, report.StallCount));
                var cutoff = report.ReportedAtUtc - RecentStallWindow;
                // One change at or before the cutoff stays: it is the stall count the window started with.
                while (_stallChanges.Count > 1 && _stallChanges[1].At <= cutoff)
                {
                    _stallChanges.RemoveAt(0);
                }

                if (_stallChanges.Count > MaxStallChanges)
                {
                    _stallChanges.RemoveAt(0);
                }
            }

            _latest = report;
            _history.Add(report);
            var historyCutoff = report.ReportedAtUtc - s_historyWindow;
            _history.RemoveAll(x => x.ReportedAtUtc < historyCutoff);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveRange(0, _history.Count - MaxHistory);
            }

            // A paused report still moves the progress mark (a seek while paused) but never extends the session.
            var shows = ShowsProgress(previous, report);
            return shows && report.State != PlaybackClientState.Paused ? PlaybackTelemetryOutcome.ShowsProgress : PlaybackTelemetryOutcome.Recorded;
        }
    }

    private bool ShowsProgress(PlaybackTelemetry? previous, PlaybackTelemetry report)
    {
        if (previous is null)
        {
            _progressMark = report.PositionSeconds;
            return true;
        }

        var elapsed = report.ReportedAtUtc - previous.ReportedAtUtc;
        var step = report.PositionSeconds - previous.PositionSeconds;
        if (step < -PositionSlackSeconds || step > elapsed.TotalSeconds * s_maxPlaybackRate + PositionSlackSeconds)
        {
            AnchorAfterSeek(report, step < 0);
            return false;
        }

        if (report.PositionSeconds > _progressMark)
        {
            _progressMark = report.PositionSeconds;
            _bufferingExtension = TimeSpan.Zero;
            return true;
        }

        // No new media played: waiting for it counts for a while (the clock-bound stall time proves the player is really waiting).
        var stalled = report.StallTotalMs - previous.StallTotalMs;
        if (report.State == PlaybackClientState.Buffering && stalled > 0 && stalled <= elapsed.TotalMilliseconds + 1000 && _bufferingExtension + elapsed <= MaxBufferingExtension)
        {
            _bufferingExtension += elapsed;
            return true;
        }

        return false;
    }

    private void AnchorAfterSeek(PlaybackTelemetry report, bool backward)
    {
        if (!backward)
        {
            _progressMark = report.PositionSeconds;
            return;
        }

        while (_rewinds.Count > 0 && report.ReportedAtUtc - _rewinds.Peek() > RewindWindow)
        {
            _rewinds.Dequeue();
        }

        if (_rewinds.Count < MaxRewindsPerWindow)
        {
            _rewinds.Enqueue(report.ReportedAtUtc);
            _progressMark = report.PositionSeconds;
        }
    }

    /// <summary>The recent reports, oldest first; what the runtime adaptation reads. A copy, so it cannot change while it is evaluated.</summary>
    public IReadOnlyList<PlaybackTelemetry> Recent()
    {
        lock (_gate)
        {
            return [.. _history];
        }
    }

    /// <summary>What a plan that replaces this session learns from it; null when the player never reported or reported too long ago.</summary>
    public PlaybackTelemetryEvidence? Evidence(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_latest is not { } latest || now - latest.ReportedAtUtc > EvidenceFreshFor)
            {
                return null;
            }

            var cutoff = now - RecentStallWindow;
            var atCutoff = 0;
            foreach (var (at, count) in _stallChanges)
            {
                if (at > cutoff)
                {
                    break;
                }

                atCutoff = count;
            }

            return new PlaybackTelemetryEvidence(now - latest.ReportedAtUtc <= FreshFor ? latest.BufferAheadSeconds : null, Math.Max(0, latest.StallCount - atCutoff));
        }
    }
}
