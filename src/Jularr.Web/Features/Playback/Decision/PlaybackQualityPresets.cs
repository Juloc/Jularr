using System.Globalization;
using System.Text.Json.Serialization;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// The quality a user asks for. Original never limits; a bitrate preset limits the delivered
/// stream to that bitrate; Automatic lets network evidence decide. A preset never raises a
/// stream above its source.
/// </summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackQualityPreset>))]
public enum PlaybackQualityPreset
{
    Auto,
    Original,
    Mbps20,
    Mbps12,
    Mbps8,
    Mbps4,
    Mbps2,
    Mbps1
}

[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackNetworkClass>))]
public enum PlaybackNetworkClass
{
    Unknown,
    Local,
    Remote,
    Metered
}

/// <summary>
/// What the client measured about its connection. Throughput is the sustained download rate
/// the client observed (a startup probe or the running stream), not a browser guess.
/// </summary>
public sealed record PlaybackNetworkConditions(
    PlaybackNetworkClass Class = PlaybackNetworkClass.Unknown,
    int? EstimatedThroughputKbps = null,
    double? BufferSeconds = null,
    int RecentStalls = 0);

public sealed record PlaybackQualityStep(
    PlaybackQualityPreset Preset,
    string Name,
    int BitrateKbps,
    int MaxHeight);

public static class PlaybackQualityPresets
{
    /// <summary>Bitrate ladder from the highest to the lowest preset.</summary>
    public static IReadOnlyList<PlaybackQualityStep> Ladder { get; } =
    [
        new(PlaybackQualityPreset.Mbps20, "20mbps", 20_000, 2160),
        new(PlaybackQualityPreset.Mbps12, "12mbps", 12_000, 1080),
        new(PlaybackQualityPreset.Mbps8, "8mbps", 8_000, 1080),
        new(PlaybackQualityPreset.Mbps4, "4mbps", 4_000, 720),
        new(PlaybackQualityPreset.Mbps2, "2mbps", 2_000, 720),
        new(PlaybackQualityPreset.Mbps1, "1mbps", 1_000, 480)
    ];

    public static IReadOnlyList<string> Names { get; } =
        ["auto", "original", .. Ladder.Select(x => x.Name)];

    /// <summary>A remote client without measurements starts here instead of at the source bitrate.</summary>
    public const int RemoteStartKbps = 8_000;

    /// <summary>A metered connection without measurements starts here.</summary>
    public const int MeteredStartKbps = 4_000;

    /// <summary>Share of the measured throughput a stream may use; the rest absorbs variance.</summary>
    public const double ThroughputHeadroom = 0.7;

    public static string Name(PlaybackQualityPreset preset) =>
        preset switch
        {
            PlaybackQualityPreset.Auto => "auto",
            PlaybackQualityPreset.Original => "original",
            _ => Step(preset)!.Name
        };

    public static PlaybackQualityStep? Step(PlaybackQualityPreset preset) =>
        Ladder.FirstOrDefault(x => x.Preset == preset);

    /// <summary>
    /// Parses a preset name. The legacy height caps (1080p, 720p, low) of the first native
    /// contract map onto the ladder step whose bitrate they used.
    /// </summary>
    public static bool TryParse(string? value, out PlaybackQualityPreset preset)
    {
        var name = value?.Trim().ToLowerInvariant();
        switch (name)
        {
            case null or "" or "auto":
                preset = PlaybackQualityPreset.Auto;
                return true;
            case "original":
                preset = PlaybackQualityPreset.Original;
                return true;
            case "1080p":
                preset = PlaybackQualityPreset.Mbps8;
                return true;
            case "720p":
                preset = PlaybackQualityPreset.Mbps4;
                return true;
            case "low":
                preset = PlaybackQualityPreset.Mbps2;
                return true;
        }

        var step = Ladder.FirstOrDefault(x => x.Name == name);
        preset = step?.Preset ?? PlaybackQualityPreset.Auto;
        return step is not null;
    }

    /// <summary>The ladder step at or below a bitrate; the lowest step when nothing fits.</summary>
    public static PlaybackQualityStep StepAtOrBelow(int kbps) =>
        Ladder.FirstOrDefault(x => x.BitrateKbps <= kbps) ?? Ladder[^1];

    /// <summary>A delivered bitrate this close under a rung (the audio and rate-control slack of a transcode) is still that rung.</summary>
    private const double RungSlack = 0.85;

    /// <summary>The highest rung strictly below a delivered bitrate (what a step down limits to); null at the bottom of the ladder.</summary>
    public static int? TierBelow(int deliveredKbps) =>
        Ladder.FirstOrDefault(x => x.BitrateKbps < deliveredKbps)?.BitrateKbps;

    /// <summary>
    /// The tier above a delivered bitrate (what a step up limits to): the next rung, or the source itself when it lies below that rung.
    /// Null when the delivery is already at the source or at the top rung of a source that is not known to be higher. Never above the source.
    /// </summary>
    public static int? TierAbove(int deliveredKbps, int? sourceKbps)
    {
        var atLeast = deliveredKbps / RungSlack;
        if (sourceKbps is { } source && source <= atLeast)
        {
            return null;
        }

        var rung = Ladder.Where(x => x.BitrateKbps > atLeast).Select(x => (int?)x.BitrateKbps).LastOrDefault();
        if (rung is null)
        {
            return sourceKbps;
        }

        return sourceKbps is { } known && known < rung ? known : rung;
    }

    /// <summary>The default preset for a network when the user did not pick one for this session.</summary>
    public static PlaybackQualityPreset DefaultFor(PlaybackNetworkClass network) =>
        network == PlaybackNetworkClass.Local
            ? PlaybackQualityPreset.Original
            : PlaybackQualityPreset.Auto;

    public static string FormatKbps(int kbps) =>
        kbps >= 1000
            ? (kbps / 1000d).ToString(kbps % 1000 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " Mbps"
            : kbps.ToString(CultureInfo.InvariantCulture) + " kbps";
}

[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackLimitSource>))]
public enum PlaybackLimitSource
{
    None,
    Preset,
    Network,
    NetworkDefault,
    Stalls,

    /// <summary>The server's encoder could not keep up with a higher tier (measured transcode speed).</summary>
    TranscodeSpeed,

    /// <summary>A step up the ladder: the measured delivery rate has had room for the next tier for a long stable window.</summary>
    Headroom
}

/// <summary>The bitrate a delivered stream must not exceed, and where that limit came from.</summary>
public sealed record PlaybackBitrateLimit(
    int? MaxKbps,
    PlaybackLimitSource Source,
    int? MaxHeight = null)
{
    public static PlaybackBitrateLimit None { get; } = new(null, PlaybackLimitSource.None);
}

/// <summary>
/// Automatic quality V1: one dynamically chosen target. It starts from the measured throughput
/// (or a conservative default off the home network), steps one ladder rung down after repeated
/// stalls and never asks for more than the evidence supports. The contract only carries a
/// single target, so a later multi-rendition ladder can replace this without a new API.
/// The directive of the session being replaced (<see cref="PlaybackAdaptation"/>) adds the runtime rules: a step up one rung after a long
/// stable window, and the ceiling a transcode that could not keep up leaves for the rest of the title's playback.
/// </summary>
public static class PlaybackAutoQuality
{
    public const int StallsBeforeStepDown = 2;
    public const double LowBufferSeconds = 4;

    /// <summary>The existing stall rule, shared with the runtime advice: two stalls in the last minute, or a short buffer after any.</summary>
    public static bool IsStruggling(int recentStalls, double? bufferSeconds) =>
        recentStalls >= StallsBeforeStepDown || bufferSeconds is { } buffer && buffer < LowBufferSeconds && recentStalls > 0;

    public static PlaybackBitrateLimit Resolve(
        PlaybackQualityPreset preset,
        PlaybackNetworkConditions network,
        int? currentTargetKbps = null,
        PlaybackAdaptationDirective? adaptation = null)
    {
        var limit = preset == PlaybackQualityPreset.Original
            ? PlaybackBitrateLimit.None
            : PlaybackQualityPresets.Step(preset) is { } step
                ? new PlaybackBitrateLimit(step.BitrateKbps, PlaybackLimitSource.Preset, step.MaxHeight)
                : ResolveAutomatic(network, currentTargetKbps, adaptation);

        // What the server's encoder sustained is a capacity fact, not a preference: it caps every selection, a fixed tier included.
        return adaptation?.CeilingKbps is { } ceiling && (limit.MaxKbps is null || ceiling < limit.MaxKbps)
            ? new PlaybackBitrateLimit(ceiling, PlaybackLimitSource.TranscodeSpeed)
            : limit;
    }

    private static PlaybackBitrateLimit ResolveAutomatic(PlaybackNetworkConditions network, int? currentTargetKbps, PlaybackAdaptationDirective? adaptation)
    {
        PlaybackBitrateLimit limit;
        if (network.EstimatedThroughputKbps is > 0 and var throughput)
        {
            var usable = (int)(throughput * PlaybackQualityPresets.ThroughputHeadroom);
            limit = network.Class == PlaybackNetworkClass.Local && usable >= 40_000
                ? PlaybackBitrateLimit.None
                : new PlaybackBitrateLimit(usable, PlaybackLimitSource.Network);
        }
        else
        {
            limit = network.Class switch
            {
                PlaybackNetworkClass.Remote => new PlaybackBitrateLimit(
                    PlaybackQualityPresets.RemoteStartKbps,
                    PlaybackLimitSource.NetworkDefault),
                PlaybackNetworkClass.Metered => new PlaybackBitrateLimit(
                    PlaybackQualityPresets.MeteredStartKbps,
                    PlaybackLimitSource.NetworkDefault),
                _ => PlaybackBitrateLimit.None
            };
        }

        // A too-slow transcode is handled by the ceiling (and a backend change keeps the tier), so only evidence of the player's own struggle steps down here.
        var struggling = IsStruggling(network.RecentStalls, network.BufferSeconds) ||
                         adaptation is { Advice: PlaybackAdaptationAdvice.StepDown, Reason: not PlaybackAdaptationReason.TranscodeTooSlow };
        if (adaptation is { Advice: PlaybackAdaptationAdvice.StepUp, TierKbps: { } up })
        {
            // The advice rests on live evidence of the delivery rate, which outranks the startup hint the limit above came from.
            limit = new PlaybackBitrateLimit(up, PlaybackLimitSource.Headroom);
        }
        else if (struggling && currentTargetKbps is > 0 and var current)
        {
            var lower = PlaybackQualityPresets.Ladder.FirstOrDefault(x => x.BitrateKbps < current)
                ?? PlaybackQualityPresets.Ladder[^1];
            if (limit.MaxKbps is null || lower.BitrateKbps < limit.MaxKbps)
            {
                limit = new PlaybackBitrateLimit(lower.BitrateKbps, PlaybackLimitSource.Stalls);
            }
        }

        return limit;
    }
}
