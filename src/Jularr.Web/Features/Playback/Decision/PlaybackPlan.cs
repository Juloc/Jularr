using System.Text.Json.Serialization;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// The outcome of one playback decision, in the order it is tried: the untouched file,
/// a lossless runtime remux (video copied, audio copied or converted), a server transcode.
/// </summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackDeliveryMode>))]
public enum PlaybackDeliveryMode
{
    DirectPlay,
    DirectStream,
    Transcode,
    Unavailable
}

/// <summary>How the delivered bytes reach the client.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackTransport>))]
public enum PlaybackTransport
{
    None,
    // The original file with HTTP range requests; seeking is native.
    File,
    // One live fragmented MP4 response; seeking restarts it at the new position.
    ProgressiveMp4,
    // fMP4 HLS segments; seeking outside the produced window restarts the session.
    Hls
}

[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackReasonSeverity>))]
public enum PlaybackReasonSeverity
{
    // Rules out a mode (e.g. the container cannot be played untouched).
    Blocker,
    // A quality or bandwidth limit that rules out a mode.
    Limit,
    // The plan works but relies on something unconfirmed or degraded.
    Warning,
    // Context such as "no server processing".
    Info
}

[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackModePreference>))]
public enum PlaybackModePreference
{
    Auto,
    // Never convert video on the server (the former "Device only").
    DirectOnly,
    // Always convert on the server, e.g. to work around a misbehaving decoder.
    AlwaysTranscode
}

/// <summary>
/// One machine-readable reason. Clients translate <see cref="Code"/> (the web player through
/// the <c>playback.reason.*</c> UI resources) and fill in <see cref="Values"/>.
/// </summary>
public sealed record PlaybackReason(
    string Code,
    PlaybackReasonSeverity Severity,
    PlaybackDeliveryMode? RulesOut = null,
    IReadOnlyDictionary<string, string>? Values = null);

public static class PlaybackReasonCodes
{
    public const string MediaUnavailable = "media_unavailable";
    public const string MediaNotAnalyzed = "media_not_analyzed";
    public const string NoVideoStream = "no_video_stream";
    public const string AudioTrackNotFound = "audio_track_not_found";
    public const string SubtitleTrackNotFound = "subtitle_track_not_found";

    public const string ContainerUnsupported = "container_unsupported";
    public const string ContainerUnconfirmed = "container_unconfirmed";
    public const string VideoCodecUnsupported = "video_codec_unsupported";
    public const string VideoCodecUnconfirmed = "video_codec_unconfirmed";
    public const string VideoBitDepthUnsupported = "video_bit_depth_unsupported";
    public const string VideoCodecTagUnsupported = "video_codec_tag_unsupported";
    public const string VideoResolutionUnsupported = "video_resolution_unsupported";
    public const string HdrUnsupported = "hdr_unsupported";
    public const string HdrUnconfirmed = "hdr_unconfirmed";
    public const string AudioCodecUnsupported = "audio_codec_unsupported";
    public const string AudioCodecUnconfirmed = "audio_codec_unconfirmed";
    public const string AudioChannelsUnsupported = "audio_channels_unsupported";
    public const string AudioTrackNeedsRemux = "audio_track_needs_remux";
    public const string SubtitleBurnIn = "subtitle_burn_in";
    public const string SubtitleStylingLost = "subtitle_styling_lost";
    public const string QualityLimit = "quality_limit";
    public const string BandwidthLimit = "bandwidth_limit";
    public const string RemoteStartLimit = "remote_start_limit";
    public const string StallLimit = "stall_limit";
    public const string ClientPlaybackFailed = "client_playback_failed";
    public const string TranscodeRequested = "transcode_requested";

    public const string DirectOnlyRequested = "direct_only_requested";
    public const string TranscodingDisabled = "transcoding_disabled";
    public const string TranscoderBusy = "transcoder_busy";
    public const string TranscodeTargetUnsupported = "transcode_target_unsupported";
    public const string NoDeliveryTransport = "no_delivery_transport";
    public const string LimitIgnoredNoTranscoder = "limit_ignored_no_transcoder";

    public const string SupportInferred = "support_inferred";
    public const string SourceBitrateUnknown = "source_bitrate_unknown";
    public const string HdrToneMapped = "hdr_tone_mapped";
    public const string HdrToneMapUnavailable = "hdr_tone_map_unavailable";
    public const string SubtitleBurnInUnavailable = "subtitle_burn_in_unavailable";
    public const string SubtitleUnsupported = "subtitle_unsupported";
    public const string AudioConverted = "audio_converted";
    public const string AudioDownmixed = "audio_downmixed";
    public const string ResolutionReduced = "resolution_reduced";
    public const string HardwareEncoder = "hardware_encoder";
    public const string HardwareEncoderSuspended = "hardware_encoder_suspended";
    public const string NoServerProcessing = "no_server_processing";
    public const string CompatibleOriginal = "compatible_original";
}

public sealed record PlaybackVideoOutput(
    bool Copy,
    string? SourceCodec,
    string OutputCodec,
    int? SourceWidth,
    int? SourceHeight,
    int? MaxOutputHeight,
    string? SourcePixelFormat,
    string? SourceDynamicRange,
    int? TargetBitrateKbps = null,
    bool TagHevcAsHvc1 = false,
    bool ToneMap = false,
    string? Encoder = null,
    int? BurnInSubtitleStreamIndex = null);

/// <summary>How the requested subtitle stream reaches the viewer.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PlaybackSubtitleDelivery>))]
public enum PlaybackSubtitleDelivery
{
    // The client draws it (text cues, or bitmaps on a native player).
    Client,
    // The server draws the picture subtitle into the transcoded video.
    BurnIn,
    // It cannot be shown in this session; playback continues without it.
    Unavailable
}

public sealed record PlaybackSubtitleOutput(
    int StreamIndex,
    string? Codec,
    string? Format,
    PlaybackSubtitleDelivery Delivery);

public sealed record PlaybackAudioOutput(
    int StreamIndex,
    bool Copy,
    string? SourceCodec,
    string OutputCodec,
    int? SourceChannels,
    int? OutputChannels,
    int? BitrateKbps,
    string? Language);

public sealed record PlaybackQualityResolution(
    PlaybackQualityPreset Requested,
    PlaybackNetworkClass Network,
    int? LimitKbps,
    PlaybackLimitSource LimitSource,
    int? SourceBitrateKbps,
    int? DeliveredBitrateKbps);

/// <summary>
/// A resolved, explainable plan for one playback session. Reasons list why the chosen mode
/// was chosen and, for anything but Direct Play, why each earlier mode was ruled out.
/// </summary>
public sealed record PlaybackPlan(
    PlaybackDeliveryMode Mode,
    PlaybackTransport Transport,
    string Container,
    PlaybackVideoOutput? Video,
    PlaybackAudioOutput? Audio,
    PlaybackQualityResolution Quality,
    IReadOnlyList<PlaybackReason> Reasons,
    PlaybackCapabilitySupport Confidence,
    string? SourceContainer = null,
    PlaybackSubtitleOutput? Subtitle = null,
    PlaybackBufferPolicy? Buffer = null)
{
    public bool UsesServerProcessing =>
        Mode is PlaybackDeliveryMode.DirectStream or PlaybackDeliveryMode.Transcode;

    public bool TranscodesVideo => Video is { Copy: false };

    public IEnumerable<PlaybackReason> WhyNot(PlaybackDeliveryMode mode) =>
        Reasons.Where(x => x.RulesOut == mode);
}
