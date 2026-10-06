using System.Text.Json.Serialization;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>How far ahead of the playhead the player aims to keep media buffered; the Admin picks one for the whole server.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackBufferPreset>))]
public enum PlaybackBufferPreset
{
    Low,
    Normal,
    High,
    Max
}

/// <summary>
/// The runtime buffer policy of one playback plan (#403). <see cref="StartupSeconds"/> is the media a start should have ahead,
/// <see cref="TargetAheadSeconds"/> the steady-state reserve and <see cref="LowWaterSeconds"/> the reserve below which the buffer
/// counts as running low. All of it is advisory today: a browser decides by itself how far ahead it fetches (Direct Play file,
/// fragmented MP4 and native HLS alike) and a paused element loads only a couple of seconds, so a client cannot hold playback for a
/// reserve without delaying it for nothing. Diagnostics show the policy next to the buffer the player actually observed.
/// </summary>
public sealed record PlaybackBufferPolicy(PlaybackBufferPreset Preset, int StartupSeconds, int TargetAheadSeconds, int LowWaterSeconds)
{
    /// <summary>Direct Play and lossless remux start from what a browser fetches almost instantly.</summary>
    public const int DirectStartupSeconds = 3;

    /// <summary>A transcode produces its first media at encoder speed, so it waits for a larger reserve.</summary>
    public const int TranscodeStartupSeconds = 6;

    public const double LowWaterShare = 0.4;

    public static PlaybackBufferPolicy For(PlaybackBufferPreset preset, PlaybackDeliveryMode mode)
    {
        var target = TargetSeconds(preset);
        var startup = mode == PlaybackDeliveryMode.Transcode ? TranscodeStartupSeconds : DirectStartupSeconds;
        return new PlaybackBufferPolicy(preset, startup, target, (int)Math.Round(target * LowWaterShare));
    }

    public static int TargetSeconds(PlaybackBufferPreset preset) =>
        preset switch
        {
            PlaybackBufferPreset.Low => 15,
            PlaybackBufferPreset.Normal => 30,
            PlaybackBufferPreset.High => 60,
            PlaybackBufferPreset.Max => 120,
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };
}
