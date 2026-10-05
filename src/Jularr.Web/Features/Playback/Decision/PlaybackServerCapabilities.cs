using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// What the server can do for this decision right now. <see cref="ProcessingAvailable"/> is
/// false when ffmpeg is missing (no remux, no transcode); <see cref="TranscodingEnabled"/> is
/// the administrator's switch; the free slots bound concurrent encodes of the encoder's cost
/// class. <see cref="H264Encoder"/> is the detected encoder (software when no hardware passed
/// its test); <see cref="SuspendedHardware"/> names a preferred backend skipped by its breaker.
/// </summary>
public sealed record PlaybackServerCapabilities(
    bool ProcessingAvailable,
    bool TranscodingEnabled,
    int AvailableTranscodeSlots,
    string H264Encoder,
    int MaxTranscodeHeight,
    bool CanToneMap,
    bool CanBurnInSubtitles,
    PlaybackBackendSuspension? SuspendedHardware = null,
    PlaybackBufferPreset BufferPreset = PlaybackBufferPreset.Normal)
{
    public const string SoftwareH264Encoder = "libx264";

    /// <summary>Software encoding is realistic up to 1080p on a typical home server CPU; a hardware encoder keeps up with 4K.</summary>
    public const int SoftwareMaxHeight = 1080;

    public const int HardwareMaxHeight = 2160;

    public static PlaybackServerCapabilities Software(int availableSlots = 2) =>
        new(
            ProcessingAvailable: true,
            TranscodingEnabled: true,
            AvailableTranscodeSlots: availableSlots,
            H264Encoder: SoftwareH264Encoder,
            MaxTranscodeHeight: SoftwareMaxHeight,
            CanToneMap: true,
            CanBurnInSubtitles: true);
}
