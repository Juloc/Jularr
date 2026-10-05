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

    private static void RunTooSlow(PlaybackServerTestKit kit, PlaybackStreamSession session, PlaybackHardwareBackend backend)
    {
        var progress = session.BeginTranscodeRun(backend)!;
        progress(new PlaybackTranscodeSample(0.7, 15, 30));
        ((ManualTimeProvider)kit.Time).Advance(PlaybackTranscodeMeter.SustainedFor);
        progress(new PlaybackTranscodeSample(0.7, 15, 40));
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
