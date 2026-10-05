using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Jularr.Web.Infrastructure;

namespace Jularr.Tests;

/// <summary>
/// Hardware encoder detection, selection and the circuit breaker, answered by a fake ffmpeg and a manual clock:
/// nothing here starts a process, and no hardware is assumed.
/// </summary>
[TestClass]
public sealed class PlaybackHardwareTests
{
    private const string ChromeAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private const string EncoderListing = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V....D h264_qsv             H.264 / AVC / MPEG-4 AVC (Intel Quick Sync Video acceleration) (codec h264)
         V....D h264_vaapi           H.264/AVC (VAAPI) (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private const string AccelerationListing = """
        Hardware acceleration methods:
        vdpau
        cuda
        vaapi
        qsv
        """;

    [TestMethod]
    public void EncoderListingYieldsEncoderNamesOnly()
    {
        var names = PlaybackHardwareProbe.ParseEncoders(EncoderListing);

        CollectionAssert.AreEquivalent(new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_vaapi", "aac" }, names.ToArray());
    }

    [TestMethod]
    public void AccelerationListingYieldsTheMethodsAfterItsHeader()
    {
        var names = PlaybackHardwareProbe.ParseHardwareAccelerations(AccelerationListing);

        CollectionAssert.AreEquivalent(new[] { "vdpau", "cuda", "vaapi", "qsv" }, names.ToArray());
        Assert.AreEqual(0, PlaybackHardwareProbe.ParseHardwareAccelerations("").Count);
    }

    [TestMethod]
    public async Task OnlyEncodersWhoseTestEncodePassesAreAvailable()
    {
        var runner = PlaybackServerTestKit.Ffmpeg(
            ["libx264", "h264_nvenc", "h264_qsv", "h264_vaapi"],
            ["cuda", "vaapi"],
            arguments => !arguments.Contains("h264_nvenc"));
        var kit = PlaybackServerTestKit.Create(runner: runner, renderDevices: () => ["/dev/dri/renderD129", "/dev/dri/renderD128"]);

        await kit.Hardware.DetectAsync(CancellationToken.None);

        var detected = kit.Hardware.Detected!;
        Assert.IsTrue(detected.FfmpegAvailable);
        var nvenc = detected.Status(PlaybackHardwareBackend.Nvenc)!;
        Assert.AreEqual(PlaybackBackendState.TestFailed, nvenc.State, "ffmpeg lists NVENC even without an NVIDIA GPU; only the test encode proves it.");
        Assert.AreEqual("Device creation failed: -12.", nvenc.Detail, "The last stderr line names the cause.");
        Assert.AreEqual(PlaybackBackendState.Available, detected.Status(PlaybackHardwareBackend.Qsv)!.State);
        Assert.IsFalse(detected.Status(PlaybackHardwareBackend.Qsv)!.HardwareDecoding, "qsv is not among the listed hardware accelerations.");
        var vaapi = detected.Status(PlaybackHardwareBackend.Vaapi)!;
        Assert.AreEqual(PlaybackBackendState.Available, vaapi.State);
        Assert.AreEqual("/dev/dri/renderD129", vaapi.Device, "The first render node that encodes wins.");
        Assert.IsTrue(vaapi.HardwareDecoding);
        Assert.AreEqual(PlaybackBackendState.EncoderMissing, detected.Status(PlaybackHardwareBackend.Amf)!.State);
    }

    [TestMethod]
    public async Task VaapiTriesEveryRenderNodeAndReportsNoDeviceWhenThereIsNone()
    {
        var runner = PlaybackServerTestKit.Ffmpeg(["h264_vaapi"], ["vaapi"], arguments => arguments.Any(x => x.EndsWith("renderD128", StringComparison.Ordinal)));
        var kit = PlaybackServerTestKit.Create(runner: runner, renderDevices: () => ["/dev/dri/renderD129", "/dev/dri/renderD128", "/etc/passwd"]);
        await kit.Hardware.DetectAsync(CancellationToken.None);

        var vaapi = kit.Hardware.Detected!.Status(PlaybackHardwareBackend.Vaapi)!;
        Assert.AreEqual("/dev/dri/renderD128", vaapi.Device, "The failing node is skipped; a path that is not a render node is never tried.");
        Assert.AreEqual(2, runner.Calls.Count(x => x.Contains("h264_vaapi")));

        var withoutDevice = PlaybackServerTestKit.Create(runner: PlaybackServerTestKit.Ffmpeg(["h264_vaapi"], ["vaapi"]));
        await withoutDevice.Hardware.DetectAsync(CancellationToken.None);
        Assert.AreEqual(PlaybackBackendState.NoDevice, withoutDevice.Hardware.Detected!.Status(PlaybackHardwareBackend.Vaapi)!.State);
    }

    [TestMethod]
    public async Task AMissingFfmpegMakesEveryBackendUnavailableAndProcessingUnavailable()
    {
        var kit = PlaybackServerTestKit.Create(runner: new FakeMediaProcessRunner(_ => null));

        await kit.Hardware.DetectAsync(CancellationToken.None);

        Assert.IsFalse(kit.Hardware.Detected!.FfmpegAvailable);
        Assert.IsTrue(kit.Hardware.Detected.Backends.All(x => x.State == PlaybackBackendState.FfmpegUnavailable));
        Assert.IsFalse(kit.Capabilities.Current().ProcessingAvailable);
    }

    [TestMethod]
    public async Task ATestEncodeThatTimesOutIsAFailureNotAnAssumption()
    {
        var runner = new FakeMediaProcessRunner(arguments =>
            arguments.Contains("-encoders") ? new MediaProcessResult(0, EncoderListing, "")
            : arguments.Contains("-hwaccels") ? new MediaProcessResult(0, AccelerationListing, "")
            : null);
        var kit = PlaybackServerTestKit.Create(runner: runner);

        await kit.Hardware.DetectAsync(CancellationToken.None);

        var nvenc = kit.Hardware.Detected!.Status(PlaybackHardwareBackend.Nvenc)!;
        Assert.AreEqual(PlaybackBackendState.TestFailed, nvenc.State);
        StringAssert.Contains(nvenc.Detail!, "timed out");
        Assert.AreEqual(PlaybackHardwareBackend.Software, kit.Hardware.Choose().Target.Backend);
    }

    [TestMethod]
    public async Task CancellationReachesTheProbeAndLeavesTheCachedResultAlone()
    {
        var kit = PlaybackServerTestKit.Create(runner: PlaybackServerTestKit.Ffmpeg(["h264_nvenc"], []));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => kit.Hardware.DetectAsync(cancelled.Token));

        Assert.IsNull(kit.Hardware.Detected);
        Assert.IsNull(kit.Hardware.DetectionError, "Cancelling is not a detection failure.");
    }

    [TestMethod]
    public async Task AnUnexpectedProbeFailureKeepsThePreviousResultAndIsShown()
    {
        var failing = false;
        var runner = new FakeMediaProcessRunner(arguments =>
            failing ? throw new IOException("pipe broken")
            : arguments.Contains("-encoders") ? new MediaProcessResult(0, "Encoders:\n ------\n V....D h264_nvenc nvenc\n", "")
            : new MediaProcessResult(0, "", ""));
        var kit = PlaybackServerTestKit.Create(runner: runner);
        await kit.Hardware.DetectAsync(CancellationToken.None);
        var first = kit.Hardware.Detected;

        failing = true;
        await kit.Hardware.DetectAsync(CancellationToken.None);

        Assert.AreSame(first, kit.Hardware.Detected);
        Assert.AreEqual("pipe broken", kit.Hardware.DetectionError);
    }

    [TestMethod]
    public async Task TheFirstAvailableBackendInPolicyOrderWins()
    {
        var kit = await DetectedKitAsync(["h264_vaapi", "h264_qsv", "h264_amf"], ["qsv"]);

        var target = kit.Hardware.Choose().Target;

        Assert.AreEqual(PlaybackHardwareBackend.Qsv, target.Backend, "Order is NVENC, QSV, VAAPI, AMF.");
        Assert.IsTrue(target.HardwareDecoding);
        Assert.AreEqual(PlaybackHardwareBackend.Software, PlaybackServerTestKit.Create().Hardware.Choose().Target.Backend, "Before any detection the server is software-only.");
    }

    [TestMethod]
    public async Task ThreeConsecutiveFailuresOpenTheBreakerForTenMinutes()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z"));
        var breaker = new PlaybackBackendBreaker(time);

        breaker.RecordFailure(PlaybackHardwareBackend.Nvenc, "start_failed", "no device");
        breaker.RecordFailure(PlaybackHardwareBackend.Nvenc, "start_failed", "no device");
        Assert.IsFalse(breaker.State(PlaybackHardwareBackend.Nvenc).IsOpen, "Two failures are not enough.");

        breaker.RecordFailure(PlaybackHardwareBackend.Nvenc, "start_timed_out", "slow");
        var open = breaker.State(PlaybackHardwareBackend.Nvenc);
        Assert.IsTrue(open.IsOpen);
        Assert.AreEqual(3, open.ConsecutiveFailures);
        Assert.AreEqual("start_timed_out", open.LastFailureReason);
        Assert.AreEqual("slow", open.LastFailureDetail);
        Assert.AreEqual(time.GetUtcNow() + TimeSpan.FromMinutes(10), open.OpenUntilUtc);
        Assert.IsFalse(breaker.State(PlaybackHardwareBackend.Qsv).IsOpen, "A breaker is per backend.");

        time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.IsTrue(breaker.State(PlaybackHardwareBackend.Nvenc).IsOpen);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.IsFalse(breaker.State(PlaybackHardwareBackend.Nvenc).IsOpen, "The cooldown ends after ten minutes.");
    }

    [TestMethod]
    public void ASuccessResetsTheRunAndATrialFailureAfterTheCooldownReopensAtOnce()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z"));
        var breaker = new PlaybackBackendBreaker(time);
        breaker.RecordFailure(PlaybackHardwareBackend.Qsv, "start_failed");
        breaker.RecordFailure(PlaybackHardwareBackend.Qsv, "start_failed");
        breaker.RecordSuccess(PlaybackHardwareBackend.Qsv);
        breaker.RecordFailure(PlaybackHardwareBackend.Qsv, "start_failed");
        Assert.IsFalse(breaker.State(PlaybackHardwareBackend.Qsv).IsOpen, "Only consecutive failures count.");

        breaker.RecordFailure(PlaybackHardwareBackend.Qsv, "start_failed");
        breaker.RecordFailure(PlaybackHardwareBackend.Qsv, "start_failed");
        time.Advance(PlaybackBackendBreaker.OpenDuration);
        Assert.IsFalse(breaker.State(PlaybackHardwareBackend.Qsv).IsOpen);

        breaker.RecordFailure(PlaybackHardwareBackend.Qsv, "start_failed");
        Assert.IsTrue(breaker.State(PlaybackHardwareBackend.Qsv).IsOpen, "The single trial session after the cooldown failed.");

        time.Advance(PlaybackBackendBreaker.OpenDuration);
        breaker.RecordSuccess(PlaybackHardwareBackend.Qsv);
        Assert.AreEqual(0, breaker.State(PlaybackHardwareBackend.Qsv).ConsecutiveFailures);
    }

    [TestMethod]
    public async Task AnOpenBreakerFallsBackToTheNextBackendThenSoftwareAndIsExplained()
    {
        var kit = await DetectedKitAsync(["h264_nvenc", "h264_qsv"], [], time: new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z")));
        Open(kit.Breaker, PlaybackHardwareBackend.Nvenc);

        var next = kit.Hardware.Choose();
        Assert.AreEqual(PlaybackHardwareBackend.Qsv, next.Target.Backend);
        Assert.AreEqual(PlaybackHardwareBackend.Nvenc, next.Suspended!.Backend);
        Assert.AreEqual("start_failed", next.Suspended.Reason);

        Open(kit.Breaker, PlaybackHardwareBackend.Qsv);
        var software = kit.Hardware.Choose();
        Assert.AreEqual(PlaybackHardwareBackend.Software, software.Target.Backend);
        Assert.AreEqual(PlaybackHardwareBackend.Nvenc, software.Suspended!.Backend, "The preferred backend is the one reported.");
        Assert.AreEqual(PlaybackHardwareBackend.Software, kit.Hardware.Resolve("h264_nvenc").Backend, "A session planned before the breaker opened falls back to software.");
    }

    [TestMethod]
    public async Task ARedetectionThatPassesTheTestEncodeClosesTheBreaker()
    {
        var kit = await DetectedKitAsync(["h264_nvenc"], [], time: new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z")));
        Open(kit.Breaker, PlaybackHardwareBackend.Nvenc);
        Assert.AreEqual(PlaybackHardwareBackend.Software, kit.Hardware.Choose().Target.Backend);

        await kit.Hardware.DetectAsync(CancellationToken.None);

        Assert.IsFalse(kit.Breaker.State(PlaybackHardwareBackend.Nvenc).IsOpen);
        Assert.AreEqual(PlaybackHardwareBackend.Nvenc, kit.Hardware.Choose().Target.Backend);
    }

    [TestMethod]
    public async Task ServerCapabilitiesFollowTheDetectedEncoderAndItsSlots()
    {
        var kit = await DetectedKitAsync(["h264_nvenc"], ["cuda"]);

        var hardware = kit.Capabilities.Current();
        Assert.AreEqual("h264_nvenc", hardware.H264Encoder);
        Assert.AreEqual(PlaybackServerCapabilities.HardwareMaxHeight, hardware.MaxTranscodeHeight);
        Assert.AreEqual(4, hardware.AvailableTranscodeSlots, "Hardware video has its own limit of four.");

        using var first = kit.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        Assert.AreEqual(4, kit.Capabilities.Current().AvailableTranscodeSlots, "Software slots do not consume hardware capacity.");

        var software = PlaybackServerTestKit.Create().Capabilities.Current();
        Assert.AreEqual("libx264", software.H264Encoder);
        Assert.AreEqual(PlaybackServerCapabilities.SoftwareMaxHeight, software.MaxTranscodeHeight);
        Assert.AreEqual(2, software.AvailableTranscodeSlots);
    }

    [TestMethod]
    public async Task TheDecisionEngineUsesTheDetectedEncoderAndExplainsIt()
    {
        var media = PlaybackMediaProfile.From("/media/episode.mkv", 3_200_000_000, MediaProbeParser.Parse(MediaProbeFixtures.HevcTenBitHdrMultiAudio));
        var client = ClientPlaybackCapabilities.InferFromUserAgent(ChromeAgent, ClientKinds.Web);

        var softwarePlan = PlaybackDecisionEngine.Decide(new(media, client, PlaybackServerTestKit.Create().Capabilities.Current()));
        Assert.AreEqual(PlaybackDeliveryMode.Transcode, softwarePlan.Mode);
        Assert.AreEqual("libx264", softwarePlan.Video!.Encoder);
        Assert.AreEqual(1080, softwarePlan.Video.MaxOutputHeight, "Software encoding is bounded to 1080p.");
        Assert.IsFalse(softwarePlan.Reasons.Any(x => x.Code == PlaybackReasonCodes.HardwareEncoder));

        var kit = await DetectedKitAsync(["h264_nvenc"], ["cuda"], time: new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z")));
        var hardwarePlan = PlaybackDecisionEngine.Decide(new(media, client, kit.Capabilities.Current()));
        Assert.AreEqual("h264_nvenc", hardwarePlan.Video!.Encoder);
        Assert.IsTrue(hardwarePlan.Video.MaxOutputHeight >= softwarePlan.Video.MaxOutputHeight, "Hardware encoding never lowers the software bound.");
        var why = hardwarePlan.Reasons.Single(x => x.Code == PlaybackReasonCodes.HardwareEncoder);
        Assert.AreEqual("NVENC", why.Values!["backend"]);

        Open(kit.Breaker, PlaybackHardwareBackend.Nvenc);
        var suspendedPlan = PlaybackDecisionEngine.Decide(new(media, client, kit.Capabilities.Current()));
        Assert.AreEqual("libx264", suspendedPlan.Video!.Encoder);
        var suspended = suspendedPlan.Reasons.Single(x => x.Code == PlaybackReasonCodes.HardwareEncoderSuspended);
        Assert.AreEqual(PlaybackReasonSeverity.Warning, suspended.Severity);
        Assert.AreEqual("NVENC", suspended.Values!["backend"]);
    }

    [TestMethod]
    public async Task AMissingFfmpegBlocksProcessingButATimeoutOrFailingRunDoesNot()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z"));

        var missing = PlaybackServerTestKit.Create(time, new FakeMediaProcessRunner(_ => null));
        await missing.Hardware.DetectAsync(CancellationToken.None);
        Assert.AreEqual(PlaybackFfmpegState.NotFound, missing.Hardware.Detected!.FfmpegState);
        Assert.IsFalse(missing.Capabilities.Current().ProcessingAvailable);

        var slow = PlaybackServerTestKit.Create(time, new FakeMediaProcessRunner(_ =>
        {
            time.Advance(PlaybackHardwareProbe.ListTimeout);
            return null;
        }));
        await slow.Hardware.DetectAsync(CancellationToken.None);
        Assert.AreEqual(PlaybackFfmpegState.TimedOut, slow.Hardware.Detected!.FfmpegState);
        StringAssert.Contains(slow.Hardware.Detected.Backends[0].Detail!, "in time");
        Assert.IsTrue(slow.Capabilities.Current().ProcessingAvailable, "A sleeping disk or a busy host must not block every remux and transcode.");

        var failing = PlaybackServerTestKit.Create(time, new FakeMediaProcessRunner(_ => new MediaProcessResult(1, "", "Segmentation fault")));
        await failing.Hardware.DetectAsync(CancellationToken.None);
        Assert.AreEqual(PlaybackFfmpegState.Failed, failing.Hardware.Detected!.FfmpegState);
        Assert.IsTrue(failing.Capabilities.Current().ProcessingAvailable);
    }

    [TestMethod]
    public async Task DetectionIsRepeatedWithABoundedBackoffWhileFfmpegIsNotAvailable()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z"));
        var healthy = false;
        var ffmpeg = PlaybackServerTestKit.Ffmpeg(["libx264"], []);
        var runner = new FakeMediaProcessRunner(arguments => healthy ? ffmpeg.RunAsync("ffmpeg", arguments, TimeSpan.Zero, CancellationToken.None).Result : null);
        var kit = PlaybackServerTestKit.Create(time, runner);
        Assert.IsFalse(kit.Hardware.IsRedetectionDue(), "Nothing to repeat before the first run.");

        await kit.Hardware.DetectAsync(CancellationToken.None);
        Assert.IsFalse(kit.Hardware.IsRedetectionDue(), "The first retry waits a minute.");
        time.Advance(PlaybackHardwareService.RetryStart);
        Assert.IsTrue(kit.Hardware.IsRedetectionDue());

        await kit.Hardware.DetectAsync(CancellationToken.None);
        time.Advance(PlaybackHardwareService.RetryStart);
        Assert.IsFalse(kit.Hardware.IsRedetectionDue(), "The backoff doubled.");
        time.Advance(PlaybackHardwareService.RetryStart);
        Assert.IsTrue(kit.Hardware.IsRedetectionDue());

        for (var run = 0; run < 12; run++)
        {
            await kit.Hardware.DetectAsync(CancellationToken.None);
            time.Advance(PlaybackHardwareService.RetryMax);
            Assert.IsTrue(kit.Hardware.IsRedetectionDue(), "The backoff never exceeds ten minutes.");
        }

        healthy = true;
        await kit.Hardware.DetectAsync(CancellationToken.None);
        time.Advance(PlaybackHardwareService.RetryMax);
        Assert.IsTrue(kit.Hardware.Detected!.FfmpegAvailable);
        Assert.IsFalse(kit.Hardware.IsRedetectionDue(), "A healthy detection ends the retries.");
    }

    [TestMethod]
    public void EveryNewReasonAndRefusalCodeHasUserText()
    {
        foreach (var code in new[]
                 {
                     PlaybackReasonCodes.HardwareEncoder,
                     PlaybackReasonCodes.HardwareEncoderSuspended,
                     PlaybackAdmissionCodes.TranscodingDisabled,
                     PlaybackAdmissionCodes.TranscoderBusy,
                     PlaybackAdmissionCodes.CacheBudgetExhausted,
                     PlaybackAdmissionCodes.CacheFreeSpaceLow,
                     PlaybackAdmissionCodes.CacheFolderNotOwned,
                     PlaybackAdmissionCodes.ProfileSessionLimit
                 })
        {
            Assert.IsTrue(Jularr.Web.Features.Localization.UiTranslationResources.TryGet($"playback.reason.{code}", out _), $"playback.reason.{code} is missing.");
            if (code != PlaybackReasonCodes.HardwareEncoder && code != PlaybackReasonCodes.HardwareEncoderSuspended)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(PlaybackAdmissionCodes.Message(code)));
            }
        }

        foreach (var reason in new[] { PlaybackStartFailure.StartFailed })
        {
            Assert.IsTrue(Jularr.Web.Features.Localization.UiTranslationResources.TryGet($"admin.health.hardware.reason.{reason}", out _), reason);
        }
    }

    private static async Task<PlaybackServerTestKit> DetectedKitAsync(string[] hardwareEncoders, string[] accelerations, TimeProvider? time = null)
    {
        var kit = PlaybackServerTestKit.Create(
            time,
            PlaybackServerTestKit.Ffmpeg(["libx264", .. hardwareEncoders], accelerations),
            () => ["/dev/dri/renderD128"]);
        await kit.Hardware.DetectAsync(CancellationToken.None);
        return kit;
    }

    private static void Open(PlaybackBackendBreaker breaker, PlaybackHardwareBackend backend)
    {
        for (var failure = 0; failure < PlaybackBackendBreaker.FailureThreshold; failure++)
        {
            breaker.RecordFailure(backend, PlaybackStartFailure.StartFailed, "ffmpeg exited");
        }
    }
}
