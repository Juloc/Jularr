using System.Globalization;
using Jularr.Web.Features.Media.Compatibility;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Features.Subtitles;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// Everything one decision needs. Track indexes are ffprobe stream indexes of the canonical
/// media inventory; null audio means the file default, null subtitle means none (or the
/// client's own learning overlay).
/// </summary>
public sealed record PlaybackDecisionRequest(
    PlaybackMediaProfile Media,
    ClientPlaybackCapabilities Client,
    PlaybackServerCapabilities Server,
    int? AudioStreamIndex = null,
    int? SubtitleStreamIndex = null,
    bool BurnInSubtitle = false,
    PlaybackQualityPreset Quality = PlaybackQualityPreset.Auto,
    PlaybackNetworkConditions? Network = null,
    PlaybackModePreference ModePreference = PlaybackModePreference.Auto,
    IReadOnlySet<PlaybackDeliveryMode>? FailedModes = null,
    int? CurrentTargetKbps = null,
    PlaybackAdaptationDirective? Adaptation = null);

/// <summary>
/// The one playback decision for every Jularr client. It walks Direct Play → Direct Stream
/// (lossless runtime remux) → Transcode and keeps a machine-readable reason for every mode it
/// rules out. Codec/container facts come from the shared compatibility model (#399) applied to
/// the client's reported capabilities, never from OS or container shortcuts.
/// </summary>
public static class PlaybackDecisionEngine
{
    // The runtime remux target: fragmented MP4, over one live response or as HLS segments.
    private const MediaContainerFamily RemuxContainer = MediaContainerFamily.Mp4;
    private const int MinimumVideoKbps = 400;

    public static PlaybackPlan Decide(PlaybackDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = DecideCore(request);
        // The source container travels with every plan so diagnostics can show source → delivered,
        // and a requested subtitle says how it reaches the viewer (client, burn-in or not at all).
        return plan with
        {
            SourceContainer = PlaybackContainerNames.Name(request.Media.Container),
            Subtitle = SubtitleOutput(request, plan),
            Buffer = plan.Mode == PlaybackDeliveryMode.Unavailable ? null : PlaybackBufferPolicy.For(request.Server.BufferPreset, plan.Mode)
        };
    }

    /// <summary>The subtitle format kind of a stream, trusting the inventory's text flag for unknown codecs.</summary>
    public static SubtitleFormatKind SubtitleKind(PlaybackSubtitleStreamProfile subtitle)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        var kind = SubtitleFormats.Classify(subtitle.Codec);
        return kind == SubtitleFormatKind.Unsupported && subtitle.IsText ? SubtitleFormatKind.Text : kind;
    }

    private static PlaybackSubtitleOutput? SubtitleOutput(PlaybackDecisionRequest request, PlaybackPlan plan)
    {
        if (request.SubtitleStreamIndex is not { } index || request.Media.SubtitleStream(index) is not { } subtitle)
        {
            return null;
        }

        var kind = SubtitleKind(subtitle);
        var delivery = kind switch
        {
            _ when plan.Mode == PlaybackDeliveryMode.Unavailable => PlaybackSubtitleDelivery.Unavailable,
            SubtitleFormatKind.Unsupported => PlaybackSubtitleDelivery.Unavailable,
            SubtitleFormatKind.Image when plan.Video?.BurnInSubtitleStreamIndex == index => PlaybackSubtitleDelivery.BurnIn,
            SubtitleFormatKind.Image when !request.BurnInSubtitle && request.Client.SubtitlesOrDefault.Image.IsUsable() =>
                PlaybackSubtitleDelivery.Client,
            SubtitleFormatKind.Image => PlaybackSubtitleDelivery.Unavailable,
            _ => PlaybackSubtitleDelivery.Client
        };

        return new PlaybackSubtitleOutput(index, subtitle.Codec, SubtitleFormats.DisplayName(subtitle.Codec), delivery);
    }

    private static PlaybackPlan DecideCore(PlaybackDecisionRequest request)
    {
        var media = request.Media;
        var client = request.Client;
        var network = request.Network ?? new PlaybackNetworkConditions();
        var failed = request.FailedModes ?? new HashSet<PlaybackDeliveryMode>();
        var sourceKbps = media.OverallBitrateKbps;
        var limit = PlaybackAutoQuality.Resolve(request.Quality, network, request.CurrentTargetKbps, request.Adaptation);
        var quality = new PlaybackQualityResolution(
            request.Quality,
            network.Class,
            limit.MaxKbps,
            limit.Source,
            sourceKbps,
            null);

        if (media.Video?.Codec is null)
        {
            return Unavailable(quality, [Reason(PlaybackReasonCodes.NoVideoStream, PlaybackReasonSeverity.Blocker)]);
        }

        PlaybackAudioStreamProfile? audio;
        if (request.AudioStreamIndex is { } audioIndex)
        {
            audio = media.AudioStream(audioIndex);
            if (audio is null)
            {
                return Unavailable(quality, [Reason(
                    PlaybackReasonCodes.AudioTrackNotFound,
                    PlaybackReasonSeverity.Blocker,
                    values: Values(("track", PlaybackTrackIds.Format(audioIndex))))]);
            }
        }
        else
        {
            audio = media.DefaultAudio;
        }

        PlaybackSubtitleStreamProfile? subtitle = null;
        if (request.SubtitleStreamIndex is { } subtitleIndex)
        {
            subtitle = media.SubtitleStream(subtitleIndex);
            if (subtitle is null)
            {
                return Unavailable(quality, [Reason(
                    PlaybackReasonCodes.SubtitleTrackNotFound,
                    PlaybackReasonSeverity.Blocker,
                    values: Values(("track", PlaybackTrackIds.Format(subtitleIndex))))]);
            }
        }

        // Text subtitles are rendered by the client from the cues API; only bitmap subtitles a
        // client cannot draw (or asks to have burned in) need the picture itself changed.
        // A format the server cannot decode is never burned in; playback continues without it.
        var subtitles = client.SubtitlesOrDefault;
        var subtitleKind = subtitle is null ? (SubtitleFormatKind?)null : SubtitleKind(subtitle);
        var burnIn = subtitleKind == SubtitleFormatKind.Image &&
                     (request.BurnInSubtitle || !subtitles.Image.IsUsable());

        if (burnIn && !request.Server.CanBurnInSubtitles)
        {
            return WithoutBurnIn(request, subtitle!);
        }

        var reasons = new List<PlaybackReason>();
        if (subtitleKind == SubtitleFormatKind.StyledText && !subtitles.StyledAss.IsUsable())
        {
            reasons.Add(Reason(
                PlaybackReasonCodes.SubtitleStylingLost,
                PlaybackReasonSeverity.Info,
                values: Values(("codec", subtitle!.Codec))));
        }
        else if (subtitleKind == SubtitleFormatKind.Unsupported)
        {
            reasons.Add(Reason(
                PlaybackReasonCodes.SubtitleUnsupported,
                PlaybackReasonSeverity.Warning,
                values: Values(("codec", subtitle!.Codec ?? "unknown"))));
        }

        // ---- Direct Play: the untouched original ------------------------------------------
        var server = request.Server;
        // HDR the client cannot show only rules a mode out when a tone-mapping transcode could
        // fix it; otherwise the untouched stream is still the best available picture.
        var hdrFixable = server.ProcessingAvailable && server.TranscodingEnabled && server.CanToneMap &&
                         request.ModePreference != PlaybackModePreference.DirectOnly;
        var profile = client.ToCompatibilityProfile();
        var directReasons = new List<PlaybackReason>();
        var directConfidence = CompatibilityReasons(
            client,
            profile,
            media,
            audio,
            media.Container,
            retagHevc: false,
            PlaybackDeliveryMode.DirectPlay,
            directReasons,
            audioIsConvertible: false,
            hdrFixable);

        if (audio is not null &&
            audio.StreamIndex != media.DefaultAudio?.StreamIndex &&
            !client.FeaturesOrDefault.AudioTrackSelection.IsUsable())
        {
            directReasons.Add(Reason(
                PlaybackReasonCodes.AudioTrackNeedsRemux,
                PlaybackReasonSeverity.Blocker,
                PlaybackDeliveryMode.DirectPlay,
                Values(("track", PlaybackTrackIds.Format(audio.StreamIndex)))));
        }

        AddCommonRules(request, failed, limit, sourceKbps, burnIn, subtitle, PlaybackDeliveryMode.DirectPlay, directReasons);
        reasons.AddRange(directReasons);

        if (!directReasons.Any(Rules))
        {
            AddConfidence(reasons, directConfidence);
            reasons.Add(Reason(PlaybackReasonCodes.CompatibleOriginal, PlaybackReasonSeverity.Info));
            reasons.Add(Reason(PlaybackReasonCodes.NoServerProcessing, PlaybackReasonSeverity.Info));
            return DirectPlay(media, audio, quality with { DeliveredBitrateKbps = sourceKbps }, reasons, directConfidence);
        }

        // ---- Direct Stream: copy video into fMP4, copy or convert audio ------------------
        var transport = ChooseTransport(client);
        var streamReasons = new List<PlaybackReason>();
        var streamAudioReasons = new List<PlaybackReason>();
        var streamConfidence = PlaybackCapabilitySupport.Confirmed;
        PlaybackAudioOutput? streamAudio = null;
        if (!server.ProcessingAvailable)
        {
            streamReasons.Add(Reason(PlaybackReasonCodes.TranscodingDisabled, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.DirectStream));
        }
        else if (transport == PlaybackTransport.None)
        {
            streamReasons.Add(Reason(PlaybackReasonCodes.NoDeliveryTransport, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.DirectStream));
        }
        else
        {
            streamConfidence = CompatibilityReasons(
                client,
                profile,
                media,
                audio,
                RemuxContainer,
                retagHevc: true,
                PlaybackDeliveryMode.DirectStream,
                streamReasons,
                audioIsConvertible: true,
                hdrFixable);
            streamAudio = PlanAudio(client, audio, limit, transcodeVideo: false, streamAudioReasons);
            AddCommonRules(request, failed, limit, sourceKbps, burnIn, subtitle, PlaybackDeliveryMode.DirectStream, streamReasons);
        }

        if (!streamReasons.Any(Rules))
        {
            reasons.AddRange(streamReasons);
            reasons.AddRange(streamAudioReasons);
            AddConfidence(reasons, streamConfidence);
            return DirectStream(media, streamAudio, transport, quality with { DeliveredBitrateKbps = sourceKbps }, reasons, streamConfidence);
        }

        // ---- Transcode: H.264 in fMP4 -------------------------------------------------------
        var transcodeReasons = new List<PlaybackReason>();
        if (request.ModePreference == PlaybackModePreference.DirectOnly)
        {
            transcodeReasons.Add(Reason(PlaybackReasonCodes.DirectOnlyRequested, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.Transcode));
        }
        else if (!server.ProcessingAvailable || !server.TranscodingEnabled)
        {
            transcodeReasons.Add(Reason(PlaybackReasonCodes.TranscodingDisabled, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.Transcode));
        }
        else if (server.AvailableTranscodeSlots <= 0)
        {
            transcodeReasons.Add(Reason(PlaybackReasonCodes.TranscoderBusy, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.Transcode));
        }
        else if (server.EncoderTooSlow)
        {
            transcodeReasons.Add(Reason(PlaybackReasonCodes.TranscodeUnsustainable, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.Transcode));
        }
        else if (transport == PlaybackTransport.None)
        {
            transcodeReasons.Add(Reason(PlaybackReasonCodes.NoDeliveryTransport, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.Transcode));
        }

        var h264 = client.VideoCodec(RemuxContainer, "h264");
        var h264Support = h264?.Support ?? PlaybackCapabilitySupport.Unknown;
        if (h264Support == PlaybackCapabilitySupport.Unsupported ||
            client.ContainerSupport(RemuxContainer) == PlaybackCapabilitySupport.Unsupported)
        {
            transcodeReasons.Add(Reason(
                PlaybackReasonCodes.TranscodeTargetUnsupported,
                PlaybackReasonSeverity.Blocker,
                PlaybackDeliveryMode.Transcode,
                Values(("codec", "h264"))));
        }

        if (failed.Contains(PlaybackDeliveryMode.Transcode))
        {
            transcodeReasons.Add(Reason(PlaybackReasonCodes.ClientPlaybackFailed, PlaybackReasonSeverity.Blocker, PlaybackDeliveryMode.Transcode));
        }

        if (!transcodeReasons.Any(Rules))
        {
            reasons.AddRange(streamReasons);
            var confidence = h264Support.IsUsable()
                ? h264Support.Weakest(client.ContainerSupport(RemuxContainer))
                : PlaybackCapabilitySupport.Unknown;
            AddConfidence(reasons, confidence);
            return Transcode(request, media, audio, subtitle, burnIn, h264, limit, transport, quality, reasons, confidence);
        }

        // ---- No transcoder: a picture subtitle never stops playback --------------------------
        if (burnIn)
        {
            return WithoutBurnIn(request, subtitle!);
        }

        // ---- No transcoder: never fail playback over a soft limit alone ---------------------
        if (directReasons.Where(Rules).All(IsSoft))
        {
            reasons.AddRange(transcodeReasons);
            reasons.Add(Reason(PlaybackReasonCodes.LimitIgnoredNoTranscoder, PlaybackReasonSeverity.Warning));
            AddConfidence(reasons, directConfidence);
            return DirectPlay(media, audio, quality with { DeliveredBitrateKbps = sourceKbps }, reasons, directConfidence);
        }

        if (streamReasons.Where(Rules).All(IsSoft) && server.ProcessingAvailable && transport != PlaybackTransport.None)
        {
            reasons.AddRange(streamReasons);
            reasons.AddRange(streamAudioReasons);
            reasons.AddRange(transcodeReasons);
            reasons.Add(Reason(PlaybackReasonCodes.LimitIgnoredNoTranscoder, PlaybackReasonSeverity.Warning));
            AddConfidence(reasons, streamConfidence);
            return DirectStream(media, streamAudio, transport, quality with { DeliveredBitrateKbps = sourceKbps }, reasons, streamConfidence);
        }

        reasons.AddRange(streamReasons);
        reasons.AddRange(transcodeReasons);
        return Unavailable(quality, reasons);
    }

    // Maps compatibility issues of the untouched file (Direct Play) or of the copied video in
    // fMP4 (Direct Stream) to reasons, and returns the weakest support the mode relies on.
    private static PlaybackCapabilitySupport CompatibilityReasons(
        ClientPlaybackCapabilities client,
        PlaybackClientProfile profile,
        PlaybackMediaProfile media,
        PlaybackAudioStreamProfile? audio,
        MediaContainerFamily container,
        bool retagHevc,
        PlaybackDeliveryMode mode,
        List<PlaybackReason> reasons,
        bool audioIsConvertible,
        bool hdrFixable)
    {
        var video = media.Video!;
        var characteristics = media.Characteristics(audio, container);
        if (retagHevc && IsHevc(video.Codec))
        {
            // A runtime remux writes HEVC as hvc1, the tag WebKit and most decoders require.
            characteristics = characteristics with { VideoCodecTag = "hvc1" };
        }

        var confidence = client.ContainerSupport(container);
        foreach (var issue in MediaPlaybackCompatibility.Issues(profile, characteristics))
        {
            switch (issue.Kind)
            {
                case MediaCompatibilityIssueKind.Container:
                    reasons.Add(Reason(
                        client.ContainerSupport(container) == PlaybackCapabilitySupport.Unknown
                            ? PlaybackReasonCodes.ContainerUnconfirmed
                            : PlaybackReasonCodes.ContainerUnsupported,
                        PlaybackReasonSeverity.Blocker,
                        mode,
                        Values(("container", PlaybackContainerNames.Name(container)))));
                    return PlaybackCapabilitySupport.Unsupported;
                case MediaCompatibilityIssueKind.VideoCodec:
                    reasons.Add(Reason(
                        client.VideoCodec(container, video.Codec)?.Support == PlaybackCapabilitySupport.Unknown
                            ? PlaybackReasonCodes.VideoCodecUnconfirmed
                            : PlaybackReasonCodes.VideoCodecUnsupported,
                        PlaybackReasonSeverity.Blocker,
                        mode,
                        Values(("codec", video.Codec), ("container", PlaybackContainerNames.Name(container)))));
                    break;
                case MediaCompatibilityIssueKind.PixelFormat:
                    reasons.Add(Reason(
                        PlaybackReasonCodes.VideoBitDepthUnsupported,
                        PlaybackReasonSeverity.Blocker,
                        mode,
                        Values(
                            ("codec", video.Codec),
                            ("bitDepth", video.BitDepth?.ToString(CultureInfo.InvariantCulture)),
                            ("pixelFormat", video.PixelFormat ?? "unknown"))));
                    break;
                case MediaCompatibilityIssueKind.CodecTag:
                    reasons.Add(Reason(
                        PlaybackReasonCodes.VideoCodecTagUnsupported,
                        PlaybackReasonSeverity.Blocker,
                        mode,
                        Values(("codec", video.Codec), ("tag", characteristics.VideoCodecTag ?? "unknown"))));
                    break;
                case MediaCompatibilityIssueKind.AudioCodec when !audioIsConvertible:
                    reasons.Add(Reason(
                        client.AudioCodec(container, audio?.Codec)?.Support == PlaybackCapabilitySupport.Unknown
                            ? PlaybackReasonCodes.AudioCodecUnconfirmed
                            : PlaybackReasonCodes.AudioCodecUnsupported,
                        PlaybackReasonSeverity.Blocker,
                        mode,
                        Values(("codec", audio?.Codec), ("container", PlaybackContainerNames.Name(container)))));
                    break;
            }
        }

        var videoCapability = client.VideoCodec(container, video.Codec);
        if (videoCapability is not null)
        {
            confidence = confidence.Weakest(videoCapability.Support);
            if (videoCapability.MaxHeight is { } maxHeight && video.Height is { } height && height > maxHeight)
            {
                reasons.Add(Reason(
                    PlaybackReasonCodes.VideoResolutionUnsupported,
                    PlaybackReasonSeverity.Blocker,
                    mode,
                    Values(
                        ("codec", video.Codec),
                        ("height", height.ToString(CultureInfo.InvariantCulture)),
                        ("maxHeight", maxHeight.ToString(CultureInfo.InvariantCulture)))));
            }
        }

        if (video.IsHdr)
        {
            var hdr = HdrSupport(client.HdrOrDefault, video.DynamicRange);
            if (hdr == PlaybackCapabilitySupport.Unsupported)
            {
                reasons.Add(hdrFixable
                    ? Reason(
                        PlaybackReasonCodes.HdrUnsupported,
                        PlaybackReasonSeverity.Blocker,
                        mode,
                        Values(("format", video.DynamicRange)))
                    : Reason(
                        PlaybackReasonCodes.HdrUnsupported,
                        PlaybackReasonSeverity.Warning,
                        values: Values(("format", video.DynamicRange))));
            }
            else if (hdr == PlaybackCapabilitySupport.Unknown && mode == PlaybackDeliveryMode.DirectPlay)
            {
                reasons.Add(Reason(
                    PlaybackReasonCodes.HdrUnconfirmed,
                    PlaybackReasonSeverity.Warning,
                    values: Values(("format", video.DynamicRange))));
            }
        }

        if (audio is not null && !audioIsConvertible)
        {
            var audioCapability = client.AudioCodec(container, audio.Codec);
            if (audioCapability is not null)
            {
                confidence = confidence.Weakest(audioCapability.Support);
                if (audioCapability.MaxChannels is { } maxChannels && audio.Channels is { } channels && channels > maxChannels)
                {
                    reasons.Add(Reason(
                        PlaybackReasonCodes.AudioChannelsUnsupported,
                        PlaybackReasonSeverity.Blocker,
                        mode,
                        Values(
                            ("codec", audio.Codec),
                            ("channels", channels.ToString(CultureInfo.InvariantCulture)),
                            ("maxChannels", maxChannels.ToString(CultureInfo.InvariantCulture)))));
                }
            }
        }

        return confidence;
    }

    // A burn-in needs a transcode; when none is possible (no transcoder, Direct only, busy) the
    // video plays as it would without the picture subtitle and the plan says it is not shown.
    private static PlaybackPlan WithoutBurnIn(PlaybackDecisionRequest request, PlaybackSubtitleStreamProfile subtitle)
    {
        var plan = DecideCore(request with { SubtitleStreamIndex = null, BurnInSubtitle = false });
        return plan with
        {
            Reasons =
            [
                .. plan.Reasons,
                Reason(
                    PlaybackReasonCodes.SubtitleBurnInUnavailable,
                    PlaybackReasonSeverity.Warning,
                    values: Values(("codec", subtitle.Codec), ("track", PlaybackTrackIds.Format(subtitle.StreamIndex))))
            ]
        };
    }

    // Rules shared by both untouched-video modes: subtitle burn-in, bitrate limits, a client
    // failure report and an explicit request to always convert.
    private static void AddCommonRules(
        PlaybackDecisionRequest request,
        IReadOnlySet<PlaybackDeliveryMode> failed,
        PlaybackBitrateLimit limit,
        int? sourceKbps,
        bool burnIn,
        PlaybackSubtitleStreamProfile? subtitle,
        PlaybackDeliveryMode mode,
        List<PlaybackReason> reasons)
    {
        if (burnIn)
        {
            reasons.Add(Reason(
                PlaybackReasonCodes.SubtitleBurnIn,
                PlaybackReasonSeverity.Blocker,
                mode,
                Values(("codec", subtitle?.Codec), ("track", subtitle is null ? null : PlaybackTrackIds.Format(subtitle.StreamIndex)))));
        }

        if (limit.MaxKbps is { } maxKbps)
        {
            if (sourceKbps is { } source && source > maxKbps)
            {
                reasons.Add(Reason(
                    LimitCode(limit.Source),
                    PlaybackReasonSeverity.Limit,
                    mode,
                    Values(
                        ("limit", PlaybackQualityPresets.FormatKbps(maxKbps)),
                        ("source", PlaybackQualityPresets.FormatKbps(source)))));
            }
            else if (sourceKbps is null && mode == PlaybackDeliveryMode.DirectPlay)
            {
                reasons.Add(Reason(PlaybackReasonCodes.SourceBitrateUnknown, PlaybackReasonSeverity.Warning));
            }
        }

        if (failed.Contains(mode))
        {
            reasons.Add(Reason(PlaybackReasonCodes.ClientPlaybackFailed, PlaybackReasonSeverity.Blocker, mode));
        }

        if (request.ModePreference == PlaybackModePreference.AlwaysTranscode)
        {
            reasons.Add(Reason(PlaybackReasonCodes.TranscodeRequested, PlaybackReasonSeverity.Limit, mode));
        }
    }

    private static string LimitCode(PlaybackLimitSource source) =>
        source switch
        {
            PlaybackLimitSource.Preset => PlaybackReasonCodes.QualityLimit,
            PlaybackLimitSource.NetworkDefault => PlaybackReasonCodes.RemoteStartLimit,
            PlaybackLimitSource.Stalls => PlaybackReasonCodes.StallLimit,
            PlaybackLimitSource.TranscodeSpeed => PlaybackReasonCodes.TranscodeTooSlow,
            PlaybackLimitSource.Headroom => PlaybackReasonCodes.QualityRaised,
            _ => PlaybackReasonCodes.BandwidthLimit
        };

    private static PlaybackAudioOutput? PlanAudio(
        ClientPlaybackCapabilities client,
        PlaybackAudioStreamProfile? audio,
        PlaybackBitrateLimit limit,
        bool transcodeVideo,
        List<PlaybackReason> reasons)
    {
        if (audio is null)
        {
            return null;
        }

        var capability = client.AudioCodec(RemuxContainer, audio.Codec);
        var lowBandwidth = limit.MaxKbps is <= 2_000;
        var channelsFit = capability?.MaxChannels is not { } max || audio.Channels is not { } source || source <= max;
        if (capability is not null && capability.Support.IsUsable() && channelsFit && !(transcodeVideo && lowBandwidth && audio.Channels > 2))
        {
            return new PlaybackAudioOutput(
                audio.StreamIndex,
                Copy: true,
                audio.Codec,
                audio.Codec ?? "aac",
                audio.Channels,
                audio.Channels,
                null,
                audio.Language);
        }

        var aacMaxChannels = client.AudioCodec(RemuxContainer, "aac")?.MaxChannels ?? 6;
        var channels = Math.Min(audio.Channels ?? 2, lowBandwidth ? 2 : Math.Min(6, aacMaxChannels));
        var bitrate = channels > 2 ? 384 : lowBandwidth ? 128 : 192;
        reasons.Add(Reason(
            PlaybackReasonCodes.AudioConverted,
            PlaybackReasonSeverity.Info,
            values: Values(("codec", audio.Codec), ("output", "aac"))));
        if (audio.Channels is { } sourceChannels && channels < sourceChannels)
        {
            reasons.Add(Reason(
                PlaybackReasonCodes.AudioDownmixed,
                PlaybackReasonSeverity.Info,
                values: Values(
                    ("channels", sourceChannels.ToString(CultureInfo.InvariantCulture)),
                    ("output", channels.ToString(CultureInfo.InvariantCulture)))));
        }

        return new PlaybackAudioOutput(
            audio.StreamIndex,
            Copy: false,
            audio.Codec,
            "aac",
            audio.Channels,
            channels,
            bitrate,
            audio.Language);
    }

    private static PlaybackPlan Transcode(
        PlaybackDecisionRequest request,
        PlaybackMediaProfile media,
        PlaybackAudioStreamProfile? audio,
        PlaybackSubtitleStreamProfile? subtitle,
        bool burnIn,
        ClientVideoCodecCapability? h264,
        PlaybackBitrateLimit limit,
        PlaybackTransport transport,
        PlaybackQualityResolution quality,
        List<PlaybackReason> reasons,
        PlaybackCapabilitySupport confidence)
    {
        var video = media.Video!;
        var server = request.Server;
        var audioOutput = PlanAudio(request.Client, audio, limit, transcodeVideo: true, reasons);
        var audioKbps = audioOutput is { Copy: false, BitrateKbps: { } converted } ? converted : audioOutput is null ? 0 : 256;

        // Height: the source, bounded by the limit's ladder step, the client decoder and
        // what the encoder handles in real time. Never above the source.
        var heightLimit = limit.MaxHeight ??
                          (limit.MaxKbps is { } kbps ? PlaybackQualityPresets.StepAtOrBelow(kbps).MaxHeight : (int?)null);
        int?[] bounds = [video.Height, heightLimit, h264?.MaxHeight, server.MaxTranscodeHeight];
        var maxHeight = bounds.Where(x => x is > 0).Select(x => x!.Value).DefaultIfEmpty(server.MaxTranscodeHeight).Min();
        if (video.Height is { } sourceHeight && maxHeight < sourceHeight)
        {
            reasons.Add(Reason(
                PlaybackReasonCodes.ResolutionReduced,
                PlaybackReasonSeverity.Info,
                values: Values(
                    ("height", sourceHeight.ToString(CultureInfo.InvariantCulture)),
                    ("output", maxHeight.ToString(CultureInfo.InvariantCulture)))));
        }

        var targetKbps = TranscodeVideoKbps(maxHeight, limit.MaxKbps, audioKbps, media.OverallBitrateKbps);

        var toneMap = video.IsHdr && server.CanToneMap;
        if (video.IsHdr)
        {
            reasons.Add(Reason(
                toneMap ? PlaybackReasonCodes.HdrToneMapped : PlaybackReasonCodes.HdrToneMapUnavailable,
                toneMap ? PlaybackReasonSeverity.Info : PlaybackReasonSeverity.Warning,
                values: Values(("format", video.DynamicRange))));
        }

        // A hardware encoder is named so Diagnostics shows what converts the video; a preferred
        // backend skipped by its circuit breaker explains why the conversion runs on something else.
        var encoderBackend = PlaybackHardwareBackends.FromEncoder(server.H264Encoder);
        if (encoderBackend != PlaybackHardwareBackend.Software)
        {
            reasons.Add(Reason(PlaybackReasonCodes.HardwareEncoder, PlaybackReasonSeverity.Info, values: Values(("backend", PlaybackHardwareBackends.DisplayName(encoderBackend)))));
        }

        if (server.SuspendedHardware is { } suspended)
        {
            reasons.Add(Reason(
                PlaybackReasonCodes.HardwareEncoderSuspended,
                PlaybackReasonSeverity.Warning,
                values: Values(("backend", PlaybackHardwareBackends.DisplayName(suspended.Backend)))));
        }

        // DecideCore only asks for a burn-in the server can do (see WithoutBurnIn).
        int? burnInIndex = burnIn && subtitle is not null ? subtitle.StreamIndex : null;

        var videoOutput = new PlaybackVideoOutput(
            Copy: false,
            video.Codec,
            "h264",
            video.Width,
            video.Height,
            maxHeight,
            video.PixelFormat,
            video.DynamicRange,
            targetKbps,
            ToneMap: toneMap,
            Encoder: server.H264Encoder,
            BurnInSubtitleStreamIndex: burnInIndex);

        return new PlaybackPlan(
            PlaybackDeliveryMode.Transcode,
            transport,
            PlaybackContainerNames.Name(RemuxContainer),
            videoOutput,
            audioOutput,
            quality with { DeliveredBitrateKbps = targetKbps + audioKbps },
            reasons,
            confidence);
    }

    /// <summary>
    /// The video bitrate a transcode targets: the height's default, within the limit once the audio took its share, and never more than the
    /// source (a transcode cannot add quality). The runtime adaptation asks the same function whether a higher tier would deliver more.
    /// </summary>
    public static int TranscodeVideoKbps(int outputHeight, int? limitKbps, int audioKbps, int? sourceKbps)
    {
        var target = DefaultVideoKbps(outputHeight);
        if (limitKbps is { } max)
        {
            target = Math.Min(target, max - audioKbps);
        }

        if (sourceKbps is { } source)
        {
            target = Math.Min(target, source);
        }

        return Math.Max(MinimumVideoKbps, target);
    }

    public static int DefaultVideoKbps(int height) =>
        height switch
        {
            >= 2160 => 30_000,
            >= 1440 => 16_000,
            >= 1080 => 10_000,
            >= 720 => 5_000,
            >= 480 => 2_500,
            _ => 1_500
        };

    private static PlaybackTransport ChooseTransport(ClientPlaybackCapabilities client)
    {
        var delivery = client.DeliveryOrDefault;
        if (delivery.Hls.IsUsable())
        {
            return PlaybackTransport.Hls;
        }

        return delivery.ProgressiveMp4 != PlaybackCapabilitySupport.Unsupported &&
               client.ContainerSupport(RemuxContainer).IsUsable()
            ? PlaybackTransport.ProgressiveMp4
            : PlaybackTransport.None;
    }

    private static PlaybackCapabilitySupport HdrSupport(ClientHdrCapabilities hdr, string? dynamicRange) =>
        dynamicRange?.ToUpperInvariant() switch
        {
            "HDR10" => hdr.Hdr10,
            "HDR10+" => hdr.Hdr10Plus > hdr.Hdr10 ? hdr.Hdr10Plus : hdr.Hdr10,
            "HLG" => hdr.Hlg,
            "DOLBY VISION" => hdr.DolbyVision,
            _ => PlaybackCapabilitySupport.Unknown
        };

    private static PlaybackPlan DirectPlay(
        PlaybackMediaProfile media,
        PlaybackAudioStreamProfile? audio,
        PlaybackQualityResolution quality,
        List<PlaybackReason> reasons,
        PlaybackCapabilitySupport confidence)
    {
        var video = media.Video!;
        return new PlaybackPlan(
            PlaybackDeliveryMode.DirectPlay,
            PlaybackTransport.File,
            PlaybackContainerNames.Name(media.Container),
            CopiedVideo(video, tagHvc1: false),
            audio is null
                ? null
                : new PlaybackAudioOutput(audio.StreamIndex, true, audio.Codec, audio.Codec ?? "", audio.Channels, audio.Channels, null, audio.Language),
            quality,
            reasons,
            confidence);
    }

    private static PlaybackPlan DirectStream(
        PlaybackMediaProfile media,
        PlaybackAudioOutput? audio,
        PlaybackTransport transport,
        PlaybackQualityResolution quality,
        List<PlaybackReason> reasons,
        PlaybackCapabilitySupport confidence) =>
        new(
            PlaybackDeliveryMode.DirectStream,
            transport,
            PlaybackContainerNames.Name(RemuxContainer),
            CopiedVideo(media.Video!, tagHvc1: IsHevc(media.Video!.Codec)),
            audio,
            quality,
            reasons,
            confidence);

    private static PlaybackVideoOutput CopiedVideo(PlaybackVideoStreamProfile video, bool tagHvc1) =>
        new(
            Copy: true,
            video.Codec,
            video.Codec ?? "",
            video.Width,
            video.Height,
            video.Height,
            video.PixelFormat,
            video.DynamicRange,
            TagHevcAsHvc1: tagHvc1);

    private static PlaybackPlan Unavailable(PlaybackQualityResolution quality, IReadOnlyList<PlaybackReason> reasons) =>
        new(
            PlaybackDeliveryMode.Unavailable,
            PlaybackTransport.None,
            "",
            null,
            null,
            quality,
            reasons,
            PlaybackCapabilitySupport.Unknown);

    private static void AddConfidence(List<PlaybackReason> reasons, PlaybackCapabilitySupport confidence)
    {
        if (confidence < PlaybackCapabilitySupport.Confirmed)
        {
            reasons.Add(Reason(
                PlaybackReasonCodes.SupportInferred,
                PlaybackReasonSeverity.Warning,
                values: Values(("support", confidence.ToString().ToLowerInvariant()))));
        }
    }

    private static bool Rules(PlaybackReason reason) =>
        reason.RulesOut is not null &&
        reason.Severity is PlaybackReasonSeverity.Blocker or PlaybackReasonSeverity.Limit;

    // A limit or an "always convert" wish is preference, not incompatibility.
    private static bool IsSoft(PlaybackReason reason) =>
        reason.Severity == PlaybackReasonSeverity.Limit;

    private static bool IsHevc(string? codec) =>
        codec is not null && PlaybackCodecNames.SameVideo(codec, "hevc");

    private static PlaybackReason Reason(
        string code,
        PlaybackReasonSeverity severity,
        PlaybackDeliveryMode? rulesOut = null,
        IReadOnlyDictionary<string, string>? values = null) =>
        new(code, severity, rulesOut, values);

    private static Dictionary<string, string> Values(params (string Key, string? Value)[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[key] = value;
            }
        }

        return result;
    }
}
