using System.Text.Json;
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
/// are learned at <see cref="LearnedAtUtc"/> and forgotten after <see cref="PlaybackAdaptationPolicy.CapacityMemory"/>: a busy minute must not
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

    /// <summary>The directive without capacity facts that are older than the policy's memory.</summary>
    public PlaybackAdaptationDirective Current(DateTimeOffset now, PlaybackAdaptationPolicy policy) =>
        LearnedAtUtc is { } learned && now - learned > policy.CapacityMemory ? this with { CeilingKbps = null, SlowBackends = [], LearnedAtUtc = null } : this;
}

/// <summary>
/// Every threshold of the runtime adaptation in one place: when a step up is allowed and worth it, how a low buffer is judged, how long
/// the server remembers what it learned about its own capacity, which sessions count as load, and how often a player may follow advice
/// (the client gate, delivered to the page from here). The defaults are the binding values of the #403 policy comment; making them
/// Admin-editable settings through the one settings owner is a follow-up, not a second source.
/// </summary>
public sealed record PlaybackAdaptationPolicy(
    double StepUpHeadroom,
    TimeSpan StepUpStableFor,
    TimeSpan Cooldown,
    TimeSpan MinSessionAge,
    TimeSpan MaxReportGap,
    TimeSpan LowBufferSustainedFor,
    TimeSpan CapacityMemory,
    double LimitedShare,
    double WorthwhileGain,
    TimeSpan OverloadActivityWindow,
    TimeSpan SwitchMinInterval,
    int SwitchMaxPerWindow,
    TimeSpan SwitchWindow,
    TimeSpan SwitchBackoff)
{
    /// <summary>
    /// 1.5x the next tier's bitrate for 60 s, 120 s cooldown after any change, no advice in the first 20 s, reports within 15 s of each
    /// other, a short buffer must last 10 s, capacity facts live 10 min, a plan counts as limited above 90 % of its limit and a step up
    /// must deliver 10 % more, only sessions active in the last 90 s count as load, a player switches at most 6 times per 10 min with
    /// 30 s between switches and backs off 5 min after a plan that failed.
    /// </summary>
    public static PlaybackAdaptationPolicy Default { get; } = new(
        1.5,
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(10),
        0.9,
        1.1,
        TimeSpan.FromSeconds(90),
        TimeSpan.FromSeconds(30),
        6,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(5));

    /// <summary>The numbers the web player's advice gate runs on, so the client never carries a copy of them.</summary>
    public string ClientGateJson() =>
        JsonSerializer.Serialize(new { minIntervalMs = SwitchMinInterval.TotalMilliseconds, maxSwitches = SwitchMaxPerWindow, windowMs = SwitchWindow.TotalMilliseconds, backoffMs = SwitchBackoff.TotalMilliseconds });
}

/// <summary>Everything the adaptation reads from one session; the clock is part of the input so the rule is a pure function.</summary>
public sealed record PlaybackAdaptationInput(
    DateTimeOffset Now,
    DateTimeOffset SessionStartedAtUtc,
    PlaybackPlan Plan,
    int? CeilingKbps,
    IReadOnlyList<PlaybackTelemetry> Reports,
    int RecentStalls,
    PlaybackTranscodeSpeedState Speed,
    PlaybackAdaptationPolicy Policy);

/// <summary>
/// The server side of Automatic quality (#403): from one session's telemetry and measured transcode speed it decides whether the player
/// should step down, step up or stay. Down is quick and evidence-driven, up is slow and needs a long stable window and a plan that was
/// really held back by its tier; a change of any kind (every re-plan starts a new session) opens a cooldown during which nothing steps
/// up. The player only reports and applies; this is the one decision owner, and the next plan reads its result
/// (<see cref="PlaybackAutoQuality"/>).
/// </summary>
public static class PlaybackAdaptation
{
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
        if (quality.Requested != PlaybackQualityPreset.Auto || quality.DeliveredBitrateKbps is not > 0 || input.Reports.Count == 0 || input.Now - input.SessionStartedAtUtc < input.Policy.MinSessionAge)
        {
            return PlaybackAdaptationDecision.None;
        }

        var delivered = quality.DeliveredBitrateKbps.Value;
        var latest = input.Reports[^1];
        if (input.Now - latest.ReportedAtUtc > PlaybackSessionTelemetry.FreshFor || latest.State == PlaybackClientState.Paused)
        {
            return PlaybackAdaptationDecision.None;
        }

        if (PlaybackQualityPresets.TierBelow(delivered) is not null)
        {
            if (PlaybackAutoQuality.IsStruggling(input.RecentStalls, latest.BufferAheadSeconds))
            {
                return new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.Stalls);
            }

            if (IsLowBufferSustained(input.Reports, latest, delivered, input.Policy))
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
        DateTimeOffset now,
        PlaybackAdaptationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(inherited);
        var directive = inherited.Current(now, policy) with { Advice = decision.Advice, Reason = decision.Reason, TierKbps = decision.TierKbps };
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
        if (input.Now - input.SessionStartedAtUtc < input.Policy.Cooldown || input.RecentStalls > 0 || input.Speed is PlaybackTranscodeSpeedState.BelowTarget)
        {
            return null;
        }

        if (WorthwhileTierAbove(input.Plan, delivered, input.Policy) is not { } next || input.CeilingKbps is { } ceiling && next > ceiling)
        {
            return null;
        }

        var required = next * input.Policy.StepUpHeadroom;
        var lowWater = input.Plan.Buffer?.LowWaterSeconds ?? 0;
        var windowStart = input.Now - input.Policy.StepUpStableFor;
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
                         (previousAt is not { } before || report.ReportedAtUtc - before <= input.Policy.MaxReportGap);
            if (!stable)
            {
                return null;
            }

            earliest ??= report.ReportedAtUtc;
            previousAt = report.ReportedAtUtc;
        }

        // The first report in the window may be one interval inside it, so a window "of 60 s" is covered when it reaches back to 55 s.
        return earliest is { } first && input.Now - first >= input.Policy.StepUpStableFor - TimeSpan.FromSeconds(5) ? next : null;
    }

    private static int? WorthwhileTierAbove(PlaybackPlan plan, int delivered, PlaybackAdaptationPolicy policy)
    {
        var quality = plan.Quality;
        if (!plan.TranscodesVideo || plan.Video is not { SourceHeight: > 0 } video || quality.LimitKbps is not > 0 || delivered < quality.LimitKbps * policy.LimitedShare)
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
        return wouldDeliver >= delivered * policy.WorthwhileGain ? next : null;
    }

    // A short buffer on its own says nothing (a seek empties it): it must have stayed short for a while of continuous playing reports
    // while the delivery rate is falling or below what the tier needs.
    private static bool IsLowBufferSustained(IReadOnlyList<PlaybackTelemetry> reports, PlaybackTelemetry latest, int deliveredKbps, PlaybackAdaptationPolicy policy)
    {
        if (!IsFalling(reports, latest, deliveredKbps))
        {
            return false;
        }

        PlaybackTelemetry? oldest = null;
        for (var index = reports.Count - 1; index >= 0; index--)
        {
            var report = reports[index];
            if (report.State != PlaybackClientState.Playing || report.BufferAheadSeconds >= PlaybackAutoQuality.LowBufferSeconds || (oldest is not null && oldest.ReportedAtUtc - report.ReportedAtUtc > policy.MaxReportGap))
            {
                break;
            }

            oldest = report;
        }

        return oldest is not null && latest.ReportedAtUtc - oldest.ReportedAtUtc >= policy.LowBufferSustainedFor;
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
