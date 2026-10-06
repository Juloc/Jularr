using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Media.Compatibility;
using Jularr.Web.Features.Playback.Decision;

namespace Jularr.Tests;

[TestClass]
public sealed class PlaybackCapabilityProbeTests
{
    [TestMethod]
    public void BrowserProbeReportsConfirmedInferredAndUnsupportedClaimsTheServerUnderstands()
    {
        var output = RunProbe(hevc: false);
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(4, lines.Length, output);

        var capabilities = JsonSerializer.Deserialize<ClientPlaybackCapabilities>(
            lines[0],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Normalize();

        Assert.AreEqual("web", capabilities.Client.Kind);
        var mp4 = capabilities.Container(MediaContainerFamily.Mp4)!;
        Assert.AreEqual(PlaybackCapabilitySupport.Confirmed, mp4.Support);
        var h264 = capabilities.VideoCodec(MediaContainerFamily.Mp4, "h264")!;
        Assert.AreEqual(PlaybackCapabilitySupport.Confirmed, h264.Support);
        CollectionAssert.AreEqual(new[] { 8 }, h264.BitDepths!.ToArray(), "10-bit H.264 was not decodable.");
        Assert.AreEqual(1080, h264.MaxHeight);
        Assert.AreEqual(PlaybackCapabilitySupport.Unsupported, capabilities.VideoCodec(MediaContainerFamily.Mp4, "hevc")!.Support);
        Assert.AreEqual(PlaybackCapabilitySupport.Unsupported, capabilities.ContainerSupport(MediaContainerFamily.Matroska),
            "MediaCapabilities rejects the Matroska MIME type and canPlayType has no answer.");
        Assert.AreEqual(PlaybackCapabilitySupport.Unsupported, capabilities.DeliveryOrDefault.Hls);
        Assert.AreEqual(PlaybackCapabilitySupport.Inferred, capabilities.DeliveryOrDefault.ProgressiveMp4);
        Assert.AreEqual(PlaybackCapabilitySupport.Unsupported, capabilities.HdrOrDefault.Display);
        Assert.AreEqual(PlaybackCapabilitySupport.Unsupported, capabilities.FeaturesOrDefault.AudioTrackSelection);
        Assert.AreEqual(PlaybackCapabilitySupport.Confirmed, capabilities.SubtitlesOrDefault.Text);

        Assert.AreEqual("cached", lines[1], "The second detect() reads the cache.");
        Assert.AreEqual("reprobed", lines[2], "A browser update invalidates the cache.");
        Assert.AreEqual("reprobed", lines[3], "A Jularr update (data-app-version) invalidates the cache.");

        var h264Media = PlaybackMediaProfile.From("/media/a.mp4", 540_000_000, MediaProbeParser.Parse(MediaProbeFixtures.H264Stereo));
        var hevcMedia = PlaybackMediaProfile.From("/media/a.mkv", 3_200_000_000, MediaProbeParser.Parse(MediaProbeFixtures.HevcTenBitHdrMultiAudio));
        var server = PlaybackServerCapabilities.Software();
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, PlaybackDecisionEngine.Decide(new(h264Media, capabilities, server)).Mode);
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, PlaybackDecisionEngine.Decide(new(hevcMedia, capabilities, server)).Mode);
    }

    [TestMethod]
    public void HardwareHevcInTheProbeUnlocksDirectStream()
    {
        var output = RunProbe(hevc: true);
        var capabilities = JsonSerializer.Deserialize<ClientPlaybackCapabilities>(
            output.Split('\n')[0],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Normalize();

        var hevc = capabilities.VideoCodec(MediaContainerFamily.Mp4, "hevc")!;
        Assert.AreEqual(PlaybackCapabilitySupport.Confirmed, hevc.Support);
        CollectionAssert.AreEqual(new[] { 8, 10 }, hevc.BitDepths!.ToArray());
        Assert.AreEqual(2160, hevc.MaxHeight);
        Assert.IsNull(hevc.CodecTags, "hev1 is accepted too, so no tag restriction.");

        var hevcMedia = PlaybackMediaProfile.From("/media/a.mkv", 3_200_000_000, MediaProbeParser.Parse(MediaProbeFixtures.HevcTenBitHdrMultiAudio));
        var plan = PlaybackDecisionEngine.Decide(new(hevcMedia, capabilities, PlaybackServerCapabilities.Software()));
        Assert.AreEqual(PlaybackDeliveryMode.DirectStream, plan.Mode);
        Assert.IsTrue(plan.Video!.Copy);
    }

    [TestMethod]
    public void EveryReasonCodeAndModeHasAUiText()
    {
        var codes = typeof(PlaybackReasonCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral)
            .Select(x => (string)x.GetRawConstantValue()!)
            .ToArray();
        Assert.IsTrue(codes.Length > 30);
        foreach (var code in codes)
        {
            Assert.IsTrue(UiTranslationResources.TryGet($"playback.reason.{code}", out _), $"playback.reason.{code} is missing.");
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var mode in Enum.GetValues<PlaybackDeliveryMode>())
        {
            var name = JsonSerializer.Serialize(mode, options).Trim('"');
            Assert.IsTrue(UiTranslationResources.TryGet($"playback.mode.{name}", out _), $"playback.mode.{name} is missing.");
        }

        foreach (var preference in Enum.GetValues<PlaybackModePreference>())
        {
            var name = JsonSerializer.Serialize(preference, options).Trim('"');
            Assert.IsTrue(UiTranslationResources.TryGet($"playback.preference.{name}", out _), $"playback.preference.{name} is missing.");
        }
    }

    [TestMethod]
    public void WatchPageAsksTheServerForAPlanInsteadOfDecidingItself()
    {
        var root = PlayerControlsTests.RepositoryRoot();
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));
        var page = EpisodePlayerSource.Read(root);

        StringAssert.Contains(page, "data-playback-plan-url");
        StringAssert.Contains(page, "js/playback-capabilities.js");
        StringAssert.Contains(player, "failedModes");
        foreach (var removed in new[] { "canPlayType", "supportsHevc", "variants", "chooseMode", "handler=Media" })
        {
            Assert.IsFalse(player.Contains(removed, StringComparison.Ordinal), $"The player must not decide playback itself ({removed}).");
        }

        Assert.IsTrue(page.IndexOf("playback-capabilities.js", StringComparison.Ordinal) <
                      page.IndexOf("episode-player.js", StringComparison.Ordinal));
    }

    private static string RunProbe(bool hevc)
    {
        var root = PlayerControlsTests.RepositoryRoot();
        var probe = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "playback-capabilities.js");
        var script = """
            const supported = new Set([
                'video/mp4; codecs="avc1.640028"',
                'video/mp4; codecs="avc1.640028,mp4a.40.2"',
                'video/mp4; codecs="vp09.00.40.08"',
                'video/mp4; codecs="vp09.02.40.10"',
                'video/mp4; codecs="av01.0.08M.08"',
                'video/mp4; codecs="av01.0.08M.10"',
                'video/webm; codecs="vp09.00.40.08"',
                'video/webm; codecs="vp09.02.40.10"',
                'video/webm; codecs="vp8"',
                'video/mp4; codecs="mp4a.40.2"',
                'video/mp4; codecs="mp4a.6B"',
                'video/mp4; codecs="opus"',
                'video/mp4; codecs="flac"',
                'video/webm; codecs="opus"',
                'video/webm; codecs="vorbis"'
            ]);
            if (process.argv[3] === "hevc") {
                supported.add('video/mp4; codecs="hvc1.1.6.L120.90"');
                supported.add('video/mp4; codecs="hvc1.2.4.L120.90"');
                supported.add('video/mp4; codecs="hev1.1.6.L120.90"');
            }
            let decodes = 0;
            const element = {
                canPlayType: type => supported.has(type) ? "probably" : type === "video/mp4" || type === "video/webm" ? "maybe" : "",
                requestPictureInPicture() {}
            };
            const window = {
                matchMedia: query => ({ matches: query === "(dynamic-range: standard)" }),
                screen: { width: 1920, height: 1080, orientation: { lock() {} } },
                devicePixelRatio: 1,
                MediaSource: { isTypeSupported: () => true }
            };
            const document = {
                documentElement: { dataset: {} },
                createElement: () => element,
                pictureInPictureEnabled: true,
                fullscreenEnabled: true
            };
            const navigator = {
                userAgent: "Probe/1",
                connection: { saveData: false, type: "wifi" },
                mediaCapabilities: {
                    decodingInfo: async config => {
                        decodes++;
                        const type = (config.video || config.audio).contentType.replace(/^audio\//, "video/");
                        if (config.audio && !(config.audio.contentType.startsWith("audio/"))) throw new TypeError("audio MIME required");
                        if (type.startsWith("video/x-matroska")) throw new TypeError("unsupported MIME");
                        // The plain device decodes up to 1080p; the HEVC device also 2160p.
                        const uhd = config.video && config.video.height > 1080 && process.argv[3] !== "hevc";
                        return { supported: supported.has(type) && !uhd, smooth: true, powerEfficient: true };
                    }
                }
            };
            const values = new Map();
            const storage = { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) };
            eval(require("fs").readFileSync(process.argv[2], "utf8"));
            (async () => {
                const env = { window, navigator, document, storage };
                const first = await window.JularrPlaybackCapabilities.detect(env);
                console.log(JSON.stringify(first));
                const before = decodes;
                await window.JularrPlaybackCapabilities.detect(env);
                console.log(decodes === before ? "cached" : "reprobed");
                navigator.userAgent = "Probe/2";
                await window.JularrPlaybackCapabilities.detect(env);
                console.log(decodes > before ? "reprobed" : "cached");
                const afterBrowserUpdate = decodes;
                await window.JularrPlaybackCapabilities.detect(env);
                document.querySelector = selector => selector === "[data-app-version]" ? { dataset: { appVersion: "9.9.9" } } : null;
                await window.JularrPlaybackCapabilities.detect(env);
                console.log(decodes > afterBrowserUpdate ? "reprobed" : "cached");
            })().catch(error => { console.error(error); process.exit(1); });
            """;

        var scriptPath = Path.Combine(Path.GetTempPath(), $"jularr-probe-{Guid.NewGuid():N}.js");
        File.WriteAllText(scriptPath, script);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node")
            {
                ArgumentList = { scriptPath, probe, hevc ? "hevc" : "plain" },
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                Assert.Inconclusive("Node.js is not available to execute the capability probe.");
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, errors);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Inconclusive("Node.js is not available to execute the capability probe.");
            throw;
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }
}
