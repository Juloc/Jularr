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

/// <summary><paramref name="TierKbps"/> is the tier a step up leads to (decided here so the next plan cannot disagree), null otherwise.</summary>
public sealed record PlaybackAdaptationDecision(PlaybackAdaptationAdvice Advice, PlaybackAdaptationReason? Reason = null, int? TierKbps = null)
{
    public static PlaybackAdaptationDecision None { get; } = new(PlaybackAdaptationAdvice.None);
}

/// <summary>
/// What the session being replaced asks of the next plan of the same title: the advice it ended with (and the tier a step up leads to)
/// plus what the server learned about its own capacity and keeps for the chain of re-plans: <see cref="CeilingKbps"/> is the highest tier
/// the current encoder sustained, <see cref="SlowBackends"/> the encoders that could not keep up even at the lowest tier. Capacity facts
/// are learned at <see cref="LearnedAtUtc"/> and forgotten after <see cref="PlaybackAdaptation.CapacityMemory"/>: a busy minute must not
/// cap a title for the rest of the evening. Ephemeral like the session.
/// </summary>
public sealed record PlaybackAdaptationDirective(
    PlaybackAdaptationAdvice Advice,
    PlaybackAdaptationReason? Reason,
    int? TierKbps,
    int? CeilingKbps,
    IReadOnlyList<PlaybackHardwareBackend> SlowBackends,
    DateTimeOffset? LearnedAtUtc = null)
{
    public static PlaybackAdaptationDirective None { get; } = new(PlaybackAdaptationAdvice.None, null, null, null, [], null);

    /// <summary>The directive without capacity facts that are older than their memory.</summary>
    public PlaybackAdaptationDirective Current(DateTimeOffset now) =>
        LearnedAtUtc is { } learned && now - learned > PlaybackAdaptation.CapacityMemory ? this with { CeilingKbps = null, SlowBackends = [], LearnedAtUtc = null } : this;
}

/// <summary>Everything the adaptation reads from one session; the clock is part of the input so the rule is a pure function.</summary>
public sealed record PlaybackAdaptationInput(
    DateTimeOffset Now,
    DateTimeOffset SessionStartedAtUtc,
    PlaybackPlan Plan,
    int? CeilingKbps,
    IReadOnlyList<PlaybackTelemetry> Reports,
    int RecentStalls,
    PlaybackTranscodeSpeedState Speed);

/// <summary>
/// The server side of Automatic quality (#403): from one session's telemetry and measured transcode speed it decides whether the player
/// should step down, step up or stay. Down is quick and evidence-driven, up is slow and needs a long stable window and a plan that was
/// really held back by its tier; a change of any kind (every re-plan starts a new session) opens a cooldown during which nothing steps
/// up. The player only reports and applies; this is the one decision owner, and the next plan reads its result
/// (<see cref="PlaybackAutoQuality"/>).
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

    /// <summary>Reports further apart than this leave a hole in a stable or low window (reports come every 5 s).</summary>
    public static readonly TimeSpan MaxReportGap = TimeSpan.FromSeconds(15);

    /// <summary>The newest report must be this recent to describe the player at all.</summary>
    public static readonly TimeSpan FreshFor = PlaybackSessionTelemetry.FreshFor;

    /// <summary>A short buffer without a stall only counts when it stayed short this long while playing: a seek empties it for a moment.</summary>
    public static readonly TimeSpan LowBufferSustainedFor = TimeSpan.FromSeconds(10);

    /// <summary>How long what the server learned about its capacity (a tier ceiling, encoders that were too slow) keeps limiting a title.</summary>
    public static readonly TimeSpan CapacityMemory = TimeSpan.FromMinutes(10);

    /// <summary>A plan counts as held back by its limit when it delivers at least this share of it.</summary>
    private const double LimitedShare = 0.9;

    /// <summary>A step up must promise at least this much more delivered bitrate, or it only reloads the source for nothing.</summary>
    private const double WorthwhileGain = 1.1;

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

        var quality = input.Plan.Quality;
        if (quality.Requested != PlaybackQualityPreset.Auto || quality.DeliveredBitrateKbps is not > 0 || input.Reports.Count == 0 || input.Now - input.SessionStartedAtUtc < MinSessionAge)
        {
            return PlaybackAdaptationDecision.None;
        }

        var delivered = quality.DeliveredBitrateKbps.Value;
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

            if (IsLowBufferSustained(input.Reports, latest, delivered))
            {
                return new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.LowBuffer);
            }
        }

        return StepUpTier(input, delivered) is { } tier
            ? new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepUp, PlaybackAdaptationReason.ThroughputHeadroom, tier)
            : PlaybackAdaptationDecision.None;
    }

    /// <summary>
    /// The directive the next plan of the same title is made under. A too-slow transcode first lowers the ceiling to the tier below the one
    /// that failed; with no tier left it marks the encoder as unable to keep up so the next plan tries another backend, and with none
    /// left the plan is unavailable. Everything else only carries the advice; capacity facts learned earlier are kept until they expire.
    /// </summary>
    public static PlaybackAdaptationDirective NextDirective(
        PlaybackAdaptationDecision decision,
        PlaybackAdaptationDirective inherited,
        int? deliveredKbps,
        PlaybackHardwareBackend? encoderBackend,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(inherited);
        var directive = inherited.Current(now) with { Advice = decision.Advice, Reason = decision.Reason, TierKbps = decision.TierKbps };
        if (decision.Reason != PlaybackAdaptationReason.TranscodeTooSlow)
        {
            return directive;
        }

        directive = directive with { LearnedAtUtc = now };
        if (deliveredKbps is > 0 and var delivered && PlaybackQualityPresets.TierBelow(delivered) is { } lower)
        {
            return directive with { CeilingKbps = lower };
        }

        IReadOnlyList<PlaybackHardwareBackend> slow = encoderBackend is { } backend && !directive.SlowBackends.Contains(backend) ? [.. directive.SlowBackends, backend] : directive.SlowBackends;
        return directive with { CeilingKbps = null, SlowBackends = slow };
    }

    // A step up only makes sense for a plan that its own limit held back and that would really deliver more under the next tier (the
    // engine's own bitrate and height rules decide that, so height-capped or default-bitrate-capped plans never get advice that changes
    // nothing). Then: sustained delivery well above the next tier, a healthy buffer and not a single stall for the whole window; a hole in
    // the reports, a pause or a missing rate breaks it. The window is anchored on the newest report so a stale session never qualifies.
    private static int? StepUpTier(PlaybackAdaptationInput input, int delivered)
    {
        if (input.Now - input.SessionStartedAtUtc < Cooldown || input.RecentStalls > 0 || input.Speed is PlaybackTranscodeSpeedState.BelowTarget)
        {
            return null;
        }

        if (WorthwhileTierAbove(input.Plan, delivered) is not { } next || input.CeilingKbps is { } ceiling && next > ceiling)
        {
            return null;
        }

        var required = next * StepUpHeadroom;
        var lowWater = input.Plan.Buffer?.LowWaterSeconds ?? 0;
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
                         report.BufferAheadSeconds >= lowWater &&
                         (previousAt is not { } before || report.ReportedAtUtc - before <= MaxReportGap);
            if (!stable)
            {
                return null;
            }

            earliest ??= report.ReportedAtUtc;
            previousAt = report.ReportedAtUtc;
        }

        // The first report in the window may be one interval inside it, so a window "of 60 s" is covered when it reaches back to 55 s.
        return earliest is { } first && input.Now - first >= StepUpStableFor - TimeSpan.FromSeconds(5) ? next : null;
    }

    private static int? WorthwhileTierAbove(PlaybackPlan plan, int delivered)
    {
        var quality = plan.Quality;
        if (!plan.TranscodesVideo || plan.Video is not { SourceHeight: > 0 } video || quality.LimitKbps is not > 0 || delivered < quality.LimitKbps * LimitedShare)
        {
            return null;
        }

        var limit = quality.LimitKbps.Value;
        if (PlaybackQualityPresets.TierAbove(limit, quality.SourceBitrateKbps) is not { } next)
        {
            return null;
        }

        // The height the current limit alone would allow; when the plan is lower, something else (client decoder, server encoder) caps it
        // and keeps capping it under the next tier.
        var sourceHeight = video.SourceHeight.Value;
        var limitHeight = Math.Min(sourceHeight, PlaybackQualityPresets.StepAtOrBelow(limit).MaxHeight);
        var otherCap = video.MaxOutputHeight is { } outputHeight && outputHeight < limitHeight ? outputHeight : int.MaxValue;
        var nextHeight = Math.Min(Math.Min(sourceHeight, PlaybackQualityPresets.StepAtOrBelow(next).MaxHeight), otherCap);
        var audioKbps = plan.Audio is null ? 0 : plan.Audio is { Copy: false, BitrateKbps: { } converted } ? converted : 256;
        var wouldDeliver = PlaybackDecisionEngine.TranscodeVideoKbps(nextHeight, next, audioKbps, quality.SourceBitrateKbps) + audioKbps;
        return wouldDeliver >= delivered * WorthwhileGain ? next : null;
    }

    // A short buffer on its own says nothing (a seek empties it): it must have stayed short for a while of continuous playing reports
    // while the delivery rate is falling or below what the tier needs.
    private static bool IsLowBufferSustained(IReadOnlyList<PlaybackTelemetry> reports, PlaybackTelemetry latest, int deliveredKbps)
    {
        if (!IsFalling(reports, latest, deliveredKbps))
        {
            return false;
        }

        PlaybackTelemetry? oldest = null;
        for (var index = reports.Count - 1; index >= 0; index--)
        {
            var report = reports[index];
            if (report.State != PlaybackClientState.Playing || report.BufferAheadSeconds >= PlaybackAutoQuality.LowBufferSeconds || (oldest is not null && oldest.ReportedAtUtc - report.ReportedAtUtc > MaxReportGap))
            {
                break;
            }

            oldest = report;
        }

        return oldest is not null && latest.ReportedAtUtc - oldest.ReportedAtUtc >= LowBufferSustainedFor;
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
