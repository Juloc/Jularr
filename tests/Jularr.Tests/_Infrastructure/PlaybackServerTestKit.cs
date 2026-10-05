using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>Answers every ffmpeg call from a delegate and records the arguments; no process is ever started.</summary>
internal sealed class FakeMediaProcessRunner(Func<IReadOnlyList<string>, MediaProcessResult?> respond) : IMediaProcessRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<MediaProcessResult?> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(arguments);
        return Task.FromResult(respond(arguments));
    }
}

/// <summary>
/// The playback server resource owners wired the way the application wires them, with ffmpeg and the
/// clock faked. Without a detection run the server is software-only, exactly like a fresh start.
/// </summary>
internal sealed class PlaybackServerTestKit
{
    private PlaybackServerTestKit(string dataRoot, TimeProvider time, FakeMediaProcessRunner runner, Func<IReadOnlyList<string>> renderDevices)
    {
        DataRoot = dataRoot;
        Time = time;
        Runner = runner;
        Settings = new PlaybackTranscodingSettingsStore(dataRoot);
        Slots = new PlaybackTranscodeSlots(Settings);
        Breaker = new PlaybackBackendBreaker(time);
        Hardware = new PlaybackHardwareService(new PlaybackHardwareProbe(runner, time, renderDevices), Breaker, NullLogger<PlaybackHardwareService>.Instance);
        Capabilities = new PlaybackServerCapabilityProvider(Settings, Slots, Hardware);
        Admission = new PlaybackAdmissionService(Settings, Slots, Hardware);
    }

    public string DataRoot { get; }

    public TimeProvider Time { get; }

    public FakeMediaProcessRunner Runner { get; }

    public PlaybackTranscodingSettingsStore Settings { get; }

    public PlaybackTranscodeSlots Slots { get; }

    public PlaybackBackendBreaker Breaker { get; }

    public PlaybackHardwareService Hardware { get; }

    public PlaybackServerCapabilityProvider Capabilities { get; }

    public PlaybackAdmissionService Admission { get; }

    public static PlaybackServerTestKit Create(TimeProvider? time = null, FakeMediaProcessRunner? runner = null, Func<IReadOnlyList<string>>? renderDevices = null) =>
        new(
            Path.Combine(Path.GetTempPath(), $"jularr-playback-{Guid.NewGuid():N}"),
            time ?? TimeProvider.System,
            runner ?? new FakeMediaProcessRunner(_ => null),
            renderDevices ?? (() => []));

    /// <summary>An ffmpeg that lists the given encoders and hardware accelerations and passes the test encode of the given backends.</summary>
    public static FakeMediaProcessRunner Ffmpeg(IEnumerable<string> encoders, IEnumerable<string> hardwareAccelerations, Func<IReadOnlyList<string>, bool>? testEncodePasses = null) =>
        new(arguments =>
        {
            if (arguments.Contains("-encoders"))
            {
                var lines = encoders.Select(name => $" V....D {name,-18} {name} encoder");
                return new MediaProcessResult(0, $"Encoders:\n V..... = Video\n ------\n{string.Join('\n', lines)}\n", "");
            }

            if (arguments.Contains("-hwaccels"))
            {
                return new MediaProcessResult(0, $"Hardware acceleration methods:\n{string.Join('\n', hardwareAccelerations)}\n", "");
            }

            return testEncodePasses is null || testEncodePasses(arguments)
                ? new MediaProcessResult(0, "", "")
                : new MediaProcessResult(1, "", "Cannot load libcuda.so.1\nDevice creation failed: -12.");
        });

    /// <summary>A session manager over the kit's settings and clock whose ffmpeg is a fake.</summary>
    public HlsPlaybackSessionManager Hls(HlsProcessStarter starter, Func<string, long?>? freeSpace = null) =>
        new(Settings, Time, NullLogger<HlsPlaybackSessionManager>.Instance, starter, freeSpace);
}

/// <summary>A clock the test moves by hand, so cooldowns and idle times are deterministic.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _current = now;

    public override DateTimeOffset GetUtcNow() => _current;

    public void Advance(TimeSpan by) => _current += by;
}

/// <summary>Plans built by hand, so resource and argument tests do not depend on the decision engine's choices.</summary>
internal static class PlaybackTestPlans
{
    public static PlaybackVideoOutput Video(
        string? sourceCodec = "h264",
        int? sourceWidth = 3840,
        int? sourceHeight = 2160,
        int? maxHeight = 1080,
        string? sourcePixelFormat = "yuv420p",
        bool toneMap = false,
        int? burnInIndex = null,
        string? encoder = null) =>
        new(
            Copy: false,
            sourceCodec,
            "h264",
            sourceWidth,
            sourceHeight,
            maxHeight,
            sourcePixelFormat,
            "SDR",
            TargetBitrateKbps: 8000,
            ToneMap: toneMap,
            Encoder: encoder,
            BurnInSubtitleStreamIndex: burnInIndex);

    public static PlaybackPlan Transcode(PlaybackVideoOutput video, PlaybackTransport transport = PlaybackTransport.Hls) =>
        new(
            PlaybackDeliveryMode.Transcode,
            transport,
            "mp4",
            video,
            new PlaybackAudioOutput(1, false, "aac", "aac", 2, 2, 192, "jpn"),
            new PlaybackQualityResolution(PlaybackQualityPreset.Auto, PlaybackNetworkClass.Local, null, PlaybackLimitSource.None, null, null),
            [],
            PlaybackCapabilitySupport.Confirmed);

    public static PlaybackPlan Remux(PlaybackTransport transport = PlaybackTransport.Hls) =>
        Transcode(new PlaybackVideoOutput(true, "h264", "h264", 1920, 1080, null, "yuv420p", "SDR"), transport) with { Mode = PlaybackDeliveryMode.DirectStream };

    public static PlaybackPlan AudioOnly() => Transcode(Video()) with { Mode = PlaybackDeliveryMode.DirectStream, Video = null };
}
