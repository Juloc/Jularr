using System.Text.Json;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>
/// Overload behavior of the playback delivery (#403, "Admission control and overload behavior"): a server whose running transcodes stay
/// under real time refuses a new one at once with an explicit code and a retry hint instead of queueing it, and every refusal carries a
/// stable code, a text and its retry semantics.
/// </summary>
[TestClass]
public sealed class PlaybackOverloadTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static PlaybackStreamSession StartTranscode(PlaybackServerTestKit kit, PlaybackPlan? plan = null) =>
        kit.Sessions.Create(
            "viewer",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/media/episode.mkv",
            1400,
            plan ?? Transcode(Video()),
            new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"));

    private static Action<PlaybackTranscodeSample> RunTooSlow(PlaybackServerTestKit kit, PlaybackStreamSession session, PlaybackHardwareBackend backend)
    {
        var progress = session.BeginTranscodeRun(backend)!;
        progress(new PlaybackTranscodeSample(0.7, 15, 30));
        ((ManualTimeProvider)kit.Time).Advance(PlaybackTranscodeMeter.SustainedFor);
        progress(new PlaybackTranscodeSample(0.7, 15, 40));
        return progress;
    }

    [TestMethod]
    public void AServerThatAlreadyRunsASlowTranscodeRefusesAnotherAtOnceInsteadOfQueueingIt()
    {
        var clock = new ManualTimeProvider(s_start);
        var kit = PlaybackServerTestKit.Create(clock);
        var slow = StartTranscode(kit);
        RunTooSlow(kit, slow, PlaybackHardwareBackend.Software);

        var refused = kit.Admission.Admit(Transcode(Video()), "someone-else");

        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, refused.RefusalCode);
        Assert.IsFalse(refused.Admitted);
        Assert.IsNull(refused.Lease, "Nothing is held for a refused delivery.");
        Assert.AreEqual(0, kit.Slots.Active(PlaybackCostClass.SoftwareVideo), "The refusal does not even take a slot: nothing waits.");
        Assert.IsTrue(kit.Admission.Admit(Remux(), "someone-else").Admitted, "A lossless remux costs no encoder time and is never refused for it.");
        Assert.IsTrue(kit.Admission.Admit(AudioOnly(), "someone-else").Admitted);
    }

    [TestMethod]
    public async Task TheRefusalReachesTheStartOfADeliveryAsTheTypedExceptionTheEndpointsTranslate()
    {
        var clock = new ManualTimeProvider(s_start);
        var kit = PlaybackServerTestKit.Create(clock);
        RunTooSlow(kit, StartTranscode(kit), PlaybackHardwareBackend.Software);
        var started = 0;

        var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => kit.Admission.StartAsync(Transcode(Video()), "viewer-2", admitted =>
        {
            started++;
            return Task.FromResult(admitted);
        }));

        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, refusal.Code);
        Assert.AreEqual(0, started, "No ffmpeg is started for a refused delivery.");
    }

    [TestMethod]
    public void OnlyTheSameKindOfEncoderCountsAsLoadAndThePressureEndsWithTheSlowSession()
    {
        var clock = new ManualTimeProvider(s_start);
        var runner = PlaybackServerTestKit.Ffmpeg(["libx264", "h264_nvenc"], ["cuda"]);
        var kit = PlaybackServerTestKit.Create(clock, runner);
        kit.Hardware.DetectAsync(CancellationToken.None).GetAwaiter().GetResult();
        var slow = StartTranscode(kit);
        RunTooSlow(kit, slow, PlaybackHardwareBackend.Software);

        var hardware = kit.Admission.Admit(Transcode(Video(encoder: "h264_nvenc")), "viewer-2");
        Assert.IsTrue(hardware.Admitted, "A slow software encode says nothing about the GPU.");
        hardware.Lease!.Dispose();
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(Transcode(Video()), "viewer-2").RefusalCode);

        kit.Sessions.Remove(slow.Id, "viewer");
        Assert.IsTrue(kit.Admission.Admit(Transcode(Video()), "viewer-2").Admitted, "Once the slow session is gone the machine is not overloaded any more.");
    }

    [TestMethod]
    public void ASilentProgressPipeIsNoLongerAnOverloadAndAWarmUpIsNeverOne()
    {
        var clock = new ManualTimeProvider(s_start);
        var kit = PlaybackServerTestKit.Create(clock);
        var warming = StartTranscode(kit).BeginTranscodeRun(PlaybackHardwareBackend.Software)!;
        warming(new PlaybackTranscodeSample(0.1, 2, PlaybackTranscodeMeter.WarmUpOutputSeconds - 1));
        clock.Advance(TimeSpan.FromMinutes(1));
        warming(new PlaybackTranscodeSample(0.1, 2, PlaybackTranscodeMeter.WarmUpOutputSeconds - 1));
        Assert.IsTrue(kit.Admission.Admit(Transcode(Video()), "viewer-2").Admitted, "ffmpeg's speed is meaningless during startup.");

        var slow = StartTranscode(kit);
        RunTooSlow(kit, slow, PlaybackHardwareBackend.Software);
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(Transcode(Video()), "viewer-2").RefusalCode);

        clock.Advance(PlaybackTranscodeMeter.StaleAfter + TimeSpan.FromSeconds(1));
        Assert.IsTrue(kit.Admission.Admit(Transcode(Video()), "viewer-2").Admitted, "An encode that stopped reporting (finished or dead) is not load.");
    }

    [TestMethod]
    public void EveryRefusalHasATextAndRetrySemanticsAChosenAdminActionHasNone()
    {
        foreach (var code in new[]
                 {
                     PlaybackAdmissionCodes.TranscodingDisabled,
                     PlaybackAdmissionCodes.TranscoderBusy,
                     PlaybackAdmissionCodes.TranscoderOverloaded,
                     PlaybackAdmissionCodes.CacheBudgetExhausted,
                     PlaybackAdmissionCodes.CacheFreeSpaceLow,
                     PlaybackAdmissionCodes.CacheFolderNotOwned,
                     PlaybackAdmissionCodes.ProfileSessionLimit
                 })
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(PlaybackAdmissionCodes.Message(code)), code);
            Assert.IsTrue(UiTranslationResourcesHas($"playback.reason.{code}"), $"{code} needs a consumer-safe text.");
        }

        Assert.AreEqual(30, PlaybackAdmissionCodes.RetryAfterSeconds(PlaybackAdmissionCodes.TranscoderOverloaded));
        Assert.AreEqual(15, PlaybackAdmissionCodes.RetryAfterSeconds(PlaybackAdmissionCodes.TranscoderBusy));
        Assert.IsNull(PlaybackAdmissionCodes.RetryAfterSeconds(PlaybackAdmissionCodes.TranscodingDisabled), "Only an Admin can change it; asking again does not help.");
        Assert.IsNull(PlaybackAdmissionCodes.RetryAfterSeconds(PlaybackAdmissionCodes.CacheFolderNotOwned));
    }

    [TestMethod]
    public async Task ARefusalIsA503WithTheStableCodeAndARetryAfterWhereRetryingCanHelp()
    {
        var overloaded = await ExecuteAsync(ClientApiPlaybackPlanEndpoints.Refused(PlaybackAdmissionCodes.TranscoderOverloaded));
        var disabled = await ExecuteAsync(ClientApiPlaybackPlanEndpoints.Refused(PlaybackAdmissionCodes.TranscodingDisabled));

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, overloaded.Status);
        Assert.AreEqual("30", overloaded.RetryAfter);
        Assert.AreEqual("transcoder_overloaded", overloaded.Code);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, disabled.Status);
        Assert.IsNull(disabled.RetryAfter);
        Assert.AreEqual("transcoding_disabled", disabled.Code);
    }

    [TestMethod]
    public void ASeekInTheSlowSessionItselfIsNeverRefusedForItsOwnSlowness()
    {
        var clock = new ManualTimeProvider(s_start);
        var kit = PlaybackServerTestKit.Create(clock);
        var slow = StartTranscode(kit);
        RunTooSlow(kit, slow, PlaybackHardwareBackend.Software);

        var seek = kit.Admission.Admit(slow.Plan, "viewer", slow);

        Assert.IsTrue(seek.Admitted, "A seek restarts the encode the session already runs; it adds nothing.");
        seek.Lease!.Dispose();
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(slow.Plan, "viewer").RefusalCode, "The same start without the session it belongs to is a new conversion.");
    }

    private static void RunHealthy(PlaybackStreamSession session) => session.BeginTranscodeRun(PlaybackHardwareBackend.Software)!(new PlaybackTranscodeSample(2.0, 50, 30));

    private static PlaybackPlan Delivering(int kbps) =>
        Transcode(Video()) with { Quality = new PlaybackQualityResolution(PlaybackQualityPreset.Auto, PlaybackNetworkClass.Remote, null, PlaybackLimitSource.None, null, kbps) };

    private static PlaybackStreamSession Start(PlaybackServerTestKit kit, string profile, PlaybackPlan plan, Guid? replaces = null) =>
        kit.Sessions.Create(
            profile,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/media/b.mkv",
            1400,
            plan,
            new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"),
            replaces);

    [TestMethod]
    public void AReplanThatReplacesARunningConversionAtTheSameOrALowerBitrateIsNeverRefusedButAHigherOneIs()
    {
        var kit = PlaybackServerTestKit.Create(new ManualTimeProvider(s_start));
        RunTooSlow(kit, StartTranscode(kit), PlaybackHardwareBackend.Software);

        var running = Start(kit, "viewer-2", Delivering(8_000));
        RunHealthy(running);
        var lower = Start(kit, "viewer-2", Delivering(4_000), running.Id);
        RunHealthy(lower);
        var same = Start(kit, "viewer-2", Delivering(4_000), lower.Id);
        RunHealthy(same);
        var higher = Start(kit, "viewer-2", Delivering(8_000), same.Id);
        var fromRemux = Start(kit, "viewer-3", Remux());
        var nowConverting = Start(kit, "viewer-3", Delivering(2_000), fromRemux.Id);

        Assert.IsTrue(kit.Admission.Admit(lower.Plan, "viewer-2", lower).Admitted, "A step-down re-plan lowers the cost: it is never the one to refuse.");
        Assert.IsTrue(kit.Admission.Admit(same.Plan, "viewer-2", same).Admitted);
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(higher.Plan, "viewer-2", higher).RefusalCode, "A higher bitrate is more load than the one it replaces.");
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(nowConverting.Plan, "viewer-3", nowConverting).RefusalCode, "Replacing a remux with a conversion adds load.");
        Assert.AreEqual(8_000, lower.ReplacedTranscodeKbps);
        Assert.IsNull(nowConverting.ReplacedTranscodeKbps);
    }

    [TestMethod]
    public void ReplacingASessionWhoseEncodeNeverRanIsNoWayAroundTheRefusal()
    {
        var kit = PlaybackServerTestKit.Create(new ManualTimeProvider(s_start));
        RunTooSlow(kit, StartTranscode(kit), PlaybackHardwareBackend.Software);

        var refused = Start(kit, "viewer-2", Delivering(4_000));
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(refused.Plan, "viewer-2", refused).RefusalCode);
        // The stock client's retry plans again and names the refused session; a client can also do this on purpose.
        var retry = Start(kit, "viewer-2", Delivering(4_000), refused.Id);
        var again = Start(kit, "viewer-2", Delivering(2_000), retry.Id);

        Assert.IsNull(retry.ReplacedTranscodeKbps, "The replaced session never converted anything.");
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(retry.Plan, "viewer-2", retry).RefusalCode);
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(again.Plan, "viewer-2", again).RefusalCode, "A chain of re-plans of a session that never ran never earns the exemption.");

        var stale = Start(kit, "viewer-3", Delivering(4_000));
        RunHealthy(stale);
        ((ManualTimeProvider)kit.Time).Advance(PlaybackTranscodeMeter.StaleAfter + TimeSpan.FromSeconds(1));
        var afterSilence = Start(kit, "viewer-3", Delivering(4_000), stale.Id);
        Assert.IsNull(afterSilence.ReplacedTranscodeKbps, "An encode whose progress went silent is not a running conversion either.");
    }

    [TestMethod]
    public void ASlowSessionNobodyUsesAnymoreStopsCountingAndTheSweeperEndsItsEncode()
    {
        var clock = new ManualTimeProvider(s_start);
        var kit = PlaybackServerTestKit.Create(clock);
        var abandoned = StartTranscode(kit);
        var encode = RunTooSlow(kit, abandoned, PlaybackHardwareBackend.Software);
        var removed = new List<Guid>();
        kit.Sessions.Removed += session => removed.Add(session.Id);
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, kit.Admission.Admit(Transcode(Video()), "viewer-2").RefusalCode, "While its player is active it is load.");

        clock.Advance(PlaybackAdaptationPolicy.Default.OverloadActivityWindow + TimeSpan.FromSeconds(1));
        encode(new PlaybackTranscodeSample(0.7, 15, 60));

        Assert.IsTrue(kit.Admission.Admit(Transcode(Video()), "viewer-2").Admitted, "A paused or abandoned session does not lock the server for the idle lifetime.");
        // Its encode ran on unthrottled: the sweeper ends the session and with it the output.
        Assert.AreEqual(1, kit.Sessions.ReleaseAbandonedSlowEncodes());
        CollectionAssert.AreEqual(new[] { abandoned.Id }, removed);
        Assert.AreEqual(0, kit.Sessions.ReleaseAbandonedSlowEncodes(), "Nothing is released twice.");

        var active = StartTranscode(kit);
        var activeEncode = RunTooSlow(kit, active, PlaybackHardwareBackend.Software);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.IsNotNull(kit.Sessions.Get(active.Id, "viewer"), "A player that keeps using the session keeps it.");
        clock.Advance(TimeSpan.FromSeconds(60));
        activeEncode(new PlaybackTranscodeSample(0.7, 15, 90));
        Assert.AreEqual(PlaybackTranscodeSpeedState.TooSlow, active.Transcode.Read().State);
        Assert.AreEqual(0, kit.Sessions.ReleaseAbandonedSlowEncodes());
    }

    [TestMethod]
    public async Task ARetryOnSoftwareAfterAFailedHardwareStartIsANewStartAndFacesTheOverloadCheck()
    {
        var clock = new ManualTimeProvider(s_start);
        var runner = PlaybackServerTestKit.Ffmpeg(["libx264", "h264_nvenc"], ["cuda"]);
        var kit = PlaybackServerTestKit.Create(clock, runner);
        await kit.Hardware.DetectAsync(CancellationToken.None);
        RunTooSlow(kit, StartTranscode(kit), PlaybackHardwareBackend.Software);
        var plan = Transcode(Video(encoder: "h264_nvenc"));
        var session = Start(kit, "viewer-2", plan);
        var attempts = new List<PlaybackHardwareBackend>();

        var refusal = await Assert.ThrowsAsync<PlaybackAdmissionRefusedException>(() => kit.Admission.StartAsync<int>(
            plan,
            "viewer-2",
            admitted =>
            {
                attempts.Add(admitted.Encoder.Backend);
                // The endpoint begins the measurement before ffmpeg starts, so the first attempt marks the session as converting.
                session.BeginTranscodeRun(admitted.Encoder.Backend)!(new PlaybackTranscodeSample(2.0, 50, 30));
                admitted.Lease?.Dispose();
                throw new InvalidOperationException("Device creation failed");
            },
            session));

        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderOverloaded, refusal.Code, "The software retry is a new conversion on an overloaded server.");
        Assert.IsTrue(attempts.Count >= 2 && attempts.All(x => x == PlaybackHardwareBackend.Nvenc), "The hardware attempts ran; the software one was refused before it started.");
        Assert.AreEqual(0, kit.Slots.Active(PlaybackCostClass.SoftwareVideo));
        Assert.AreEqual(0, kit.Slots.Active(PlaybackCostClass.HardwareVideo), "Every failed attempt gave its slot back.");
    }

    [TestMethod]
    public void AReplacementTakesOverTheSlotOfTheConversionItReplacesSoAFullClassCanStillSwap()
    {
        var kit = PlaybackServerTestKit.Create(new ManualTimeProvider(s_start));
        var oldSession = Start(kit, "viewer", Delivering(8_000));
        RunHealthy(oldSession);
        using var oldLease = kit.Admission.Admit(oldSession.Plan, "viewer", oldSession).Lease;
        using var otherLease = kit.Admission.Admit(Transcode(Video()), "other").Lease;
        Assert.AreEqual(0, kit.Slots.Available(PlaybackCostClass.SoftwareVideo), "Both software slots are in use.");

        var target = new PlaybackVideoTarget(Guid.NewGuid(), Guid.NewGuid());
        var replacement = kit.Sessions.Create("viewer", target, Guid.NewGuid(), "/media/b.mkv", 1400, Delivering(4_000), oldSession.Selections, oldSession.Id, deferRetirement: true);
        Assert.AreSame(oldSession, replacement.Replacing, "The playing session stays until the new one delivered.");
        Assert.IsNull(kit.Admission.Preflight(replacement.Plan, "viewer", replacement), "The advised swap is possible although the class is full.");
        using var lease = kit.Admission.Admit(replacement.Plan, "viewer", replacement).Lease;
        Assert.IsNotNull(lease, "It takes over the slot of the conversion it replaces for the moment both exist.");

        var stranger = Start(kit, "viewer-9", Delivering(4_000));
        Assert.AreEqual(PlaybackAdmissionCodes.TranscoderBusy, kit.Admission.Preflight(stranger.Plan, "viewer-9", stranger), "Nobody else may use the extra slot.");
    }

    private static bool UiTranslationResourcesHas(string key) => Jularr.Web.Features.Localization.UiTranslationResources.TryGet(key, out _);

    private static async Task<(int Status, string? RetryAfter, string? Code)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var retry = context.Response.Headers.RetryAfter.ToString();
        return (context.Response.StatusCode, retry.Length == 0 ? null : retry, document.RootElement.GetProperty("code").GetString());
    }
}
