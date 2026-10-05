using System.Net;
using Jularr.Web.Data;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Tests;

/// <summary>
/// The re-plan of a session whose transcode cannot keep up (#403): lower quality first, then another encoder, then unavailable with a
/// reason. Runs through the real <see cref="PlaybackPlanService"/> with fake backends and a clock the test moves; slowness is fed as canned
/// progress samples, and the circuit breaker must never hear about it.
/// </summary>
[TestClass]
public sealed class PlaybackReplanTests
{
    private const string Viewer = "replan-viewer";
    private const string ChromeAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    private static readonly DateTimeOffset s_start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly MediaInventoryFixture _fixture;

        private Rig(MediaInventoryFixture fixture, ManualTimeProvider clock, PlaybackServerTestKit kit, MediaFile media)
        {
            _fixture = fixture;
            Clock = clock;
            Kit = kit;
            Media = media;
            Store = new PlaybackStreamSessionStore(clock);
            Service = new PlaybackPlanService(fixture.Db, fixture.Inventory, Store, kit.Capabilities);
        }

        public ManualTimeProvider Clock { get; }

        public PlaybackServerTestKit Kit { get; }

        public MediaFile Media { get; }

        public PlaybackStreamSessionStore Store { get; }

        public PlaybackPlanService Service { get; }

        public MediaInventoryFixture Fixture => _fixture;

        /// <summary>
        /// A 20 Mbps file (1080p H.264 unless <paramref name="probe"/> says otherwise) on a server whose ffmpeg offers NVENC, and passes its test
        /// encode, when <paramref name="withHardware"/>.
        /// </summary>
        public static async Task<Rig> CreateAsync(bool withHardware, string? probe = null)
        {
            var fixture = await MediaInventoryFixture.CreateAsync();
            var media = await fixture.AddMediaAsync("episode.mp4", new byte[4096]);
            media.SizeBytes = 3_600_000_000;
            await fixture.Db.SaveChangesAsync();
            fixture.Runner.Returns(media.Path, probe ?? MediaProbeFixtures.H264Stereo);

            var clock = new ManualTimeProvider(s_start);
            var runner = withHardware
                ? PlaybackServerTestKit.Ffmpeg(["libx264", "h264_nvenc"], ["cuda"])
                : PlaybackServerTestKit.Ffmpeg(["libx264"], []);
            var kit = PlaybackServerTestKit.Create(clock, runner);
            await kit.Hardware.DetectAsync(CancellationToken.None);
            return new Rig(fixture, clock, kit, media);
        }

        public async Task<PlaybackPlanOutcome> PlanAsync(Guid? replaces = null, PlaybackQualityPreset? quality = null, MediaFile? media = null)
        {
            var input = new PlaybackPlanInput(
                null,
                "web",
                ChromeAgent,
                IPAddress.Parse("203.0.113.9"),
                Quality: quality,
                ModePreference: PlaybackModePreference.AlwaysTranscode,
                ReplacesSessionId: replaces);
            return (await Service.PlanAsync((media ?? Media).EpisodeId!.Value, Viewer, input, CancellationToken.None))!;
        }

        /// <summary>The session's encode runs under real time for longer than it takes to be believed.</summary>
        public void RunTooSlow(PlaybackStreamSession session)
        {
            var backend = PlaybackHardwareBackends.FromEncoder(session.Plan.Video!.Encoder);
            var progress = session.BeginTranscodeRun(backend) ?? throw new AssertFailedException($"Not a video transcode: {session.Plan.Mode} {session.Plan.Video}");
            progress(new PlaybackTranscodeSample(0.6, 12, 30));
            Clock.Advance(PlaybackTranscodeMeter.SustainedFor);
            progress(new PlaybackTranscodeSample(0.6, 12, 40));
        }

        public ValueTask DisposeAsync() => _fixture.DisposeAsync();
    }

    private static string Describe(PlaybackPlanOutcome outcome) =>
        outcome.Plan.Mode == PlaybackDeliveryMode.Unavailable ? "unavailable" : $"{outcome.Plan.Video!.Encoder}@{outcome.Plan.Quality.LimitKbps}/{outcome.Plan.Quality.LimitSource}";

    [TestMethod]
    public async Task ATooSlowTranscodeLowersTheQualityThenTriesAnotherEncoderThenIsUnavailableAndNeverChargesTheBreaker()
    {
        // 10-bit HDR HEVC: this client cannot play or remux it, so a transcode is the only way and nothing softens the refusal.
        await using var rig = await Rig.CreateAsync(withHardware: true, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        var seen = new List<string>();

        var outcome = await rig.PlanAsync();
        for (var guard = 0; guard < 20 && outcome.Session is { } session; guard++)
        {
            seen.Add(Describe(outcome));
            rig.RunTooSlow(session);
            outcome = await rig.PlanAsync(replaces: session.Id);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                "h264_nvenc@8000/NetworkDefault",
                "h264_nvenc@4000/TranscodeSpeed",
                "h264_nvenc@2000/TranscodeSpeed",
                "h264_nvenc@1000/TranscodeSpeed",
                "libx264@8000/NetworkDefault",
                "libx264@4000/TranscodeSpeed",
                "libx264@2000/TranscodeSpeed",
                "libx264@1000/TranscodeSpeed"
            },
            seen,
            "Quality steps down on each encoder; at the floor the next encoder starts from the requested tier again.");
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, outcome.Plan.Mode);
        Assert.IsNull(outcome.Session);
        Assert.IsTrue(outcome.Plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.TranscodeUnsustainable && x.Severity == PlaybackReasonSeverity.Blocker), "The refusal says why, with a stable code.");
        Assert.AreEqual(0, rig.Store.Count, "The slow transcode behind the replaced session is stopped, not left running unused.");
        foreach (var backend in Enum.GetValues<PlaybackHardwareBackend>())
        {
            Assert.AreEqual(0, rig.Kit.Breaker.State(backend).ConsecutiveFailures, $"Slowness is the server's capacity and never a failure of {backend}.");
            Assert.IsFalse(rig.Kit.Breaker.State(backend).IsOpen);
        }
    }

    [TestMethod]
    public async Task WithoutHardwareTheFloorOnSoftwareIsUnavailableAtOnce()
    {
        await using var rig = await Rig.CreateAsync(withHardware: false, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        var seen = new List<string>();

        var outcome = await rig.PlanAsync();
        for (var guard = 0; guard < 20 && outcome.Session is { } session; guard++)
        {
            seen.Add(Describe(outcome));
            rig.RunTooSlow(session);
            outcome = await rig.PlanAsync(replaces: session.Id);
        }

        CollectionAssert.AreEqual(new[] { "libx264@8000/NetworkDefault", "libx264@4000/TranscodeSpeed", "libx264@2000/TranscodeSpeed", "libx264@1000/TranscodeSpeed" }, seen);
        Assert.AreEqual(PlaybackDeliveryMode.Unavailable, outcome.Plan.Mode, "No other encoder exists to try.");
    }

    [TestMethod]
    public async Task ADeviceThatPlaysTheOriginalGetsItWhenNoEncoderKeepsUp()
    {
        await using var rig = await Rig.CreateAsync(withHardware: false);
        var outcome = await rig.PlanAsync();
        PlaybackStreamSession? last = null;
        for (var guard = 0; guard < 20 && outcome.Session is { Plan.Mode: PlaybackDeliveryMode.Transcode } session; guard++)
        {
            last = session;
            rig.RunTooSlow(session);
            outcome = await rig.PlanAsync(replaces: session.Id);
        }

        Assert.IsNotNull(last);
        Assert.AreEqual(PlaybackDeliveryMode.DirectPlay, outcome.Plan.Mode, "The only limits against the untouched file are preferences, so a server that cannot convert plays it as it is.");
        Assert.IsTrue(outcome.Plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.TranscodeUnsustainable));
        Assert.IsTrue(outcome.Plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.LimitIgnoredNoTranscoder));
        Assert.IsNull(rig.Store.Get(last.Id, Viewer), "The session of the slow transcode is replaced, so its process stops.");
    }

    [TestMethod]
    public async Task AFixedQualityTierIsLoweredToo()
    {
        await using var rig = await Rig.CreateAsync(withHardware: false);

        var first = await rig.PlanAsync(quality: PlaybackQualityPreset.Mbps12);
        rig.RunTooSlow(first.Session!);
        var second = await rig.PlanAsync(replaces: first.Session!.Id, quality: PlaybackQualityPreset.Mbps12);

        Assert.AreEqual(12_000, first.Plan.Quality.LimitKbps);
        Assert.AreEqual(8_000, second.Plan.Quality.LimitKbps, "The selection says 12 Mbps; what the server's encoder sustains caps it.");
        Assert.AreEqual(PlaybackLimitSource.TranscodeSpeed, second.Plan.Quality.LimitSource);
        Assert.IsTrue(second.Plan.Reasons.Any(x => x.Code == PlaybackReasonCodes.TranscodeTooSlow), "The plan explains the lowered tier.");
    }

    [TestMethod]
    public async Task ATranscodeThatKeepsUpChangesNothingAndAnotherTitleStartsFresh()
    {
        await using var rig = await Rig.CreateAsync(withHardware: false);
        var first = await rig.PlanAsync();
        var progress = first.Session!.BeginTranscodeRun(PlaybackHardwareBackend.Software)!;
        progress(new PlaybackTranscodeSample(2.5, 60, 30));
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        progress(new PlaybackTranscodeSample(2.5, 60, 90));

        var same = await rig.PlanAsync(replaces: first.Session.Id);

        Assert.AreEqual(Describe(first), Describe(same), "A re-plan for another reason keeps the tier of a transcode that is fast enough.");

        rig.RunTooSlow(same.Session!);
        var otherMedia = await rig.Fixture.AddMediaAsync("other.mp4", new byte[4096]);
        otherMedia.SizeBytes = 3_600_000_000;
        await rig.Fixture.Db.SaveChangesAsync();
        rig.Fixture.Runner.Returns(otherMedia.Path, MediaProbeFixtures.H264Stereo);
        var other = await rig.PlanAsync(replaces: same.Session.Id, media: otherMedia);

        Assert.AreEqual("libx264@8000/NetworkDefault", Describe(other), "What was learned about one title's tiers says nothing about another title.");
    }

    [TestMethod]
    public async Task AStableWindowOfHeadroomRaisesTheNextPlanByOneTierOnly()
    {
        await using var rig = await Rig.CreateAsync(withHardware: false);
        var first = await rig.PlanAsync();
        var session = first.Session!;
        var sequence = 0L;

        // 20 Mbps of source: the next tier above the 8 Mbps delivery is 12 Mbps and needs 18 Mbps of delivery.
        for (var elapsed = 5; elapsed <= 125; elapsed += 5)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            Assert.IsTrue(PlaybackTelemetryRules.TryValidate(new PlaybackTelemetryUpdate(++sequence, PlaybackClientState.Playing, 25, 19_000, 0, 0, elapsed), rig.Clock.GetUtcNow(), out var report));
            Assert.IsTrue(rig.Store.ReportTelemetry(session.Id, Viewer, report));
        }

        Assert.AreEqual(PlaybackAdaptationAdvice.StepUp, rig.Store.Advise(session.Id, Viewer)!.Decision.Advice);
        var raised = await rig.PlanAsync(replaces: session.Id);

        Assert.AreEqual("libx264@12000/Headroom", Describe(raised));
        Assert.AreEqual(PlaybackAdaptationAdvice.StepUp, raised.Session!.Adaptation.Advice, "The new session records what it was planned under.");
    }
}
