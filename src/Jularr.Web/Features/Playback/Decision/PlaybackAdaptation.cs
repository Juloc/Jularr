using System.Text.Json.Serialization;
using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>What the server asks a player to do about the quality of a running session. Serialized as snake_case.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackAdaptationAdvice>))]
public enum PlaybackAdaptationAdvice
{
    None,
    StepDown,
    StepUp
}

/// <summary>Why the server advises a change. Serialized as snake_case, the same code the next plan's reason carries where it has one.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackAdaptationReason>))]
public enum PlaybackAdaptationReason
{
    Stalls,
    LowBuffer,
    TranscodeTooSlow,
    ThroughputHeadroom
}

public sealed record PlaybackAdaptationDecision(PlaybackAdaptationAdvice Advice, PlaybackAdaptationReason? Reason = null)
{
    public static PlaybackAdaptationDecision None { get; } = new(PlaybackAdaptationAdvice.None);
}

/// <summary>
/// What the session being replaced asks of the next plan of the same title: the advice it ended with plus what the server learned about
/// its own capacity and keeps for the whole chain of re-plans (<see cref="CeilingKbps"/> is the highest tier the current encoder
/// sustained, <see cref="SlowBackends"/> the encoders that could not keep up even at the lowest tier). Ephemeral like the session.
/// </summary>
public sealed record PlaybackAdaptationDirective(
    PlaybackAdaptationAdvice Advice,
    PlaybackAdaptationReason? Reason,
    int? CeilingKbps,
    IReadOnlyList<PlaybackHardwareBackend> SlowBackends)
{
    public static PlaybackAdaptationDirective None { get; } = new(PlaybackAdaptationAdvice.None, null, null, []);
}

/// <summary>Everything the adaptation reads from one session; the clock is part of the input so the rule is a pure function.</summary>
public sealed record PlaybackAdaptationInput(
    DateTimeOffset Now,
    DateTimeOffset SessionStartedAtUtc,
    PlaybackQualityPreset Quality,
    int? DeliveredKbps,
    int? SourceKbps,
    int LowWaterSeconds,
    int? CeilingKbps,
    IReadOnlyList<PlaybackTelemetry> Reports,
    int RecentStalls,
    PlaybackTranscodeSpeedState Speed);

/// <summary>
/// The server side of Automatic quality (#403): from one session's telemetry and measured transcode speed it decides whether the player
/// should step down, step up or stay. Down is quick and evidence-driven, up is slow and needs a long stable window; a change of any kind
/// (every re-plan starts a new session) opens a cooldown during which nothing steps up. The player only reports and applies; this is the
/// one decision owner, and the next plan reads the same rule (<see cref="PlaybackAutoQuality"/>).
/// </summary>
public static class PlaybackAdaptation
{
    /// <summary>Raising quality needs the measured delivery rate to be this many times the next tier's bitrate.</summary>
    public const double StepUpHeadroom = 1.5;

    public static readonly TimeSpan StepUpStableFor = TimeSpan.FromSeconds(60);

    /// <summary>After any change of the delivery (a new session) quality is not raised for this long.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(120);

    /// <summary>A new delivery has had no time to show evidence; advising before this would chase noise of the restart itself.</summary>
    public static readonly TimeSpan MinSessionAge = TimeSpan.FromSeconds(20);

    /// <summary>Reports further apart than this leave a hole in the stable window (reports come every 5 s).</summary>
    public static readonly TimeSpan MaxReportGap = TimeSpan.FromSeconds(15);

    /// <summary>The newest report must be this recent to describe the player at all.</summary>
    public static readonly TimeSpan FreshFor = PlaybackSessionTelemetry.FreshFor;

    private static readonly TimeSpan s_fallingLookback = TimeSpan.FromSeconds(30);

    /// <summary>A rate at or below this share of an earlier one within the lookback counts as falling.</summary>
    private const double FallingShare = 0.8;

    public static PlaybackAdaptationDecision Decide(PlaybackAdaptationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The server's own capacity wins over any selection: a transcode that stays under real time cannot be played at any buffer size.
        if (input.Speed == PlaybackTranscodeSpeedState.TooSlow)
        {
            return new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.TranscodeTooSlow);
        }

        if (input.Quality != PlaybackQualityPreset.Auto || input.DeliveredKbps is not > 0 || input.Reports.Count == 0 || input.Now - input.SessionStartedAtUtc < MinSessionAge)
        {
            return PlaybackAdaptationDecision.None;
        }

        var delivered = input.DeliveredKbps.Value;
        var latest = input.Reports[^1];
        if (input.Now - latest.ReportedAtUtc > FreshFor || latest.State == PlaybackClientState.Paused)
        {
            return PlaybackAdaptationDecision.None;
        }

        if (PlaybackQualityPresets.TierBelow(delivered) is not null)
        {
            if (PlaybackAutoQuality.IsStruggling(input.RecentStalls, latest.BufferAheadSeconds))
            {
                return new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.Stalls);
            }

            if (latest.BufferAheadSeconds < PlaybackAutoQuality.LowBufferSeconds && IsFalling(input.Reports, latest, delivered))
            {
                return new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.LowBuffer);
            }
        }

        return CanStepUp(input, delivered) ? new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepUp, PlaybackAdaptationReason.ThroughputHeadroom) : PlaybackAdaptationDecision.None;
    }

    /// <summary>
    /// The directive the next plan of the same title is made under. A too-slow transcode first lowers the ceiling to the tier below the one
    /// that failed; with no tier left it marks the encoder as unable to keep up so the next plan tries another backend, and with none
    /// left the plan is unavailable. Everything else only carries the advice; what was learned about the server's capacity is kept.
    /// </summary>
    public static PlaybackAdaptationDirective NextDirective(PlaybackAdaptationDecision decision, PlaybackAdaptationDirective inherited, int? deliveredKbps, PlaybackHardwareBackend? encoderBackend)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(inherited);
        var directive = inherited with { Advice = decision.Advice, Reason = decision.Reason };
        if (decision.Reason != PlaybackAdaptationReason.TranscodeTooSlow)
        {
            return directive;
        }

        if (deliveredKbps is > 0 and var delivered && PlaybackQualityPresets.TierBelow(delivered) is { } lower)
        {
            return directive with { CeilingKbps = lower };
        }

        IReadOnlyList<PlaybackHardwareBackend> slow = encoderBackend is { } backend && !inherited.SlowBackends.Contains(backend) ? [.. inherited.SlowBackends, backend] : inherited.SlowBackends;
        return directive with { CeilingKbps = null, SlowBackends = slow };
    }

    // Sustained delivery well above the next tier, a healthy buffer and not a single stall for the whole window; a hole in the reports,
    // a pause or a missing rate breaks it. The window is anchored on the newest report so a stale session never qualifies.
    private static bool CanStepUp(PlaybackAdaptationInput input, int delivered)
    {
        if (input.Now - input.SessionStartedAtUtc < Cooldown || input.RecentStalls > 0 || input.Speed is PlaybackTranscodeSpeedState.BelowTarget)
        {
            return false;
        }

        if (PlaybackQualityPresets.TierAbove(delivered, input.SourceKbps) is not { } next || input.CeilingKbps is { } ceiling && next > ceiling)
        {
            return false;
        }

        var required = next * StepUpHeadroom;
        var windowStart = input.Now - StepUpStableFor;
        DateTimeOffset? previousAt = null;
        DateTimeOffset? earliest = null;
        foreach (var report in input.Reports)
        {
            if (report.ReportedAtUtc < windowStart)
            {
                continue;
            }

            var stable = report.State == PlaybackClientState.Playing &&
                         report.ThroughputKbps is { } rate && rate >= required &&
                         report.BufferAheadSeconds >= input.LowWaterSeconds &&
                         (previousAt is not { } before || report.ReportedAtUtc - before <= MaxReportGap);
            if (!stable)
            {
                return false;
            }

            earliest ??= report.ReportedAtUtc;
            previousAt = report.ReportedAtUtc;
        }

        // The first report in the window may be one interval inside it, so a window "of 60 s" is covered when it reaches back to 55 s.
        return earliest is { } first && input.Now - first >= StepUpStableFor - TimeSpan.FromSeconds(5);
    }

    private static bool IsFalling(IReadOnlyList<PlaybackTelemetry> reports, PlaybackTelemetry latest, int deliveredKbps)
    {
        if (latest.ThroughputKbps is not { } rate)
        {
            return false;
        }

        if (rate < deliveredKbps)
        {
            return true;
        }

        var since = latest.ReportedAtUtc - s_fallingLookback;
        return reports.Any(report => report.ReportedAtUtc >= since && report.ThroughputKbps is { } earlier && rate <= earlier * FallingShare);
    }
}
