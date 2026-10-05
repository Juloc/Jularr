using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>
/// How one delivery start is admitted and what its failures prove: slots and per-profile cap, the Admin switch for
/// legacy routes, and the fallback chain (hardware decode, then software decode, then software) with the breaker
/// accounting that only a software fallback that works may charge a hardware backend. Everything is fake.
/// </summary>
[TestClass]
public sealed class PlaybackAdmissionFlowTests
{
    private const string Profile = "profile-0";

    [TestMethod]
    public void AdmissionRefusesWithAnExplicitCodeWhenTheClassIsFull()
    {
        var kit = PlaybackServerTestKit.Create();
        using var first = kit.Admission.Admit(Transcode(Video()), "profile-a").Lease;
        using var second = kit.Admission.Admit(Transcode(Video()), "profile-b").Lease;

        var refused = kit.Admission.Admit(Transcode(Video()), "profile-c");

        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderBusy, refused.RefusalCode);
        Assert.IsNull(refused.Lease);
        Assert.IsTrue(kit.Admission.Admit(Remux(), "profile-c").Admitted, "Remux has its own six slots.");
    }

    [TestMethod]
    public void OneProfileCannotTakeMoreThanItsShareAcrossAllClasses()
    {
        var kit = PlaybackServerTestKit.Create();
        var leases = new List<IDisposable?>
        {
            kit.Admission.Admit(Remux(), Profile).Lease,
            kit.Admission.Admit(Remux(), Profile).Lease,
            kit.Admission.Admit(AudioOnly(), Profile).Lease
        };
        Assert.AreEqual(PlaybackTranscodeSlots.MaxPerProfile, leases.Count);

        var refused = kit.Admission.Admit(Remux(), Profile);

        Assert.AreEqual(PlaybackAdmissionCodes.ProfileSessionLimit, refused.RefusalCode);
        Assert.IsTrue(kit.Admission.Admit(Remux(), "another-profile").Admitted, "Other profiles are not affected.");
        leases[0]!.Dispose();
        Assert.IsTrue(kit.Admission.Admit(Remux(), Profile).Admitted, "A released slot returns to the profile.");
    }

    [TestMethod]
    public async Task LegacyRoutesObeyTheAdminSwitchAndTheSameLimits()
    {
        var kit = PlaybackServerTestKit.Create();
        try
        {
            var software = kit.Admission.AdmitLegacy(PlaybackCostClass.SoftwareVideo, Profile);
            Assert.IsTrue(software.Admitted);
            Assert.AreEqual(1, kit.Slots.Active(PlaybackCostClass.SoftwareVideo));
            software.Lease!.Dispose();

            await kit.Settings.SaveAsync(PlaybackTranscodingSettings.Default with { TranscodingEnabled = false, HlsCachePath = Path.Combine(kit.DataRoot, "hls") });

            Assert.AreEqual(PlaybackAdmissionCodes.TranscodingDisabled, kit.Admission.AdmitLegacy(PlaybackCostClass.SoftwareVideo, Profile).RefusalCode);
            Assert.IsTrue(kit.Admission.AdmitLegacy(PlaybackCostClass.Remux, Profile).Admitted, "A remux is not a transcode.");
        }
        finally
        {
            Directory.Delete(kit.DataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ASuccessfulHardwareStartProvesTheBackend()
    {
        var kit = await HardwareKitAsync();

        var started = await kit.Admission.StartAsync(HardwarePlan(), Profile, admitted => Succeed(admitted));

        Assert.AreEqual(PlaybackHardwareBackend.Nvenc, started.Encoder.Backend);
        Assert.IsTrue(started.Encoder.HardwareDecoding);
        Assert.AreEqual(0, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures);
    }

    [TestMethod]
    public async Task AHardwareDecodeFailureRetriesTheSameEncoderWithSoftwareDecodeAndChargesNothing()
    {
        var kit = await HardwareKitAsync();
        var attempts = new List<PlaybackEncoderTarget>();

        var started = await kit.Admission.StartAsync(HardwarePlan(), Profile, admitted =>
        {
            attempts.Add(admitted.Encoder);
            return admitted.Encoder.HardwareDecoding ? Fail(admitted, new InvalidOperationException("cuvid: no decoder")) : Succeed(admitted);
        });

        Assert.AreEqual(2, attempts.Count);
        Assert.AreEqual(PlaybackHardwareBackend.Nvenc, started.Encoder.Backend, "The encoder works; only the decoder is blamed.");
        Assert.IsFalse(started.Encoder.HardwareDecoding);
        Assert.IsTrue(kit.Hardware.IsHardwareDecodingDisabled(PlaybackHardwareBackend.Nvenc));
        Assert.IsFalse(kit.Hardware.Resolve("h264_nvenc").HardwareDecoding, "Later sessions skip the failing decoder.");
        Assert.AreEqual(0, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures);
        Assert.AreEqual(1, kit.Slots.Active(PlaybackCostClass.HardwareVideo), "Only the successful attempt holds a slot.");
    }

    [TestMethod]
    public async Task AHardwareFailureIsOnlyChargedAfterTheSoftwareFallbackOfTheSameRequestWorks()
    {
        var kit = await HardwareKitAsync();
        var plan = Transcode(Video(sourceCodec: "hevc", encoder: "h264_nvenc"));

        for (var request = 1; request <= PlaybackBackendBreaker.FailureThreshold; request++)
        {
            Assert.IsFalse(kit.Breaker.State(PlaybackHardwareBackend.Nvenc).IsOpen);
            var started = await kit.Admission.StartAsync(plan, Profile, admitted => admitted.Encoder.IsHardware ? Fail(admitted, new InvalidOperationException("Device creation failed")) : Succeed(admitted));
            Assert.AreEqual(PlaybackHardwareBackend.Software, started.Encoder.Backend);
            started.Lease!.Dispose();
            Assert.AreEqual(request, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures);
        }

        var open = kit.Breaker.State(PlaybackHardwareBackend.Nvenc);
        Assert.IsTrue(open.IsOpen, "Three requests that software then served opened the breaker.");
        Assert.AreEqual("Device creation failed", open.LastFailureDetail);
    }

    [TestMethod]
    public async Task WhenSoftwareFailsTooTheSourceIsAtFaultAndNothingIsCharged()
    {
        var kit = await HardwareKitAsync();
        var plan = Transcode(Video(sourceCodec: "hevc", encoder: "h264_nvenc"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Admission.StartAsync(plan, Profile, admitted => Fail(admitted, new InvalidOperationException("Invalid data found when processing input"))));

        StringAssert.Contains(failure.Message, "Invalid data");
        Assert.AreEqual(0, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures);
        Assert.AreEqual(0, kit.Slots.Active(PlaybackCostClass.HardwareVideo) + kit.Slots.Active(PlaybackCostClass.SoftwareVideo), "Every failed attempt gave its slot back.");
    }

    [TestMethod]
    public async Task ATimeoutOrARefusalIsNeverRetriedOrCharged()
    {
        var kit = await HardwareKitAsync();
        var plan = Transcode(Video(sourceCodec: "hevc", encoder: "h264_nvenc"));
        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => kit.Admission.StartAsync(plan, Profile, admitted =>
        {
            attempts++;
            return Fail(admitted, new TimeoutException("no first segment"));
        }));
        await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => kit.Admission.StartAsync(plan, Profile, admitted =>
        {
            attempts++;
            return Fail(admitted, new PlaybackAdmissionRefusedException(PlaybackAdmissionCodes.CacheBudgetExhausted));
        }));
        await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => kit.Admission.StartAsync(plan, Profile, admitted =>
        {
            attempts++;
            return Fail(admitted, new PlaybackAdmissionRefusedException(PlaybackAdmissionCodes.CacheFolderNotOwned));
        }));

        Assert.AreEqual(4, attempts, "A refusal is one attempt each; a hardware timeout is retried once on software, which timed out too.");
        Assert.AreEqual(0, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures);
    }

    [TestMethod]
    public async Task AHungHardwareDriverThatTimesOutIsChargedOnceSoftwareServesTheRequest()
    {
        var kit = await HardwareKitAsync();
        var plan = Transcode(Video(sourceCodec: "hevc", encoder: "h264_nvenc"));

        for (var request = 1; request <= PlaybackBackendBreaker.FailureThreshold; request++)
        {
            var started = await kit.Admission.StartAsync(plan, Profile, admitted => admitted.Encoder.IsHardware ? Fail(admitted, new TimeoutException("no first segment")) : Succeed(admitted));
            started.Lease!.Dispose();
            Assert.AreEqual(request, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures);
        }

        Assert.IsTrue(kit.Breaker.State(PlaybackHardwareBackend.Nvenc).IsOpen, "A hung driver opens the breaker like any other failing one.");
    }

    [TestMethod]
    public async Task ACancelledStartIsNeitherRetriedNorChargedAndKeepsNoSlot()
    {
        var kit = await HardwareKitAsync();
        var plan = Transcode(Video(sourceCodec: "hevc", encoder: "h264_nvenc"));
        var attempts = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => kit.Admission.StartAsync(plan, Profile, admitted =>
        {
            attempts++;
            return Fail(admitted, new OperationCanceledException());
        }));

        Assert.AreEqual(1, attempts, "A disconnected client does not start a second encoder.");
        Assert.AreEqual(0, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures);
        Assert.AreEqual(0, kit.Slots.ActiveFor(Profile), "The failed attempt's slot and the profile's share are back.");
    }

    [TestMethod]
    public async Task AFullSoftwareClassRefusesTheFallbackAndNothingIsCharged()
    {
        var kit = await HardwareKitAsync();
        var plan = Transcode(Video(sourceCodec: "hevc", encoder: "h264_nvenc"));
        using var first = kit.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        using var second = kit.Slots.TryAcquire(PlaybackCostClass.SoftwareVideo);

        var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => kit.Admission.StartAsync(plan, Profile, admitted => Fail(admitted, new InvalidOperationException("Device creation failed"))));

        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderBusy, refusal.Code);
        Assert.AreEqual(0, kit.Breaker.State(PlaybackHardwareBackend.Nvenc).ConsecutiveFailures, "No software run proved the failure, so nothing is charged.");
    }

    private static PlaybackPlan HardwarePlan() => Transcode(Video(encoder: "h264_nvenc"));

    private static async Task<PlaybackServerTestKit> HardwareKitAsync()
    {
        var kit = PlaybackServerTestKit.Create(new ManualTimeProvider(DateTimeOffset.Parse("2026-10-05T10:00:00Z")), PlaybackServerTestKit.Ffmpeg(["libx264", "h264_nvenc"], ["cuda"]));
        await kit.Hardware.DetectAsync(CancellationToken.None);
        return kit;
    }

    private static Task<PlaybackAdmission> Succeed(PlaybackAdmission admitted) => Task.FromResult(admitted);

    // A failed attempt releases its own slot, exactly like the HLS manager and the progressive start do.
    private static Task<PlaybackAdmission> Fail(PlaybackAdmission admitted, Exception exception)
    {
        admitted.Lease?.Dispose();
        throw exception;
    }
}
