using System.Net;
using System.Text.Json;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Media.Compatibility;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Tests;

[TestClass]
public sealed class PlaybackDecisionEngineTests
{
    private const PlaybackCapabilitySupport Confirmed = PlaybackCapabilitySupport.Confirmed;
    private const PlaybackCapabilitySupport Unknown = PlaybackCapabilitySupport.Unknown;
    private const PlaybackCapabilitySupport Unsupported = PlaybackCapabilitySupport.Unsupported;

    // ---- Media profiles from ffprobe fixtures ------------------------------------------------

    private static PlaybackMediaProfile H264AacMp4 { get; } =
        Media("episode.mp4", MediaProbeFixtures.H264Stereo, sizeBytes: 540_000_000); // ~3 Mbps

    private static PlaybackMediaProfile HevcHdrMkv { get; } =
        Media("episode.mkv", MediaProbeFixtures.HevcTenBitHdrMultiAudio, sizeBytes: 3_200_000_000); // ~18 Mbps

    private static PlaybackMediaProfile H264AacMkv { get; } =
        Media("episode.mkv", H264Mkv, sizeBytes: 360_000_000);

    private static PlaybackMediaProfile Hi10pMkv { get; } =
        Media("episode.mkv", H264Hi10pMkv, sizeBytes: 360_000_000);

    private static PlaybackMediaProfile Av1OpusWebm { get; } =
        Media("episode.webm", Av1Webm, sizeBytes: 200_000_000);

    // ---- Client capability documents -----------------------------------------------------

    // Chrome on hardware without HEVC decoding.
    private static ClientPlaybackCapabilities Chromium { get; } = Client(
        "web",
        [
            Container("mp4", Confirmed,
                [Video("h264", Confirmed, 8), Video("vp9", Confirmed, 8, 10), Video("av1", Confirmed, 8, 10), Video("hevc", Unsupported)],
                [Audio("aac"), Audio("mp3"), Audio("opus"), Audio("flac"), Audio("ac3", Unsupported), Audio("eac3", Unsupported)]),
            Container("webm", Confirmed, [Video("vp9", Confirmed, 8, 10), Video("av1", Confirmed, 8, 10)], [Audio("opus"), Audio("vorbis")]),
            Container("matroska", Unsupported)
        ],
        progressive: Confirmed,
        hls: Unsupported);

    // Chrome on a GPU with HEVC decoding (confirmed by MediaCapabilities).
    private static ClientPlaybackCapabilities ChromiumHevc { get; } = Chromium with
    {
        Containers =
        [
            Container("mp4", Confirmed,
                [Video("h264", Confirmed, 8), Video("hevc", Confirmed, 8, 10)],
                [Audio("aac"), Audio("mp3"), Audio("opus"), Audio("ac3", Unsupported), Audio("eac3", Unsupported)]),
            Container("matroska", Unsupported)
        ],
        Hdr = new ClientHdrCapabilities(Display: Confirmed, Hdr10: Confirmed, Hlg: Confirmed, DolbyVision: Unsupported)
    };

    // Safari / installed iOS PWA: HEVC only when tagged hvc1, native HLS, AC-3/E-AC-3.
    private static ClientPlaybackCapabilities Safari { get; } = Client(
        "pwa",
        [
            Container("mp4", Confirmed,
                [Video("h264", Confirmed, 8), Video("hevc", Confirmed, 8, 10, tags: ["hvc1"])],
                [Audio("aac"), Audio("mp3"), Audio("ac3"), Audio("eac3"), Audio("flac")]),
            Container("matroska", Unsupported)
        ],
        progressive: Unknown,
        hls: Confirmed,
        hdr: new ClientHdrCapabilities(Display: Confirmed, Hdr10: Confirmed, Hlg: Confirmed, DolbyVision: Confirmed));

    // A native Media3 client: Matroska, HEVC, AC-3/E-AC-3 passthrough, audio track switching.
    private static ClientPlaybackCapabilities Android { get; } = Client(
        "android",
        [
            Container("matroska", Confirmed,
                [Video("h264", Confirmed, 8), Video("hevc", Confirmed, 8, 10)],
                [Audio("aac"), Audio("eac3"), Audio("ac3"), Audio("opus")]),
            Container("mp4", Confirmed,
                [Video("h264", Confirmed, 8), Video("hevc", Confirmed, 8, 10)],
                [Audio("aac"), Audio("eac3"), Audio("ac3")])
        ],
        progressive: Confirmed,
        hls: Confirmed,
        hdr: new ClientHdrCapabilities(Display: Confirmed, Hdr10: Confirmed),
        features: new ClientPlatformFeatures(AudioTrackSelection: Confirmed),
        subtitles: new ClientSubtitleCapabilities(Text: Confirmed, StyledAss: Unknown, Image: Confirmed));

    // ---- Capability matrix × media profile → mode ------------------------------------------

    public static IEnumerable<object[]> Matrix =>
    [
        // client, media, expected mode, expected reason (why not Direct Play) or null
        ["chromium", "h264-mp4", PlaybackDeliveryMode.DirectPlay, null!],
        ["chromium", "h264-mkv", PlaybackDeliveryMode.DirectStream, PlaybackReasonCodes.ContainerUnsupported],
        ["chromium", "hevc-hdr-mkv", PlaybackDeliveryMode.Transcode, PlaybackReasonCodes.ContainerUnsupported],
        ["chromium", "hi10p-mkv", PlaybackDeliveryMode.Transcode, PlaybackReasonCodes.ContainerUnsupported],
        ["chromium", "av1-webm", PlaybackDeliveryMode.DirectPlay, null!],
        ["chromium-hevc", "hevc-hdr-mkv", PlaybackDeliveryMode.DirectStream, PlaybackReasonCodes.ContainerUnsupported],
        ["safari", "h264-mp4", PlaybackDeliveryMode.DirectPlay, null!],
        ["safari", "hevc-hdr-mkv", PlaybackDeliveryMode.DirectStream, PlaybackReasonCodes.ContainerUnsupported],
        ["safari", "av1-webm", PlaybackDeliveryMode.Transcode, PlaybackReasonCodes.ContainerUnsupported],
        ["android", "hevc-hdr-mkv", PlaybackDeliveryMode.DirectPlay, null!],
        ["android", "h264-mkv", PlaybackDeliveryMode.DirectPlay, null!],
        ["android", "hi10p-mkv", PlaybackDeliveryMode.Transcode, PlaybackReasonCodes.VideoBitDepthUnsupported],
        ["inferred-chromium", "h264-mp4", PlaybackDeliveryMode.DirectPlay, null!],
        ["inferred-chromium", "hevc-hdr-mkv", PlaybackDeliveryMode.Transcode, PlaybackReasonCodes.ContainerUnsupported]
    ];

    [TestMethod]
    [DynamicData(nameof(Matrix))]
    public void CapabilityMatrixDecidesModeAndExplainsWhyNotDirectPlay(
        string clientName,
        string mediaName,
        PlaybackDeliveryMode expected,
        string? whyNotDirect)
    {
        var plan = PlaybackDecisionEngine.Decide(Request(MediaNamed(mediaName), ClientNamed(clientName)));

        Assert.AreEqual(expected, plan.Mode, Describe(plan));
        if (whyNotDirect is null)
        {
            Assert.IsFalse(plan.WhyNot(PlaybackDeliveryMode.DirectPlay).Any(), Describe(plan));
            Assert.IsTrue(plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.NoServerProcessing));
        }
        else
        {
            CollectionAssert.Contains(
                plan.WhyNot(PlaybackDeliveryMode.DirectPlay).Select(x => x.Code).ToArray(),
                whyNotDirect,
                Describe(plan));
        }

        if (expected == PlaybackDeliveryMode.Transcode)
        {
            Assert.IsTrue(plan.WhyNot(PlaybackDeliveryMode.DirectStream).Any(), "A transcode explains why the remux was not enough.");
            Assert.IsFalse(plan.Video!.Copy);
            Assert.AreEqual("h264", plan.Video.OutputCodec);
        }
    }

    [TestMethod]
    public void DirectPlaySendsTheUntouchedFileWithoutServerProcessing()
    {
        var plan = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium));

        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, plan.Mode);
        Assert.AreEqual(PlaybackTransport.File, plan.Transport);
        Assert.IsFalse(plan.UsesServerProcessing);
        Assert.IsTrue(plan.Video!.Copy);
        Assert.IsTrue(plan.Audio!.Copy);
        Assert.AreEqual(PlaybackCapabilitySupport.Confirmed, plan.Confidence);
        Assert.ThrowsExactly<ArgumentException>(() => PlaybackDeliveryCommand.Progressive("/media/episode.mp4", plan, 0));
    }

    [TestMethod]
    public void DirectStreamCopiesVideoAndConvertsOnlyUnsupportedAudio()
    {
        var plan = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, ChromiumHevc));

        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, plan.Mode, Describe(plan));
        Assert.AreEqual(PlaybackTransport.ProgressiveMp4, plan.Transport);
        Assert.IsTrue(plan.Video!.Copy, "Direct Stream never re-encodes video.");
        Assert.IsTrue(plan.Video.TagHevcAsHvc1);
        Assert.IsFalse(plan.Audio!.Copy, "E-AC-3 is not decodable here.");
        Assert.AreEqual("aac", plan.Audio.OutputCodec);
        Assert.AreEqual(6, plan.Audio.OutputChannels, "5.1 stays 5.1 AAC.");
        Assert.IsTrue(plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.AudioConverted));

        var arguments = PlaybackDeliveryCommand.Progressive("/media/episode.mkv", plan, 120).ToList();
        CollectionAssert.Contains(arguments, "copy");
        Assert.AreEqual("hvc1", arguments[arguments.IndexOf("-tag:v") + 1]);
        Assert.AreEqual("0:1", arguments[arguments.LastIndexOf("-map") + 1], "The default audio stream is mapped explicitly.");
        Assert.AreEqual("aac", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.AreEqual("120", arguments[arguments.IndexOf("-ss") + 1]);
        CollectionAssert.DoesNotContain(arguments, "libx264");
        Assert.AreEqual("pipe:1", arguments[^1]);
    }

    [TestMethod]
    public void SafariGetsHlsAndKeepsCompatibleSurroundAudio()
    {
        var plan = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Safari));

        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, plan.Mode, Describe(plan));
        Assert.AreEqual(PlaybackTransport.Hls, plan.Transport);
        Assert.IsTrue(plan.Audio!.Copy, "Safari decodes E-AC-3 in MP4.");

        var arguments = PlaybackDeliveryCommand.Hls("/media/episode.mkv", plan, 0, "/data/playback-cache/hls/x").ToList();
        Assert.AreEqual("hls", arguments[arguments.IndexOf("-f") + 1]);
        Assert.AreEqual("fmp4", arguments[arguments.IndexOf("-hls_segment_type") + 1]);
        CollectionAssert.DoesNotContain(arguments, "-force_key_frames", "Copied video keeps its own keyframes.");
        Assert.AreEqual("event", arguments[arguments.IndexOf("-hls_playlist_type") + 1], "A fast remux may run ahead; nothing is dropped from the playlist.");
        Assert.AreEqual("0", arguments[arguments.IndexOf("-hls_list_size") + 1]);
        Assert.IsFalse(arguments.Any(x => x.Contains("delete_segments", StringComparison.Ordinal)));

        var playlist = PlaybackDeliveryCommand.StartAtBeginning("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-PLAYLIST-TYPE:EVENT\n");
        StringAssert.StartsWith(playlist, "#EXTM3U\n#EXT-X-START:TIME-OFFSET=0,PRECISE=YES\n#EXT-X-VERSION:7");
        Assert.AreEqual(playlist, PlaybackDeliveryCommand.StartAtBeginning(playlist), "Injected once.");
    }

    [TestMethod]
    public void HevcTaggedHev1InMp4NeedsALosslessRetagOnSafari()
    {
        var media = HevcHdrMkv with
        {
            Container = MediaContainerFamily.Mp4,
            Video = HevcHdrMkv.Video! with { CodecTag = "hev1" }
        };
        var plan = PlaybackDecisionEngine.Decide(Request(media, Safari));

        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, plan.Mode);
        CollectionAssert.Contains(
            plan.WhyNot(PlaybackDeliveryMode.DirectPlay).Select(x => x.Code).ToArray(),
            PlaybackReasonCodes.VideoCodecTagUnsupported);
        Assert.IsTrue(plan.Video!.TagHevcAsHvc1);
    }

    [TestMethod]
    public void TranscodeTargetsH264WithinTheEncoderAndNeverAboveTheSource()
    {
        var plan = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Chromium));

        Assert.AreEqual(PlaybackDeliveryMode.Transcode, plan.Mode, Describe(plan));
        Assert.AreEqual(1080, plan.Video!.MaxOutputHeight, "Software encoding is bounded to 1080p.");
        Assert.IsTrue(plan.Video.ToneMap, "HDR becomes SDR H.264.");
        Assert.IsTrue(plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.ResolutionReduced));
        Assert.IsTrue(plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.HdrToneMapped));
        Assert.IsTrue(plan.Video.TargetBitrateKbps <= HevcHdrMkv.OverallBitrateKbps);
        CollectionAssert.Contains(
            plan.WhyNot(PlaybackDeliveryMode.DirectStream).Select(x => x.Code).ToArray(),
            PlaybackReasonCodes.VideoCodecUnsupported);

        var arguments = PlaybackDeliveryCommand.Progressive("/media/episode.mkv", plan, 0).ToList();
        Assert.AreEqual("libx264", arguments[arguments.IndexOf("-c:v") + 1]);
        var filter = arguments[arguments.IndexOf("-vf") + 1];
        StringAssert.StartsWith(filter, PlaybackDeliveryCommand.ToneMapFilter);
        StringAssert.Contains(filter, "scale=-2:min(ih\\,1080)");
        StringAssert.EndsWith(filter, "format=yuv420p");
        Assert.AreEqual($"{plan.Video.TargetBitrateKbps}k", arguments[arguments.IndexOf("-b:v") + 1]);
    }

    [TestMethod]
    public void ALowSourceBitrateIsNeverRaisedByATranscode()
    {
        var plan = PlaybackDecisionEngine.Decide(Request(Hi10pMkv, Chromium));

        Assert.AreEqual(PlaybackDeliveryMode.Transcode, plan.Mode);
        Assert.AreEqual(Hi10pMkv.OverallBitrateKbps, plan.Video!.TargetBitrateKbps);
        Assert.AreEqual(720, plan.Video.MaxOutputHeight, "The 720p source is not upscaled.");
        Assert.IsFalse(plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.ResolutionReduced));
    }

    // ---- Quality and bandwidth ----------------------------------------------------------------

    [TestMethod]
    public void PresetBelowTheSourceBitrateTranscodesAndExplainsTheLimit()
    {
        var plan = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, ChromiumHevc) with
        {
            Quality = PlaybackQualityPreset.Mbps4
        });

        Assert.AreEqual(PlaybackDeliveryMode.Transcode, plan.Mode, Describe(plan));
        var limit = plan.WhyNot(PlaybackDeliveryMode.DirectStream).Single(x => x.Code == PlaybackReasonCodes.QualityLimit);
        Assert.AreEqual("4 Mbps", limit.Values!["limit"]);
        Assert.AreEqual(720, plan.Video!.MaxOutputHeight);
        Assert.IsTrue(plan.Video.TargetBitrateKbps + (plan.Audio?.BitrateKbps ?? 0) <= 4_000);
        Assert.AreEqual(4_000, plan.Quality.LimitKbps);
    }

    [TestMethod]
    public void PresetAboveTheSourceBitrateKeepsDirectPlay()
    {
        foreach (var preset in new[] { PlaybackQualityPreset.Original, PlaybackQualityPreset.Mbps20, PlaybackQualityPreset.Mbps4 })
        {
            var plan = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with { Quality = preset });
            Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, plan.Mode, $"{preset}: a 3 Mbps source fits.");
        }

        var capped = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with { Quality = PlaybackQualityPreset.Mbps2 });
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, capped.Mode);
        Assert.AreEqual(720, capped.Video!.MaxOutputHeight);
    }

    [TestMethod]
    public void AutomaticQualityFollowsTheNetwork()
    {
        var local = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Android) with
        {
            Network = new PlaybackNetworkConditions(PlaybackNetworkClass.Local)
        });
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, local.Mode, "Home network without evidence keeps the original.");

        var remoteUnmeasured = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Android) with
        {
            Network = new PlaybackNetworkConditions(PlaybackNetworkClass.Remote)
        });
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, remoteUnmeasured.Mode, "Remote starts conservatively.");
        Assert.IsTrue(remoteUnmeasured.WhyNot(PlaybackDeliveryMode.DirectPlay).Any(x => x.Code == PlaybackReasonCodes.RemoteStartLimit));

        var remoteFast = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Android) with
        {
            Network = new PlaybackNetworkConditions(PlaybackNetworkClass.Remote, EstimatedThroughputKbps: 60_000)
        });
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, remoteFast.Mode, "Measured throughput supports the original.");

        var remoteSlow = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Android) with
        {
            Network = new PlaybackNetworkConditions(PlaybackNetworkClass.Remote, EstimatedThroughputKbps: 5_000)
        });
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, remoteSlow.Mode);
        var bandwidth = remoteSlow.WhyNot(PlaybackDeliveryMode.DirectPlay).Single(x => x.Code == PlaybackReasonCodes.BandwidthLimit);
        Assert.AreEqual("3.5 Mbps", bandwidth.Values!["limit"], "70 % of the measured 5 Mbps.");
        Assert.IsTrue(remoteSlow.Quality.DeliveredBitrateKbps <= 3_500);
    }

    [TestMethod]
    public void RepeatedStallsStepDownOneLadderRung()
    {
        var limit = PlaybackAutoQuality.Resolve(
            PlaybackQualityPreset.Auto,
            new PlaybackNetworkConditions(PlaybackNetworkClass.Remote, 20_000, 1, RecentStalls: 2),
            currentTargetKbps: 8_000);
        Assert.AreEqual(4_000, limit.MaxKbps);
        Assert.AreEqual(PlaybackLimitSource.Stalls, limit.Source);

        var calm = PlaybackAutoQuality.Resolve(
            PlaybackQualityPreset.Auto,
            new PlaybackNetworkConditions(PlaybackNetworkClass.Remote, 20_000, 30, RecentStalls: 0),
            currentTargetKbps: 8_000);
        Assert.AreEqual(14_000, calm.MaxKbps);

        Assert.AreEqual(
            PlaybackLimitSource.Preset,
            PlaybackAutoQuality.Resolve(PlaybackQualityPreset.Mbps8, new PlaybackNetworkConditions(RecentStalls: 5), 8_000).Source,
            "A manual preset is the user's choice; stalls do not override it.");
    }

    [TestMethod]
    [DataRow(PlaybackBufferPreset.Low, 15, 6)]
    [DataRow(PlaybackBufferPreset.Normal, 30, 12)]
    [DataRow(PlaybackBufferPreset.High, 60, 24)]
    [DataRow(PlaybackBufferPreset.Max, 120, 48)]
    public void EveryPlanCarriesTheServerBufferPresetWithItsTargetAndLowWaterMark(PlaybackBufferPreset preset, int target, int lowWater)
    {
        var server = PlaybackServerCapabilities.Software() with { BufferPreset = preset };

        var plan = PlaybackDecisionEngine.Decide(new PlaybackDecisionRequest(H264AacMp4, Chromium, server));

        Assert.AreEqual(new PlaybackBufferPolicy(preset, PlaybackBufferPolicy.DirectStartupSeconds, target, lowWater), plan.Buffer);
    }

    [TestMethod]
    public void TheStartupBufferIsLargerOnlyWhenTheServerEncodesVideo()
    {
        var directPlay = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium));
        var remux = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, ChromiumHevc));
        var transcode = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with { ModePreference = PlaybackModePreference.AlwaysTranscode });

        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, directPlay.Mode);
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, remux.Mode);
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, transcode.Mode);
        Assert.AreEqual(3, directPlay.Buffer!.StartupSeconds);
        Assert.AreEqual(3, remux.Buffer!.StartupSeconds);
        Assert.AreEqual(6, transcode.Buffer!.StartupSeconds);
        Assert.AreEqual(PlaybackBufferPreset.Normal, transcode.Buffer.Preset, "Normal is the default preset.");
    }

    [TestMethod]
    public void APlanThatCannotPlayHasNoBufferPolicy()
    {
        var everyMode = new HashSet<PlaybackDeliveryMode> { PlaybackDeliveryMode.DirectPlay, PlaybackDeliveryMode.DirectStream, PlaybackDeliveryMode.Transcode };

        var plan = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with { FailedModes = everyMode });

        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, plan.Mode);
        Assert.IsNull(plan.Buffer);
    }

    [TestMethod]
    public void TheBufferPolicyIsPartOfThePlanContractInSnakeCase()
    {
        var plan = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with { Server = PlaybackServerCapabilities.Software() with { BufferPreset = PlaybackBufferPreset.High } });

        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"buffer\":{\"preset\":\"high\",\"startupSeconds\":3,\"targetAheadSeconds\":60,\"lowWaterSeconds\":24}");
    }

    [TestMethod]
    public void QualityPresetNamesIncludeLegacyCaps()
    {
        CollectionAssert.AreEqual(
            new[] { "auto", "original", "20mbps", "12mbps", "8mbps", "4mbps", "2mbps", "1mbps" },
            PlaybackQualityPresets.Names.ToArray());
        Assert.IsTrue(PlaybackQualityPresets.TryParse("720p", out var legacy));
        Assert.AreEqual(PlaybackQualityPreset.Mbps4, legacy);
        Assert.IsTrue(PlaybackQualityPresets.TryParse("12MBPS", out var twelve));
        Assert.AreEqual(PlaybackQualityPreset.Mbps12, twelve);
        Assert.IsFalse(PlaybackQualityPresets.TryParse("4k", out _));
        Assert.AreEqual(PlaybackQualityPreset.Original, PlaybackQualityPresets.DefaultFor(PlaybackNetworkClass.Local));
        Assert.AreEqual(PlaybackQualityPreset.Auto, PlaybackQualityPresets.DefaultFor(PlaybackNetworkClass.Remote));
    }

    // ---- Selections, failures and preferences ------------------------------------------------

    [TestMethod]
    public void NonDefaultAudioTrackNeedsARemuxUnlessTheClientSwitchesTracks()
    {
        var web = PlaybackDecisionEngine.Decide(Request(H264AacMp4 with
        {
            Audio = [.. H264AacMp4.Audio, new PlaybackAudioStreamProfile(2, "aac", 2, "eng", null, false)]
        }, Chromium) with { AudioStreamIndex = 2 });
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, web.Mode);
        CollectionAssert.Contains(
            web.WhyNot(PlaybackDeliveryMode.DirectPlay).Select(x => x.Code).ToArray(),
            PlaybackReasonCodes.AudioTrackNeedsRemux);
        Assert.AreEqual(2, web.Audio!.StreamIndex);

        var android = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Android) with { AudioStreamIndex = 2 });
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, android.Mode, "A native player switches tracks itself.");

        var missing = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with { AudioStreamIndex = 9 });
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, missing.Mode);
        Assert.AreEqual(PlaybackReasonCodes.AudioTrackNotFound, missing.Reasons.Single().Code);
    }

    [TestMethod]
    public void ImageSubtitlesAreBurnedInOnlyWhenTheClientCannotDrawThem()
    {
        var web = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, ChromiumHevc) with { SubtitleStreamIndex = 4 });
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, web.Mode, Describe(web));
        CollectionAssert.Contains(
            web.WhyNot(PlaybackDeliveryMode.DirectStream).Select(x => x.Code).ToArray(),
            PlaybackReasonCodes.SubtitleBurnIn);
        Assert.AreEqual(4, web.Video!.BurnInSubtitleStreamIndex);
        var arguments = PlaybackDeliveryCommand.Progressive("/media/episode.mkv", web, 0).ToList();
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        StringAssert.StartsWith(graph, $"[0:v:0]{PlaybackDeliveryCommand.ToneMapFilter}[base];", "HDR is tone mapped before the subtitle is drawn.");
        StringAssert.Contains(graph, $"[0:4]scale={web.Video.SourceWidth}:{web.Video.SourceHeight}[sub];[base][sub]overlay=eof_action=pass,");
        StringAssert.EndsWith(graph, "format=yuv420p[vout]");
        Assert.AreEqual(PlaybackSubtitleDelivery.BurnIn, web.Subtitle!.Delivery);
        Assert.AreEqual("[vout]", arguments[arguments.IndexOf("-filter_complex") + 3]);
        CollectionAssert.DoesNotContain(arguments, "-vf");

        var android = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Android) with { SubtitleStreamIndex = 4 });
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, android.Mode);

        var text = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, ChromiumHevc with
        {
            Subtitles = new ClientSubtitleCapabilities(Text: Confirmed, StyledAss: Unsupported)
        }) with { SubtitleStreamIndex = 3 });
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, text.Mode, "Text subtitles are drawn by the client.");
        Assert.IsTrue(text.Reasons.Any(x => x.Code == PlaybackReasonCodes.SubtitleStylingLost));
    }

    // ---- Subtitle burn-in matrix: format × client × server ------------------------------------

    private static PlaybackMediaProfile WithSubtitle(string codec, bool isText) =>
        H264AacMp4 with { Subtitles = [new PlaybackSubtitleStreamProfile(2, codec, "eng", isText, false, false)] };

    public static IEnumerable<object[]> SubtitleMatrix =>
    [
        // codec, client, expected mode, expected subtitle delivery
        ["hdmv_pgs_subtitle", "chromium", PlaybackDeliveryMode.Transcode, PlaybackSubtitleDelivery.BurnIn],
        ["dvd_subtitle", "chromium", PlaybackDeliveryMode.Transcode, PlaybackSubtitleDelivery.BurnIn],
        ["dvb_subtitle", "chromium", PlaybackDeliveryMode.Transcode, PlaybackSubtitleDelivery.BurnIn],
        ["xsub", "safari", PlaybackDeliveryMode.Transcode, PlaybackSubtitleDelivery.BurnIn],
        ["hdmv_pgs_subtitle", "android", PlaybackDeliveryMode.DirectPlay, PlaybackSubtitleDelivery.Client],
        ["subrip", "chromium", PlaybackDeliveryMode.DirectPlay, PlaybackSubtitleDelivery.Client],
        ["webvtt", "chromium", PlaybackDeliveryMode.DirectPlay, PlaybackSubtitleDelivery.Client],
        ["ass", "chromium", PlaybackDeliveryMode.DirectPlay, PlaybackSubtitleDelivery.Client],
        ["mov_text", "safari", PlaybackDeliveryMode.DirectPlay, PlaybackSubtitleDelivery.Client],
        ["dvb_teletext", "chromium", PlaybackDeliveryMode.DirectPlay, PlaybackSubtitleDelivery.Unavailable],
        ["arib_caption", "android", PlaybackDeliveryMode.DirectPlay, PlaybackSubtitleDelivery.Unavailable]
    ];

    [TestMethod]
    [DynamicData(nameof(SubtitleMatrix))]
    public void PictureSubtitlesAreBurnedInTextStaysClientSideAndUnknownFormatsNeverBreakPlayback(
        string codec,
        string clientName,
        PlaybackDeliveryMode expectedMode,
        PlaybackSubtitleDelivery expectedDelivery)
    {
        var isText = Jularr.Web.Features.Subtitles.SubtitleFormats.IsText(codec);
        var plan = PlaybackDecisionEngine.Decide(Request(WithSubtitle(codec, isText), ClientNamed(clientName)) with
        {
            SubtitleStreamIndex = 2
        });

        Assert.AreEqual(expectedMode, plan.Mode, Describe(plan));
        Assert.AreEqual(expectedDelivery, plan.Subtitle!.Delivery, Describe(plan));
        Assert.AreEqual(2, plan.Subtitle.StreamIndex);
        Assert.AreEqual(expectedDelivery == PlaybackSubtitleDelivery.BurnIn ? 2 : null, plan.Video?.BurnInSubtitleStreamIndex);
        if (expectedDelivery == PlaybackSubtitleDelivery.BurnIn)
        {
            CollectionAssert.Contains(plan.WhyNot(PlaybackDeliveryMode.DirectPlay).Select(x => x.Code).ToArray(), PlaybackReasonCodes.SubtitleBurnIn);
            var arguments = PlaybackDeliveryCommand.Progressive("/media/episode.mp4", plan, 12.5).ToList();
            var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
            Assert.AreEqual(
                $"[0:2]scale={plan.Video!.SourceWidth}:{plan.Video.SourceHeight}[sub];[0:v:0][sub]overlay=eof_action=pass,scale=-2:min(ih\\,{plan.Video.MaxOutputHeight}),format=yuv420p[vout]",
                graph);
            CollectionAssert.Contains(arguments, "-sn", "Only the burned-in picture carries the subtitle.");
        }

        if (expectedDelivery == PlaybackSubtitleDelivery.Unavailable)
        {
            Assert.IsTrue(plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.SubtitleUnsupported), Describe(plan));
        }
    }

    [TestMethod]
    public void APictureSubtitleThatCannotBeBurnedInPlaysTheEpisodeWithoutIt()
    {
        var pgs = WithSubtitle("hdmv_pgs_subtitle", isText: false);
        foreach (var (name, request) in new (string, PlaybackDecisionRequest)[]
                 {
                     ("direct only", Request(pgs, Chromium) with { SubtitleStreamIndex = 2, ModePreference = PlaybackModePreference.DirectOnly }),
                     ("transcoding off", Request(pgs, Chromium) with { SubtitleStreamIndex = 2, Server = PlaybackServerCapabilities.Software() with { TranscodingEnabled = false } }),
                     ("transcoder busy", Request(pgs, Chromium) with { SubtitleStreamIndex = 2, Server = PlaybackServerCapabilities.Software(availableSlots: 0) }),
                     ("no burn-in filter", Request(pgs, Chromium) with { SubtitleStreamIndex = 2, Server = PlaybackServerCapabilities.Software() with { CanBurnInSubtitles = false } })
                 })
        {
            var plan = PlaybackDecisionEngine.Decide(request);
            Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, plan.Mode, $"{name}: {Describe(plan)}");
            Assert.AreEqual(PlaybackSubtitleDelivery.Unavailable, plan.Subtitle!.Delivery, name);
            Assert.IsNull(plan.Video?.BurnInSubtitleStreamIndex, name);
            Assert.IsTrue(plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.SubtitleBurnInUnavailable && x.Severity == PlaybackReasonSeverity.Warning), name);
        }

        var none = PlaybackDecisionEngine.Decide(Request(pgs, Chromium));
        Assert.IsNull(none.Subtitle, "Without a requested subtitle the plan says nothing about subtitles.");
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, none.Mode);
    }

    [TestMethod]
    public void BurnInGraphWithoutKnownSourceSizeOverlaysTheCanvasAsIs()
    {
        var video = new PlaybackVideoOutput(false, "h264", "h264", null, null, 720, "yuv420p", "SDR");
        Assert.AreEqual(
            "[0:v:0][0:5]overlay=eof_action=pass,format=yuv420p[vout]",
            PlaybackDeliveryCommand.BurnInFilter(video, 5, ["format=yuv420p"]));
    }

    [TestMethod]
    public void ClientFailureReportsMoveToTheNextMode()
    {
        var failedDirect = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with
        {
            FailedModes = new HashSet<PlaybackDeliveryMode> { PlaybackDeliveryMode.DirectPlay }
        });
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, failedDirect.Mode);
        CollectionAssert.Contains(
            failedDirect.WhyNot(PlaybackDeliveryMode.DirectPlay).Select(x => x.Code).ToArray(),
            PlaybackReasonCodes.ClientPlaybackFailed);

        var failedBoth = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with
        {
            FailedModes = new HashSet<PlaybackDeliveryMode> { PlaybackDeliveryMode.DirectPlay, PlaybackDeliveryMode.DirectStream }
        });
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, failedBoth.Mode);

        var failedAll = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with
        {
            FailedModes = new HashSet<PlaybackDeliveryMode>
            {
                PlaybackDeliveryMode.DirectPlay,
                PlaybackDeliveryMode.DirectStream,
                PlaybackDeliveryMode.Transcode
            }
        });
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, failedAll.Mode);
    }

    [TestMethod]
    public void DirectOnlyNeverConvertsVideoAndAlwaysTranscodeSkipsTheOriginal()
    {
        var directOnly = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Chromium) with
        {
            ModePreference = PlaybackModePreference.DirectOnly
        });
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, directOnly.Mode, "HEVC cannot play here without conversion.");
        Assert.IsTrue(directOnly.WhyNot(PlaybackDeliveryMode.Transcode).Any(x => x.Code == PlaybackReasonCodes.DirectOnlyRequested));

        var directOnlyRemux = PlaybackDecisionEngine.Decide(Request(H264AacMkv, Chromium) with
        {
            ModePreference = PlaybackModePreference.DirectOnly
        });
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, directOnlyRemux.Mode, "A lossless remux is still allowed.");

        var always = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with
        {
            ModePreference = PlaybackModePreference.AlwaysTranscode
        });
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, always.Mode);
        Assert.IsTrue(always.WhyNot(PlaybackDeliveryMode.DirectPlay).Any(x => x.Code == PlaybackReasonCodes.TranscodeRequested));
    }

    [TestMethod]
    public void WithoutAFreeTranscoderALimitNeverStopsPlayback()
    {
        var busy = PlaybackServerCapabilities.Software(availableSlots: 0);
        var limited = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium) with
        {
            Server = busy,
            Quality = PlaybackQualityPreset.Mbps1
        });
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, limited.Mode);
        Assert.IsTrue(limited.Reasons.Any(x => x.Code == PlaybackReasonCodes.LimitIgnoredNoTranscoder));
        Assert.IsTrue(limited.WhyNot(PlaybackDeliveryMode.Transcode).Any(x => x.Code == PlaybackReasonCodes.TranscoderBusy));

        var incompatible = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Chromium) with { Server = busy });
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, incompatible.Mode, "An incompatible codec with no transcoder cannot play.");

        var disabled = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Chromium) with
        {
            Server = PlaybackServerCapabilities.Software() with { TranscodingEnabled = false }
        });
        Assert.IsTrue(disabled.WhyNot(PlaybackDeliveryMode.Transcode).Any(x => x.Code == PlaybackReasonCodes.TranscodingDisabled));
    }

    [TestMethod]
    public void UnconfirmedSupportIsExplainedInsteadOfAssumed()
    {
        var unknownHevc = ChromiumHevc with
        {
            Containers =
            [
                Container("mp4", Confirmed, [Video("h264", Confirmed, 8), Video("hevc", Unknown)], [Audio("aac")])
            ]
        };
        var media = HevcHdrMkv with { Container = MediaContainerFamily.Mp4, Audio = [new PlaybackAudioStreamProfile(1, "aac", 2, "jpn", null, true)] };
        var plan = PlaybackDecisionEngine.Decide(Request(media, unknownHevc));

        Assert.AreEqual(PlaybackDeliveryMode.Transcode, plan.Mode);
        CollectionAssert.Contains(
            plan.WhyNot(PlaybackDeliveryMode.DirectPlay).Select(x => x.Code).ToArray(),
            PlaybackReasonCodes.VideoCodecUnconfirmed);

        var inferred = PlaybackDecisionEngine.Decide(Request(H264AacMp4, ClientPlaybackCapabilities.InferFromUserAgent(ChromeAgent, "web")));
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, inferred.Mode);
        Assert.AreEqual(PlaybackCapabilitySupport.Inferred, inferred.Confidence);
        Assert.IsTrue(inferred.Reasons.Any(x => x.Code == PlaybackReasonCodes.SupportInferred));
    }

    [TestMethod]
    public void HdrTheClientCannotShowIsToneMappedOrKeptWithAWarning()
    {
        var sdrOnly = ChromiumHevc with { Hdr = new ClientHdrCapabilities(Display: Unsupported, Hdr10: Unsupported) };
        var plan = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, sdrOnly));
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, plan.Mode);
        CollectionAssert.Contains(plan.WhyNot(PlaybackDeliveryMode.DirectStream).Select(x => x.Code).ToArray(), PlaybackReasonCodes.HdrUnsupported);
        Assert.IsTrue(plan.Video!.ToneMap);

        var noToneMap = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, sdrOnly) with
        {
            Server = PlaybackServerCapabilities.Software() with { CanToneMap = false }
        });
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, noToneMap.Mode, "Without tone mapping the original picture is the best option.");
        Assert.IsTrue(noToneMap.Reasons.Any(x => x.Code == PlaybackReasonCodes.HdrUnsupported && x.Severity == PlaybackReasonSeverity.Warning));

        var unknownHdr = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Android with { Hdr = new ClientHdrCapabilities() }));
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, unknownHdr.Mode);
        Assert.IsTrue(unknownHdr.Reasons.Any(x => x.Code == PlaybackReasonCodes.HdrUnconfirmed));
    }

    // ---- Capability documents ---------------------------------------------------------------

    [TestMethod]
    public void CapabilityDocumentsAreBoundedAndNormalized()
    {
        var hostile = new ClientPlaybackCapabilities(
            99,
            new ClientIdentity("  ANDROID ", new string('x', 500)),
            [
                .. Enumerable.Range(0, 40).Select(_ => Container("MKV", Confirmed,
                    [.. Enumerable.Range(0, 100).Select(_ => Video("HEV1", Confirmed, 8, 10, 99))],
                    [Audio("E-AC-3")])),
                Container("../../etc", Confirmed)
            ],
            Display: new ClientDisplayInfo(-5, 100_000, 99));

        var normalized = hostile.Normalize();

        Assert.AreEqual(ClientPlaybackCapabilities.CurrentSchemaVersion, normalized.SchemaVersion);
        Assert.AreEqual("android", normalized.Client.Kind);
        Assert.AreEqual(40, normalized.Client.Name!.Length);
        var container = normalized.Containers.Single();
        Assert.AreEqual("matroska", container.Container);
        Assert.AreEqual(ClientPlaybackCapabilities.MaxCodecsPerContainer, container.Video!.Count);
        Assert.AreEqual("hevc", container.Video[0].Codec);
        CollectionAssert.AreEqual(new[] { 8, 10 }, container.Video[0].BitDepths!.ToArray());
        Assert.AreEqual("eac3", container.Audio!.Single().Codec);
        Assert.IsNull(normalized.Display!.Width);
        Assert.IsNull(normalized.Display.Height);
        Assert.IsNull(normalized.Display.PixelRatio);
    }

    [TestMethod]
    public void CapabilityDocumentsRoundTripAsSnakeCaseJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(Safari, options);
        StringAssert.Contains(json, "\"support\":\"confirmed\"");
        StringAssert.Contains(json, "\"hls\":\"confirmed\"");

        var parsed = JsonSerializer.Deserialize<ClientPlaybackCapabilities>(json, options)!;
        Assert.AreEqual(Confirmed, parsed.DeliveryOrDefault.Hls);
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, parsed)).Mode);

        var planJson = JsonSerializer.Serialize(PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Chromium)), options);
        StringAssert.Contains(planJson, "\"mode\":\"transcode\"");
        StringAssert.Contains(planJson, "\"rulesOut\":\"direct_play\"");
        StringAssert.Contains(planJson, "\"code\":\"container_unsupported\"");
    }

    [TestMethod]
    public void UserAgentOnlyPicksTheInferredBaseline()
    {
        var safari = ClientPlaybackCapabilities.InferFromUserAgent(SafariAgent, "pwa");
        Assert.IsTrue(safari.Inferred);
        Assert.AreEqual("pwa", safari.Client.Kind);
        Assert.AreEqual(PlaybackCapabilitySupport.Inferred, safari.DeliveryOrDefault.Hls);
        Assert.IsNotNull(safari.VideoCodec(MediaContainerFamily.Mp4, "hevc"));

        var chrome = ClientPlaybackCapabilities.InferFromUserAgent(ChromeAgent, "web");
        Assert.IsNull(chrome.VideoCodec(MediaContainerFamily.Mp4, "hevc"), "Hardware-dependent HEVC is never inferred.");
        Assert.AreEqual(PlaybackCapabilitySupport.Unknown, chrome.DeliveryOrDefault.Hls);

        var unknown = ClientPlaybackCapabilities.InferFromUserAgent(null, "tv-box");
        Assert.AreEqual("other", unknown.Client.Kind);
        Assert.AreEqual(1, unknown.Containers.Count(x => x.Container == "mp4"));
    }

    [TestMethod]
    public void NetworkClassUsesPrivateRangesAndMeteredHints()
    {
        Assert.AreEqual(PlaybackNetworkClass.Local, PlaybackNetworkClassifier.Classify(IPAddress.Parse("192.168.1.20"), null));
        Assert.AreEqual(PlaybackNetworkClass.Local, PlaybackNetworkClassifier.Classify(IPAddress.Parse("10.0.0.5"), null));
        Assert.AreEqual(PlaybackNetworkClass.Local, PlaybackNetworkClassifier.Classify(IPAddress.Parse("::ffff:172.20.1.1"), null));
        Assert.AreEqual(PlaybackNetworkClass.Local, PlaybackNetworkClassifier.Classify(IPAddress.Parse("fd12::1"), null));
        Assert.AreEqual(PlaybackNetworkClass.Local, PlaybackNetworkClassifier.Classify(IPAddress.Loopback, null));
        Assert.AreEqual(PlaybackNetworkClass.Remote, PlaybackNetworkClassifier.Classify(IPAddress.Parse("100.101.1.1"), null), "Overlay VPN clients may be anywhere.");
        Assert.AreEqual(PlaybackNetworkClass.Remote, PlaybackNetworkClassifier.Classify(IPAddress.Parse("203.0.113.9"), null));
        Assert.AreEqual(PlaybackNetworkClass.Remote, PlaybackNetworkClassifier.Classify(IPAddress.Parse("2001:db8::1"), null));
        Assert.AreEqual(
            PlaybackNetworkClass.Metered,
            PlaybackNetworkClassifier.Classify(IPAddress.Parse("192.168.1.20"), new PlaybackNetworkReport(ConnectionType: "cellular")));
        Assert.AreEqual(
            PlaybackNetworkClass.Metered,
            PlaybackNetworkClassifier.Classify(IPAddress.Parse("203.0.113.9"), new PlaybackNetworkReport(SaveData: true)));
        Assert.AreEqual(PlaybackNetworkClass.Unknown, PlaybackNetworkClassifier.Classify(null, null));
    }

    [TestMethod]
    public void CompatibilityIssuesListEveryProblemAndKeepTheOptimizerReason()
    {
        var media = new MediaPlaybackCharacteristics(MediaContainerFamily.Mp4, "hevc", "hev1", "yuv420p10le", "eac3");
        var issues = MediaPlaybackCompatibility.Issues(PlaybackClientProfiles.AppleWebKit, media);
        Assert.AreEqual(1, issues.Count, "E-AC-3 is fine in WebKit.");
        Assert.AreEqual(MediaCompatibilityIssueKind.CodecTag, issues[0].Kind);

        var chromium = MediaPlaybackCompatibility.Issues(PlaybackClientProfiles.Chromium, media);
        CollectionAssert.AreEqual(
            new[] { MediaCompatibilityIssueKind.VideoCodec, MediaCompatibilityIssueKind.AudioCodec },
            chromium.Select(x => x.Kind).ToArray());
        Assert.AreEqual("hevc video", MediaPlaybackCompatibility.Evaluate(PlaybackClientProfiles.Chromium, media).Reason);
    }

    [TestMethod]
    public void TranscodeSlotsAreBoundedAndReleasedOnce()
    {
        var slots = PlaybackServerTestKit.Create().Slots;
        var first = slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        var second = slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsNull(slots.TryAcquire(PlaybackCostClass.SoftwareVideo));
        Assert.AreEqual(0, slots.Available(PlaybackCostClass.SoftwareVideo));

        first!.Dispose();
        first.Dispose();
        Assert.AreEqual(1, slots.Available(PlaybackCostClass.SoftwareVideo), "A double dispose releases one slot only.");
        second!.Dispose();
        Assert.AreEqual(2, slots.Available(PlaybackCostClass.SoftwareVideo));
    }

    [TestMethod]
    public void StreamSessionsAreProfileScopedBoundedAndExpire()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-09-27T10:00:00Z"));
        var store = new PlaybackStreamSessionStore(time);
        var removed = new List<Guid>();
        store.Removed += session => removed.Add(session.Id);
        var plan = PlaybackDecisionEngine.Decide(Request(H264AacMp4, Chromium));
        var selections = new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web");

        var mine = store.Create("reader", Guid.NewGuid(), Guid.NewGuid(), "/media/a.mp4", 1440, plan, selections);
        Assert.IsNotNull(store.Get(mine.Id, "reader"));
        Assert.IsNull(store.Get(mine.Id, "other"), "Another profile never sees the session.");
        Assert.IsFalse(store.Remove(mine.Id, "other"));

        time.Advance(TimeSpan.FromSeconds(1));
        var replacement = store.Create("reader", mine.EpisodeId, mine.MediaFileId, mine.SourcePath, 1440, plan, selections, replaces: mine.Id);
        CollectionAssert.Contains(removed, mine.Id, "A re-plan replaces the previous session.");
        Assert.IsNull(store.Get(mine.Id, "reader"));

        for (var i = 0; i < PlaybackStreamSessionStore.MaxSessionsPerProfile + 2; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            store.Create("reader", Guid.NewGuid(), Guid.NewGuid(), "/media/b.mp4", 1440, plan, selections);
        }

        Assert.IsTrue(store.Count <= PlaybackStreamSessionStore.MaxSessionsPerProfile);
        CollectionAssert.Contains(removed, replacement.Id, "The oldest session of the profile is evicted.");

        time.Advance(PlaybackStreamSessionStore.IdleLifetime + TimeSpan.FromMinutes(1));
        store.CleanupExpired();
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public void HlsOutputIsReusedForTheSamePositionOnly()
    {
        var store = new PlaybackStreamSessionStore(TimeProvider.System);
        var plan = PlaybackDecisionEngine.Decide(Request(HevcHdrMkv, Safari));
        var session = store.Create(
            "reader",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/media/a.mkv",
            1420,
            plan,
            new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "pwa"));
        var hls = Guid.NewGuid();

        Assert.IsNull(session.HlsSessionAt(0));
        session.ReplaceHlsSession(hls, 120);
        Assert.AreEqual(hls, session.HlsSessionAt(120.2), "A playlist reload or probe keeps the running output.");
        Assert.IsNull(session.HlsSessionAt(300), "A seek starts a new output.");
        Assert.AreEqual(hls, session.ReplaceHlsSession(null));
        Assert.IsNull(session.HlsSessionAt(120));
    }

    [TestMethod]
    public async Task PlanServiceResolvesTheInventoryAndOpensAProfileScopedSession()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", new byte[4096]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        var store = new PlaybackStreamSessionStore(TimeProvider.System);
        var service = new PlaybackPlanService(
            fixture.Db,
            fixture.Inventory,
            store,
            PlaybackServerTestKit.Create().Capabilities);
        var input = new PlaybackPlanInput(null, "web", ChromeAgent, IPAddress.Parse("192.168.1.2"));

        var outcome = await service.PlanAsync(media.EpisodeId!.Value, "reader", input, CancellationToken.None);

        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.CapabilitiesInferred, "Without a document the user agent picks an inferred baseline.");
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, outcome.Plan.Mode);
        Assert.AreEqual(PlaybackNetworkClass.Local, outcome.Plan.Quality.Network);
        Assert.AreEqual(PlaybackQualityPreset.Original, outcome.Plan.Quality.Requested, "Home network defaults to Original.");
        Assert.AreEqual(media.Path, store.Get(outcome.Session!.Id, "reader")!.SourcePath, "The server resolves the path.");
        Assert.IsNull(store.Get(outcome.Session.Id, "other"));

        var retry = await service.PlanAsync(
            media.EpisodeId!.Value,
            "reader",
            input with
            {
                FailedModes = new HashSet<PlaybackDeliveryMode> { PlaybackDeliveryMode.Transcode },
                ReplacesSessionId = outcome.Session.Id
            },
            CancellationToken.None);
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, retry!.Plan.Mode);
        Assert.IsNull(retry.Session, "No session is opened for a plan that cannot play.");

        var remote = await service.PlanAsync(
            media.EpisodeId!.Value,
            "reader",
            input with { RemoteAddress = IPAddress.Parse("203.0.113.9") },
            CancellationToken.None);
        Assert.AreEqual(PlaybackQualityPreset.Auto, remote!.Plan.Quality.Requested, "Away from home defaults to Automatic.");

        Assert.IsNull(await service.PlanAsync(Guid.NewGuid(), "reader", input, CancellationToken.None));
    }

    [TestMethod]
    public void PlanRequestModesAcceptLegacyNames()
    {
        Assert.IsTrue(ClientApiPlaybackPlanEndpoints.TryParseMode(null, out var auto));
        Assert.AreEqual(PlaybackModePreference.Auto, auto);
        Assert.IsTrue(ClientApiPlaybackPlanEndpoints.TryParseMode("device", out var device));
        Assert.AreEqual(PlaybackModePreference.DirectOnly, device);
        Assert.IsTrue(ClientApiPlaybackPlanEndpoints.TryParseMode("always_transcode", out var always));
        Assert.AreEqual(PlaybackModePreference.AlwaysTranscode, always);
        Assert.IsFalse(ClientApiPlaybackPlanEndpoints.TryParseMode("ffmpeg -i /etc/passwd", out _));
        Assert.IsTrue(ClientApiContract.Capabilities().Features.PlaybackPlan);
        Assert.AreEqual(
            "/api/client/v1/episodes/00000000-0000-0000-0000-000000000000/playback-plan",
            ClientApiRoutes.PlaybackPlan(Guid.Empty));
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private const string ChromeAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private const string SafariAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";

    private const string H264Mkv = """
    {
      "streams": [
        { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1920, "height": 1080, "pix_fmt": "yuv420p", "disposition": { "default": 1 } },
        { "index": 1, "codec_name": "aac", "codec_type": "audio", "channels": 2, "disposition": { "default": 1 }, "tags": { "language": "jpn" } }
      ],
      "format": { "format_name": "matroska,webm", "duration": "1420.0" }
    }
    """;

    private const string H264Hi10pMkv = """
    {
      "streams": [
        { "index": 0, "codec_name": "h264", "profile": "High 10", "codec_type": "video", "width": 1280, "height": 720, "pix_fmt": "yuv420p10le", "disposition": { "default": 1 } },
        { "index": 1, "codec_name": "aac", "codec_type": "audio", "channels": 2, "disposition": { "default": 1 }, "tags": { "language": "jpn" } }
      ],
      "format": { "format_name": "matroska,webm", "duration": "1420.0" }
    }
    """;

    private const string Av1Webm = """
    {
      "streams": [
        { "index": 0, "codec_name": "av1", "codec_type": "video", "width": 1920, "height": 1080, "pix_fmt": "yuv420p10le", "disposition": { "default": 1 } },
        { "index": 1, "codec_name": "opus", "codec_type": "audio", "channels": 2, "disposition": { "default": 1 } }
      ],
      "format": { "format_name": "matroska,webm", "duration": "1420.0" }
    }
    """;

    private static PlaybackMediaProfile Media(string fileName, string probeJson, long sizeBytes) =>
        PlaybackMediaProfile.From($"/media/{fileName}", sizeBytes, MediaProbeParser.Parse(probeJson));

    private static PlaybackMediaProfile MediaNamed(string name) =>
        name switch
        {
            "h264-mp4" => H264AacMp4,
            "h264-mkv" => H264AacMkv,
            "hevc-hdr-mkv" => HevcHdrMkv,
            "hi10p-mkv" => Hi10pMkv,
            "av1-webm" => Av1OpusWebm,
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

    private static ClientPlaybackCapabilities ClientNamed(string name) =>
        name switch
        {
            "chromium" => Chromium,
            "chromium-hevc" => ChromiumHevc,
            "safari" => Safari,
            "android" => Android,
            "inferred-chromium" => ClientPlaybackCapabilities.InferFromUserAgent(ChromeAgent, "web"),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

    private static PlaybackDecisionRequest Request(PlaybackMediaProfile media, ClientPlaybackCapabilities client) =>
        new(media, client, PlaybackServerCapabilities.Software());

    private static ClientPlaybackCapabilities Client(
        string kind,
        IReadOnlyList<ClientContainerCapability> containers,
        PlaybackCapabilitySupport progressive,
        PlaybackCapabilitySupport hls,
        ClientHdrCapabilities? hdr = null,
        ClientPlatformFeatures? features = null,
        ClientSubtitleCapabilities? subtitles = null) =>
        new(
            ClientPlaybackCapabilities.CurrentSchemaVersion,
            new ClientIdentity(kind),
            containers,
            hdr ?? new ClientHdrCapabilities(Display: Unsupported, Hdr10: PlaybackCapabilitySupport.Inferred),
            new ClientDeliveryCapabilities(ProgressiveMp4: progressive, Hls: hls),
            subtitles ?? new ClientSubtitleCapabilities(Text: Confirmed, StyledAss: Unsupported, Image: Unsupported),
            features ?? new ClientPlatformFeatures(AudioTrackSelection: Unsupported));

    private static ClientContainerCapability Container(
        string name,
        PlaybackCapabilitySupport support,
        IReadOnlyList<ClientVideoCodecCapability>? video = null,
        IReadOnlyList<ClientAudioCodecCapability>? audio = null) =>
        new(name, support, video ?? [], audio ?? []);

    private static ClientVideoCodecCapability Video(
        string codec,
        PlaybackCapabilitySupport support,
        params int[] bitDepths) =>
        new(codec, support, bitDepths.Length == 0 ? null : bitDepths);

    private static ClientVideoCodecCapability Video(
        string codec,
        PlaybackCapabilitySupport support,
        int depth,
        int otherDepth,
        string[] tags) =>
        new(codec, support, [depth, otherDepth], CodecTags: tags);

    private static ClientAudioCodecCapability Audio(string codec, PlaybackCapabilitySupport support = Confirmed) =>
        new(codec, support);

    private static string Describe(PlaybackPlan plan) =>
        $"{plan.Mode}: {string.Join(", ", plan.Reasons.Select(x => $"{x.Code}/{x.Severity}/{x.RulesOut}"))}";

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan by) => current += by;
    }
}
