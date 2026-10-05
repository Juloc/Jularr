using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>
/// The per-backend ffmpeg argument sets of <see cref="PlaybackDeliveryCommand"/>, checked without starting ffmpeg:
/// the software path stays exactly what it was, every hardware backend gets its own device setup, filter tail and
/// encoder options, and nothing but a validated render node ever reaches the command line.
/// </summary>
[TestClass]
public sealed class PlaybackEncoderArgumentTests
{
    private const string Source = "/media/episode.mkv";
    private const string RenderNode = "/dev/dri/renderD128";

    [TestMethod]
    public void SoftwareKeepsTheLowCorePresetAndTheCpuFilterChain()
    {
        var arguments = Hls(Video(), PlaybackEncoderTarget.Software);

        Assert.AreEqual("libx264", Value(arguments, "-c:v"));
        Assert.AreEqual("veryfast", Value(arguments, "-preset"));
        Assert.AreEqual("high", Value(arguments, "-profile:v"));
        Assert.AreEqual("scale=-2:min(ih\\,1080),format=yuv420p", Value(arguments, "-vf"));
        CollectionAssert.DoesNotContain(arguments, "-init_hw_device");
        CollectionAssert.DoesNotContain(arguments, "-hwaccel");
        CollectionAssert.DoesNotContain(arguments, "-forced-idr");
    }

    [TestMethod]
    public void NvencEncodesOnTheGpuAfterSoftwareDecodeByDefault()
    {
        var arguments = Hls(Video(sourceCodec: "hevc"), new PlaybackEncoderTarget(PlaybackHardwareBackend.Nvenc, HardwareDecoding: true));

        Assert.AreEqual("h264_nvenc", Value(arguments, "-c:v"));
        Assert.AreEqual("p4", Value(arguments, "-preset"));
        Assert.AreEqual("scale=-2:min(ih\\,1080),format=yuv420p", Value(arguments, "-vf"), "Only H.264 8-bit is decoded on the device; HEVC is decoded in software.");
        Assert.AreEqual("1", Value(arguments, "-forced-idr"), "Forced keyframes must become IDR frames for clean HLS segments.");
        CollectionAssert.DoesNotContain(arguments, "-hwaccel");
        Assert.IsTrue(arguments.IndexOf("-force_key_frames") > arguments.IndexOf("-c:v"));
    }

    [TestMethod]
    public void NvencDecodesH264OnTheGpuAndScalesWithExplicitEvenDimensions()
    {
        var arguments = Progressive(Video(maxHeight: 1080), new PlaybackEncoderTarget(PlaybackHardwareBackend.Nvenc, HardwareDecoding: true));

        Assert.AreEqual("cuda", Value(arguments, "-hwaccel"));
        Assert.AreEqual("cuda", Value(arguments, "-hwaccel_output_format"));
        Assert.IsTrue(arguments.IndexOf("-hwaccel") < arguments.IndexOf("-i"), "Decoder options belong before the input.");
        Assert.AreEqual("scale_cuda=1920:1080", Value(arguments, "-vf"));
        CollectionAssert.DoesNotContain(arguments, "format=yuv420p");
    }

    [TestMethod]
    public void HardwareDecodeWithoutAResizeAddsNoFilter()
    {
        var arguments = Progressive(Video(sourceWidth: 1280, sourceHeight: 720, maxHeight: 1080), new PlaybackEncoderTarget(PlaybackHardwareBackend.Nvenc, HardwareDecoding: true));

        Assert.AreEqual("cuda", Value(arguments, "-hwaccel"));
        CollectionAssert.DoesNotContain(arguments, "-vf");
    }

    [TestMethod]
    public void QsvUploadsSoftwareFramesWithHwuploadAndTheFilterDevice()
    {
        var arguments = Hls(Video(sourceCodec: "hevc"), new PlaybackEncoderTarget(PlaybackHardwareBackend.Qsv, HardwareDecoding: true));

        Assert.AreEqual("h264_qsv", Value(arguments, "-c:v"));
        Assert.AreEqual("qsv=hw", Value(arguments, "-init_hw_device"));
        Assert.AreEqual("hw", Value(arguments, "-filter_hw_device"));
        Assert.AreEqual("scale=-2:min(ih\\,1080),format=nv12,hwupload=extra_hw_frames=64", Value(arguments, "-vf"));
        Assert.AreEqual("1", Value(arguments, "-forced_idr"));
    }

    [TestMethod]
    public void QsvDecodesH264OnTheDeviceAndScalesWithVppQsv()
    {
        var arguments = Progressive(Video(), new PlaybackEncoderTarget(PlaybackHardwareBackend.Qsv, HardwareDecoding: true));

        Assert.AreEqual("qsv", Value(arguments, "-hwaccel"));
        Assert.AreEqual("qsv", Value(arguments, "-hwaccel_output_format"));
        Assert.AreEqual("vpp_qsv=w=1920:h=1080", Value(arguments, "-vf"));
        CollectionAssert.DoesNotContain(arguments, "-filter_hw_device");
    }

    [TestMethod]
    public void VaapiInitialisesTheRenderNodeAndUploadsNv12Frames()
    {
        var arguments = Hls(Video(sourceCodec: "hevc"), new PlaybackEncoderTarget(PlaybackHardwareBackend.Vaapi, RenderNode, HardwareDecoding: true));

        Assert.AreEqual($"vaapi=va:{RenderNode}", Value(arguments, "-init_hw_device"));
        Assert.AreEqual("va", Value(arguments, "-filter_hw_device"));
        Assert.AreEqual("h264_vaapi", Value(arguments, "-c:v"));
        Assert.AreEqual("scale=-2:min(ih\\,1080),format=nv12,hwupload", Value(arguments, "-vf"));
        CollectionAssert.DoesNotContain(arguments, "-preset", "VAAPI has no preset option.");
    }

    [TestMethod]
    public void VaapiDecodesH264OnTheDeviceAndScalesWithScaleVaapi()
    {
        var arguments = Progressive(Video(), new PlaybackEncoderTarget(PlaybackHardwareBackend.Vaapi, RenderNode, HardwareDecoding: true));

        Assert.AreEqual("vaapi", Value(arguments, "-hwaccel"));
        Assert.AreEqual("va", Value(arguments, "-hwaccel_device"));
        Assert.AreEqual("vaapi", Value(arguments, "-hwaccel_output_format"));
        Assert.AreEqual("scale_vaapi=w=1920:h=1080:format=nv12", Value(arguments, "-vf"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("/dev/dri/../../etc/passwd")]
    [DataRow("/dev/sda")]
    [DataRow("/dev/dri/renderD128; rm -rf /")]
    public void VaapiRefusesAnythingButARenderNode(string? device)
    {
        var plan = Transcode(Video());

        Assert.ThrowsExactly<ArgumentException>(() => PlaybackDeliveryCommand.Hls(Source, plan, 0, "/cache/x", new PlaybackEncoderTarget(PlaybackHardwareBackend.Vaapi, device)));
    }

    [TestMethod]
    public void AmfEncodesWithSoftwareDecodeAndNv12Frames()
    {
        var arguments = Hls(Video(), new PlaybackEncoderTarget(PlaybackHardwareBackend.Amf, HardwareDecoding: true));

        Assert.AreEqual("h264_amf", Value(arguments, "-c:v"));
        Assert.AreEqual("transcoding", Value(arguments, "-usage"));
        Assert.AreEqual("scale=-2:min(ih\\,1080),format=nv12", Value(arguments, "-vf"));
        CollectionAssert.DoesNotContain(arguments, "-hwaccel");
    }

    [TestMethod]
    public void ToneMappingAndBurnInKeepTheSoftwareFiltersAndNeverDecodeOnTheDevice()
    {
        var toneMapped = Progressive(Video(toneMap: true), new PlaybackEncoderTarget(PlaybackHardwareBackend.Nvenc, HardwareDecoding: true));
        Assert.AreEqual($"{PlaybackDeliveryCommand.ToneMapFilter},scale=-2:min(ih\\,1080),format=yuv420p", Value(toneMapped, "-vf"));
        CollectionAssert.DoesNotContain(toneMapped, "-hwaccel");

        var burnedIn = Progressive(Video(burnInIndex: 3), new PlaybackEncoderTarget(PlaybackHardwareBackend.Vaapi, RenderNode, HardwareDecoding: true));
        var graph = Value(burnedIn, "-filter_complex");
        StringAssert.Contains(graph, "[0:3]scale=3840:2160[sub]");
        StringAssert.EndsWith(graph, "scale=-2:min(ih\\,1080),format=nv12,hwupload[vout]", "The burned-in picture is uploaded last, after the overlay.");
        CollectionAssert.DoesNotContain(burnedIn, "-hwaccel");
        Assert.AreEqual("va", Value(burnedIn, "-filter_hw_device"));
    }

    [TestMethod]
    public void HardwareDecodeIsOnlyUsedWhereItCannotGoWrong()
    {
        var target = new PlaybackEncoderTarget(PlaybackHardwareBackend.Nvenc, HardwareDecoding: true);

        CollectionAssert.DoesNotContain(Progressive(Video(sourcePixelFormat: "yuv420p10le"), target), "-hwaccel", "10-bit H.264 is decoded in software.");
        CollectionAssert.DoesNotContain(Progressive(Video(sourceWidth: null, sourceHeight: null), target), "-hwaccel", "Unknown dimensions cannot be scaled on the device.");
        CollectionAssert.DoesNotContain(Progressive(Video(), target with { HardwareDecoding = false }), "-hwaccel", "The ffmpeg build must offer the decoder.");
    }

    [TestMethod]
    public void RemuxNeverTouchesADeviceOrAnEncoder()
    {
        var plan = Remux();

        var arguments = PlaybackDeliveryCommand.Hls(Source, plan, 0, "/cache/x", new PlaybackEncoderTarget(PlaybackHardwareBackend.Vaapi, RenderNode, HardwareDecoding: true)).ToList();

        Assert.AreEqual("copy", Value(arguments, "-c:v"));
        CollectionAssert.DoesNotContain(arguments, "-init_hw_device");
        CollectionAssert.DoesNotContain(arguments, "-hwaccel");
        CollectionAssert.DoesNotContain(arguments, "-vf");
    }

    [TestMethod]
    public void PlanEncoderNameDoesNotChooseTheEncoder()
    {
        var plan = Transcode(Video(encoder: "h264_nvenc"));

        var arguments = PlaybackDeliveryCommand.Hls(Source, plan, 0, "/cache/x").ToList();

        Assert.AreEqual("libx264", Value(arguments, "-c:v"), "Only the server-resolved target selects hardware, never the (client-visible) plan text.");
    }

    [TestMethod]
    [DataRow(PlaybackHardwareBackend.Nvenc, "h264_nvenc")]
    [DataRow(PlaybackHardwareBackend.Qsv, "h264_qsv")]
    [DataRow(PlaybackHardwareBackend.Vaapi, "h264_vaapi")]
    [DataRow(PlaybackHardwareBackend.Amf, "h264_amf")]
    public void HardwareProbeEncodesAGeneratedPictureThroughTheRealDeviceSetup(PlaybackHardwareBackend backend, string encoder)
    {
        var arguments = PlaybackDeliveryCommand.HardwareProbe(new PlaybackEncoderTarget(backend, backend == PlaybackHardwareBackend.Vaapi ? RenderNode : null, HardwareDecoding: true)).ToList();

        Assert.AreEqual(encoder, Value(arguments, "-c:v"));
        Assert.AreEqual("lavfi", Value(arguments, "-f"));
        CollectionAssert.DoesNotContain(arguments, "-hwaccel", "The probe proves encoding only.");
        Assert.AreEqual("null", arguments[arguments.LastIndexOf("-f") + 1]);
        Assert.AreEqual("-", arguments[^1]);
        Assert.IsTrue(arguments.Contains("-nostdin"));
    }

    [TestMethod]
    public void HardwareEncoderNamesComeFromAFixedSet()
    {
        foreach (var backend in Enum.GetValues<PlaybackHardwareBackend>())
        {
            Assert.AreEqual(backend, PlaybackHardwareBackends.FromEncoder(PlaybackHardwareBackends.H264Encoder(backend)));
        }

        Assert.AreEqual(PlaybackHardwareBackend.Software, PlaybackHardwareBackends.FromEncoder("h264_nvenc; rm -rf /"));
        Assert.AreEqual(PlaybackHardwareBackend.Software, PlaybackHardwareBackends.FromEncoder(null));
    }

    private static List<string> Hls(PlaybackVideoOutput video, PlaybackEncoderTarget target) =>
        [.. PlaybackDeliveryCommand.Hls(Source, Transcode(video), 0, "/cache/x", target)];

    private static List<string> Progressive(PlaybackVideoOutput video, PlaybackEncoderTarget target) =>
        [.. PlaybackDeliveryCommand.Progressive(Source, Transcode(video, PlaybackTransport.ProgressiveMp4), 0, target)];

    private static string Value(List<string> arguments, string option) => arguments[arguments.IndexOf(option) + 1];
}
