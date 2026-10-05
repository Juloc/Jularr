using System.Globalization;
using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>
/// Builds the ffmpeg arguments that execute a <see cref="PlaybackPlan"/>. Every value comes
/// from the server-side plan (stream indexes, integers) or from a fixed set (the encoder of a
/// <see cref="PlaybackHardwareBackend"/>, a validated render node); the source path is resolved
/// by the server and passed as one argument, never through a shell. This is the single owner of
/// the per-backend encoder argument sets, the hardware test encode included.
/// </summary>
public static class PlaybackDeliveryCommand
{
    public const int HlsSegmentSeconds = 4;

    // Linear-light tone mapping to BT.709 SDR; the H.264 output is always 8-bit SDR.
    public const string ToneMapFilter =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv";

    private const int ProbeBitrateKbps = 1000;

    public static IReadOnlyList<string> Progressive(
        string sourcePath,
        PlaybackPlan plan,
        double startSeconds,
        PlaybackEncoderTarget? encoder = null)
    {
        var arguments = Input(sourcePath, plan, startSeconds, encoder ?? PlaybackEncoderTarget.Software);
        arguments.AddRange([
            "-max_muxing_queue_size", "2048",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000",
            "-f", "mp4",
            "pipe:1"
        ]);
        return arguments;
    }

    /// <summary>
    /// An EVENT playlist: segments are only appended, so a player that starts at the
    /// beginning never loses its place when ffmpeg (a fast remux) runs ahead of playback.
    /// Segments far behind the player are pruned by the server
    /// (<see cref="HlsPlaybackSessionManager.PruneBehind"/>) to bound disk use.
    /// </summary>
    public static IReadOnlyList<string> Hls(
        string sourcePath,
        PlaybackPlan plan,
        double startSeconds,
        string directory,
        PlaybackEncoderTarget? encoder = null)
    {
        var arguments = Input(sourcePath, plan, startSeconds, encoder ?? PlaybackEncoderTarget.Software, overwrite: true);
        arguments.AddRange([
            "-max_muxing_queue_size", "2048",
            "-avoid_negative_ts", "make_zero",
            "-f", "hls",
            "-hls_time", HlsSegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "init.mp4",
            "-hls_flags", "independent_segments",
            "-hls_segment_filename", Path.Combine(directory, "segment-%05d.m4s"),
            Path.Combine(directory, "index.m3u8")
        ]);
        return arguments;
    }

    /// <summary>
    /// A one-second encode of a generated picture through the backend's real device setup, filter
    /// chain and encoder options, discarded into the null muxer. If this passes, a software-decoded
    /// session on the backend starts; it never decodes on the device, so it proves encoding only.
    /// </summary>
    public static IReadOnlyList<string> HardwareProbe(PlaybackEncoderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var arguments = new List<string> { "-v", "error", "-nostdin" };
        AddDeviceArguments(arguments, target, hardwareDecoding: false);
        arguments.AddRange(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=1"]);
        arguments.AddRange(["-vf", string.Join(',', SoftwareFrameTail(target.Backend))]);
        arguments.AddRange(["-frames:v", "10"]);
        AddEncoderArguments(arguments, target.Backend, ProbeBitrateKbps);
        arguments.AddRange(["-f", "null", "-"]);
        return arguments;
    }

    /// <summary>
    /// Adds <c>EXT-X-START:TIME-OFFSET=0</c> so native players begin at the start of the
    /// (still growing) playlist instead of at its live edge.
    /// </summary>
    public static string StartAtBeginning(string playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (playlist.Contains("#EXT-X-START", StringComparison.Ordinal))
        {
            return playlist;
        }

        const string header = "#EXTM3U";
        return playlist.StartsWith(header, StringComparison.Ordinal)
            ? header + "\n#EXT-X-START:TIME-OFFSET=0,PRECISE=YES" + playlist[header.Length..]
            : playlist;
    }

    private static List<string> Input(
        string sourcePath,
        PlaybackPlan plan,
        double startSeconds,
        PlaybackEncoderTarget encoder,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mode is not (PlaybackDeliveryMode.DirectStream or PlaybackDeliveryMode.Transcode) ||
            plan.Video is not { } video)
        {
            throw new ArgumentException("Only Direct Stream and Transcode plans run ffmpeg.", nameof(plan));
        }

        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        }

        var arguments = new List<string> { "-v", "error", "-nostdin" };
        if (overwrite)
        {
            arguments.Add("-y");
        }

        arguments.AddRange(["-fflags", "+genpts"]);
        if (startSeconds > 0)
        {
            arguments.AddRange(["-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        // A stream copy never touches a device; only an encode needs the backend's initialisation.
        var hardwareDecoding = !video.Copy && UsesHardwareDecoding(encoder, video);
        if (!video.Copy)
        {
            AddDeviceArguments(arguments, encoder, hardwareDecoding);
        }

        arguments.AddRange(["-i", Path.GetFullPath(sourcePath)]);

        if (video.Copy)
        {
            arguments.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
            if (video.TagHevcAsHvc1)
            {
                arguments.AddRange(["-tag:v", "hvc1"]);
            }
        }
        else
        {
            AddVideoEncode(arguments, plan, video, encoder, hardwareDecoding);
        }

        if (plan.Audio is { } audio)
        {
            arguments.AddRange(["-map", LivePlaybackCommand.AudioMap(audio.StreamIndex)]);
            if (audio.Copy)
            {
                arguments.AddRange(["-c:a", "copy"]);
            }
            else
            {
                arguments.AddRange([
                    "-c:a", "aac",
                    "-b:a", $"{(audio.BitrateKbps ?? 192).ToString(CultureInfo.InvariantCulture)}k",
                    "-ac", (audio.OutputChannels ?? 2).ToString(CultureInfo.InvariantCulture)
                ]);
            }
        }

        arguments.AddRange(["-sn", "-dn"]);
        return arguments;
    }

    /// <summary>
    /// Draws a picture subtitle (PGS, VobSub, DVB, XSUB) into the video. ffmpeg turns the bitmap
    /// stream into overlay frames; the canvas is scaled to the source picture (DVD subtitles are
    /// often 720×480 on an upscaled video) and laid over the tone-mapped picture before the
    /// output is scaled, so it always lines up and never gets HDR-processed.
    /// </summary>
    public static string BurnInFilter(PlaybackVideoOutput video, int subtitleStreamIndex, IReadOnlyList<string> output)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(subtitleStreamIndex);

        var graph = new List<string>();
        var basePad = "[0:v:0]";
        if (video.ToneMap)
        {
            graph.Add($"[0:v:0]{ToneMapFilter}[base]");
            basePad = "[base]";
        }

        var subtitlePad = $"[0:{subtitleStreamIndex.ToString(CultureInfo.InvariantCulture)}]";
        if (video is { SourceWidth: int width and > 0, SourceHeight: int height and > 0 })
        {
            graph.Add($"{subtitlePad}scale={width.ToString(CultureInfo.InvariantCulture)}:{height.ToString(CultureInfo.InvariantCulture)}[sub]");
            subtitlePad = "[sub]";
        }

        graph.Add($"{basePad}{subtitlePad}overlay=eof_action=pass,{string.Join(',', output)}[vout]");
        return string.Join(';', graph);
    }

    /// <summary>
    /// Decoding on the device is only used where it cannot go wrong or cost quality: 8-bit 4:2:0
    /// H.264 (decodable by every GPU that also encodes it) with known dimensions, and no
    /// tone mapping or burn-in, whose software filters need the picture in system memory.
    /// Everything else is decoded in software and only the encode runs on the device.
    /// </summary>
    private static bool UsesHardwareDecoding(PlaybackEncoderTarget encoder, PlaybackVideoOutput video) =>
        encoder is { HardwareDecoding: true, Backend: PlaybackHardwareBackend.Nvenc or PlaybackHardwareBackend.Qsv or PlaybackHardwareBackend.Vaapi } &&
        !video.ToneMap &&
        video.BurnInSubtitleStreamIndex is null &&
        string.Equals(video.SourceCodec, "h264", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(video.SourcePixelFormat, "yuv420p", StringComparison.OrdinalIgnoreCase) &&
        video is { SourceWidth: > 0, SourceHeight: > 0 };

    // Device initialisation and hardware-decode options are input/global options: they must precede -i.
    private static void AddDeviceArguments(List<string> arguments, PlaybackEncoderTarget target, bool hardwareDecoding)
    {
        switch (target.Backend)
        {
            case PlaybackHardwareBackend.Nvenc when hardwareDecoding:
                arguments.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
                break;
            case PlaybackHardwareBackend.Qsv:
                arguments.AddRange(["-init_hw_device", "qsv=hw"]);
                arguments.AddRange(hardwareDecoding
                    ? ["-hwaccel", "qsv", "-hwaccel_device", "hw", "-hwaccel_output_format", "qsv"]
                    : ["-filter_hw_device", "hw"]);
                break;
            case PlaybackHardwareBackend.Vaapi:
                if (!PlaybackRenderDevices.IsRenderNode(target.Device))
                {
                    throw new ArgumentException("VAAPI needs a /dev/dri/renderD node.", nameof(target));
                }

                arguments.AddRange(["-init_hw_device", $"vaapi=va:{target.Device}"]);
                arguments.AddRange(hardwareDecoding
                    ? ["-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"]
                    : ["-filter_hw_device", "va"]);
                break;
        }
    }

    // The last filters of a software-decoded chain: bring the picture to the encoder's pixel format and, where the encoder only takes device frames, upload it.
    private static string[] SoftwareFrameTail(PlaybackHardwareBackend backend) =>
        backend switch
        {
            PlaybackHardwareBackend.Qsv => ["format=nv12", "hwupload=extra_hw_frames=64"],
            PlaybackHardwareBackend.Vaapi => ["format=nv12", "hwupload"],
            PlaybackHardwareBackend.Amf => ["format=nv12"],
            _ => ["format=yuv420p"]
        };

    // Even output dimensions keep 4:2:0 encoders happy; explicit numbers avoid relying on expression support of the device scalers.
    private static string? HardwareScaleFilter(PlaybackHardwareBackend backend, PlaybackVideoOutput video)
    {
        var sourceHeight = video.SourceHeight!.Value;
        var outputHeight = Math.Min(sourceHeight, video.MaxOutputHeight is > 0 ? video.MaxOutputHeight.Value : sourceHeight);
        if (outputHeight >= sourceHeight)
        {
            return null;
        }

        var height = (outputHeight & ~1).ToString(CultureInfo.InvariantCulture);
        var width = (((int)Math.Round(video.SourceWidth!.Value * (double)outputHeight / sourceHeight) + 1) & ~1).ToString(CultureInfo.InvariantCulture);
        return backend switch
        {
            PlaybackHardwareBackend.Nvenc => $"scale_cuda={width}:{height}",
            PlaybackHardwareBackend.Qsv => $"vpp_qsv=w={width}:h={height}",
            _ => $"scale_vaapi=w={width}:h={height}:format=nv12"
        };
    }

    private static void AddVideoEncode(List<string> arguments, PlaybackPlan plan, PlaybackVideoOutput video, PlaybackEncoderTarget target, bool hardwareDecoding)
    {
        var output = new List<string>();
        if (hardwareDecoding)
        {
            if (HardwareScaleFilter(target.Backend, video) is { } hardwareScale)
            {
                output.Add(hardwareScale);
            }
        }
        else
        {
            if (video.MaxOutputHeight is { } maxHeight && maxHeight > 0)
            {
                output.Add($"scale=-2:min(ih\\,{maxHeight.ToString(CultureInfo.InvariantCulture)})");
            }

            output.AddRange(SoftwareFrameTail(target.Backend));
        }

        if (video.BurnInSubtitleStreamIndex is { } subtitleIndex)
        {
            arguments.AddRange(["-filter_complex", BurnInFilter(video, subtitleIndex, output), "-map", "[vout]"]);
        }
        else
        {
            var filters = video.ToneMap ? [ToneMapFilter, .. output] : output;
            arguments.AddRange(["-map", "0:v:0"]);
            if (filters.Count > 0)
            {
                arguments.AddRange(["-vf", string.Join(',', filters)]);
            }
        }

        var bitrate = Math.Max(300, video.TargetBitrateKbps ?? PlaybackDecisionEngine.DefaultVideoKbps(video.MaxOutputHeight ?? 1080));
        AddEncoderArguments(arguments, target.Backend, bitrate);

        if (plan.Transport == PlaybackTransport.Hls)
        {
            arguments.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{HlsSegmentSeconds})"]);
            AddForcedIdrArguments(arguments, target.Backend);
        }
    }

    // veryfast is the software preset a low-core server can sustain for one stream; the hardware presets favour speed over compression for the same reason.
    private static void AddEncoderArguments(List<string> arguments, PlaybackHardwareBackend backend, int bitrateKbps)
    {
        arguments.AddRange(["-c:v", PlaybackHardwareBackends.H264Encoder(backend)]);
        arguments.AddRange(backend switch
        {
            PlaybackHardwareBackend.Nvenc => ["-preset", "p4", "-profile:v", "high"],
            PlaybackHardwareBackend.Qsv => ["-preset", "veryfast", "-profile:v", "high"],
            PlaybackHardwareBackend.Vaapi => ["-profile:v", "high"],
            PlaybackHardwareBackend.Amf => ["-usage", "transcoding", "-quality", "speed", "-profile:v", "high"],
            _ => ["-preset", "veryfast", "-profile:v", "high"]
        });
        arguments.AddRange([
            "-b:v", $"{bitrateKbps.ToString(CultureInfo.InvariantCulture)}k",
            "-maxrate", $"{(bitrateKbps * 3 / 2).ToString(CultureInfo.InvariantCulture)}k",
            "-bufsize", $"{(bitrateKbps * 2).ToString(CultureInfo.InvariantCulture)}k"
        ]);
    }

    // Hardware encoders only turn a forced keyframe into an IDR (a clean segment start) when asked to.
    private static void AddForcedIdrArguments(List<string> arguments, PlaybackHardwareBackend backend)
    {
        switch (backend)
        {
            case PlaybackHardwareBackend.Nvenc:
                arguments.AddRange(["-forced-idr", "1"]);
                break;
            case PlaybackHardwareBackend.Qsv:
                arguments.AddRange(["-forced_idr", "1"]);
                break;
        }
    }
}
