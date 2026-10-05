using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>
/// Automatic quality at runtime (#403): what the server advises from a session's telemetry and measured transcode speed. Every case runs on
/// a clock the test moves, so the 20 s minimum age, the 60 s stable window and the 120 s cooldown are exact boundaries, not timing luck.
/// </summary>
[TestClass]
public sealed class PlaybackAdaptationTests
{
    private const string Viewer = "adaptation-viewer";
    private static readonly DateTimeOffset s_start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A transcode delivering 3.8 Mbps (the 4 Mbps rung) of a 12 Mbps source: the next tier is 8 Mbps, which needs 12 Mbps of delivery.</summary>
    private static PlaybackPlan AutoPlan(PlaybackQualityPreset requested = PlaybackQualityPreset.Auto, int delivered = 3_800, int? source = 12_000) =>
        Transcode(Video()) with
        {
            Quality = new PlaybackQualityResolution(requested, PlaybackNetworkClass.Remote, delivered, PlaybackLimitSource.Network, source, delivered),
            Buffer = PlaybackBufferPolicy.For(PlaybackBufferPreset.Normal, PlaybackDeliveryMode.Transcode)
        };

    private sealed class Harness
    {
        private long _sequence;
        private int _stalls;

        public Harness(PlaybackPlan? plan = null, PlaybackAdaptationDirective? directive = null)
        {
            Clock = new ManualTimeProvider(s_start);
            Store = new PlaybackStreamSessionStore(Clock);
            Session = Store.Create(
                Viewer,
                new PlaybackVideoTarget(Guid.NewGuid(), Guid.NewGuid()),
                Guid.NewGuid(),
                "/media/episode.mkv",
                1400,
                plan ?? AutoPlan(),
                new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"),
                adaptation: directive);
        }

        public ManualTimeProvider Clock { get; }

        public PlaybackStreamSessionStore Store { get; }

        public PlaybackStreamSession Session { get; }

        public PlaybackSessionAdvice Advice => Store.Advise(Session.Id, Viewer)!;

        /// <summary>Reports once and moves the clock first, the way a player reports every 5 s; <c>newStalls</c> add to the session's cumulative count.</summary>
        public void Report(int afterSeconds = 5, PlaybackClientState state = PlaybackClientState.Playing, double buffer = 20, int? throughput = 13_000, int newStalls = 0)
        {
            Clock.Advance(TimeSpan.FromSeconds(afterSeconds));
            _stalls += newStalls;
            var update = new PlaybackTelemetryUpdate(++_sequence, state, buffer, throughput, _stalls, _stalls * 1_000L, _sequence * 5d);
            Assert.IsTrue(PlaybackTelemetryRules.TryValidate(update, Clock.GetUtcNow(), out var report));
            Assert.IsTrue(Store.ReportTelemetry(Session.Id, Viewer, report));
        }

        /// <summary>Plays steadily with the given report values until <paramref name="totalSeconds"/> have passed since the session began.</summary>
        public void PlayUntil(int totalSeconds, double buffer = 20, int? throughput = 13_000)
        {
            while ((Clock.GetUtcNow() - s_start).TotalSeconds + 5 <= totalSeconds)
            {
                Report(buffer: buffer, throughput: throughput);
            }
        }

        public void SlowTranscode()
        {
            var progress = Session.BeginTranscodeRun(PlaybackHardwareBackend.Software)!;
            progress(new PlaybackTranscodeSample(0.6, 20, 30));
            Clock.Advance(PlaybackTranscodeMeter.SustainedFor);
            progress(new PlaybackTranscodeSample(0.6, 20, 40));
        }
    }

    [TestMethod]
    public void NothingIsAdvisedBeforeThePlayerHasShownAnything()
    {
        var harness = new Harness();

        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "No report yet.");
        harness.Report(buffer: 1, throughput: 100, newStalls: 5);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "A delivery that just started has had no time to show evidence.");
        harness.Report(afterSeconds: 10, buffer: 1, throughput: 100);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "15 s of age is still below the 20 s minimum.");
        harness.Report(buffer: 1, throughput: 100);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepDown, harness.Advice.Decision.Advice, "At 20 s the same evidence counts.");
    }

    [TestMethod]
    public void TwoStallsInTheLastMinuteStepDownAndOneDoesNot()
    {
        var harness = new Harness();
        harness.PlayUntil(20);
        harness.Report(buffer: 10, newStalls: 1);

        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "One stall with a healthy buffer is not the rule.");

        harness.Report(buffer: 10, newStalls: 1);
        var decision = harness.Advice.Decision;

        Assert.AreEqual(PlaybackAdaptationAdvice.StepDown, decision.Advice);
        Assert.AreEqual(PlaybackAdaptationReason.Stalls, decision.Reason);
    }

    [TestMethod]
    public void AStallThatIsOlderThanAMinuteNoLongerCounts()
    {
        var harness = new Harness();
        harness.PlayUntil(20);
        harness.Report(buffer: 10, newStalls: 2);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepDown, harness.Advice.Decision.Advice);

        for (var index = 0; index < 13; index++)
        {
            harness.Report(buffer: 10);
        }

        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "The same two stalls, 65 s ago, are history.");
    }

    [TestMethod]
    public void ALowBufferStepsDownOnlyWhileTheRateIsFalling()
    {
        var stable = new Harness();
        stable.PlayUntil(20);
        stable.Report(buffer: PlaybackAutoQuality.LowBufferSeconds - 0.1, throughput: 13_000);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, stable.Advice.Decision.Advice, "A short buffer with plenty of delivery rate recovers by itself.");

        var starving = new Harness();
        starving.PlayUntil(20);
        starving.Report(buffer: PlaybackAutoQuality.LowBufferSeconds - 0.1, throughput: 3_000);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepDown, starving.Advice.Decision.Advice, "Delivery below the delivered bitrate cannot sustain it.");
        Assert.AreEqual(PlaybackAdaptationReason.LowBuffer, starving.Advice.Decision.Reason);

        var falling = new Harness();
        falling.PlayUntil(20, throughput: 16_000);
        falling.Report(buffer: 3, throughput: 12_000);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepDown, falling.Advice.Decision.Advice, "A rate that fell to 75 % within 30 s is falling.");

        var unknown = new Harness();
        unknown.PlayUntil(20);
        unknown.Report(buffer: 1, throughput: null);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, unknown.Advice.Decision.Advice, "Without a measured rate the low buffer alone says nothing.");

        var boundary = new Harness();
        boundary.PlayUntil(20);
        boundary.Report(buffer: PlaybackAutoQuality.LowBufferSeconds, throughput: 100);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, boundary.Advice.Decision.Advice, "Exactly at the low-buffer threshold is not below it.");
    }

    [TestMethod]
    public void NothingStepsDownBelowTheLowestTierOrWhilePaused()
    {
        var bottom = new Harness(AutoPlan(delivered: 1_000));
        bottom.PlayUntil(20);
        bottom.Report(buffer: 1, throughput: 100, newStalls: 4);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, bottom.Advice.Decision.Advice, "There is no tier below 1 Mbps to step to.");

        var paused = new Harness();
        paused.PlayUntil(20);
        paused.Report(state: PlaybackClientState.Paused, buffer: 1, throughput: 100, newStalls: 4);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, paused.Advice.Decision.Advice, "A paused player is not struggling.");

        var gone = new Harness();
        gone.PlayUntil(20);
        gone.Report(buffer: 1, throughput: 100, newStalls: 4);
        gone.Clock.Advance(PlaybackAdaptation.FreshFor + TimeSpan.FromSeconds(1));
        Assert.AreEqual(PlaybackAdaptationAdvice.None, gone.Advice.Decision.Advice, "A report that old describes nobody.");
    }

    [TestMethod]
    [DataRow(PlaybackQualityPreset.Original)]
    [DataRow(PlaybackQualityPreset.Mbps4)]
    [DataRow(PlaybackQualityPreset.Mbps1)]
    public void ANoticeLockedQualityIsNeverChangedByThroughputOrStalls(PlaybackQualityPreset locked)
    {
        var harness = new Harness(AutoPlan(locked));
        harness.PlayUntil(125);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "Perfect telemetry never raises a tier the user fixed.");

        harness.Report(buffer: 1, throughput: 100, newStalls: 5);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "Stalls never lower it either; only Automatic adapts.");
    }

    [TestMethod]
    public void AStepUpNeedsASixtySecondStableWindowAfterTheCooldownAndNeverComesEarlier()
    {
        var harness = new Harness();

        harness.PlayUntil(115);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, harness.Advice.Decision.Advice, "115 s is inside the 120 s cooldown after the delivery started.");

        harness.Report();
        var decision = harness.Advice.Decision;

        Assert.AreEqual(PlaybackAdaptationAdvice.StepUp, decision.Advice, "120 s in, the last 60 s were stable at 13 Mbps against 12 Mbps required.");
        Assert.AreEqual(PlaybackAdaptationReason.ThroughputHeadroom, decision.Reason);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepUp, harness.Advice.Decision.Advice, "Asking again answers the same: the advice is a reading, not an event.");
    }

    [TestMethod]
    public void TheHeadroomBoundaryIsExactlyOnePointFiveTimesTheNextTier()
    {
        var below = new Harness();
        below.PlayUntil(115, throughput: 11_999);
        below.Report(throughput: 11_999);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, below.Advice.Decision.Advice, "1.5 x 8 Mbps is 12 000 kbps; one kbps less is not enough.");

        var exact = new Harness();
        exact.PlayUntil(115, throughput: 12_000);
        exact.Report(throughput: 12_000);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepUp, exact.Advice.Decision.Advice);
    }

    [TestMethod]
    public void OneBadMomentInTheWindowOrAMissingMeasurementBreaksTheStableWindow()
    {
        Harness Stable()
        {
            var harness = new Harness();
            harness.PlayUntil(100);
            return harness;
        }

        var dip = Stable();
        dip.Report(throughput: 5_000);
        dip.PlayUntil(120);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, dip.Advice.Decision.Advice, "A single report below the headroom in the last 60 s.");

        var unmeasured = Stable();
        unmeasured.Report(throughput: null);
        unmeasured.PlayUntil(120);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, unmeasured.Advice.Decision.Advice, "No measured rate cannot confirm a rate.");

        var shallow = Stable();
        shallow.Report(buffer: PlaybackBufferPolicy.For(PlaybackBufferPreset.Normal, PlaybackDeliveryMode.Transcode).LowWaterSeconds - 1);
        shallow.PlayUntil(120);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, shallow.Advice.Decision.Advice, "The buffer fell below the low-water mark inside the window.");

        var stalled = Stable();
        stalled.Report(newStalls: 1);
        stalled.PlayUntil(120);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, stalled.Advice.Decision.Advice, "A stall inside the window.");

        var paused = Stable();
        paused.Report(state: PlaybackClientState.Buffering);
        paused.PlayUntil(120);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, paused.Advice.Decision.Advice, "Waiting for media is not steady playback.");

        var hole = Stable();
        hole.Report(afterSeconds: 20);
        hole.PlayUntil(135);
        hole.Report();
        Assert.AreEqual(PlaybackAdaptationAdvice.None, hole.Advice.Decision.Advice, "Reports 20 s apart leave a hole in the window.");
    }

    [TestMethod]
    public void AStepUpNeverExceedsTheSourceTheCeilingOrAnEncoderWithoutMargin()
    {
        var atSource = new Harness(AutoPlan(delivered: 11_900, source: 12_000));
        atSource.PlayUntil(125, throughput: 60_000);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, atSource.Advice.Decision.Advice, "Already at the source: no tier above.");

        var ceiling = new Harness(AutoPlan(), new PlaybackAdaptationDirective(PlaybackAdaptationAdvice.None, null, 4_000, []));
        ceiling.PlayUntil(125);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, ceiling.Advice.Decision.Advice, "The encoder already failed to sustain 8 Mbps for this title.");

        var thin = new Harness();
        thin.PlayUntil(100);
        var progress = thin.Session.BeginTranscodeRun(PlaybackHardwareBackend.Software)!;
        progress(new PlaybackTranscodeSample(1.05, 30, 40));
        thin.Clock.Advance(PlaybackTranscodeMeter.SustainedFor);
        progress(new PlaybackTranscodeSample(1.05, 30, 50));
        thin.PlayUntil(120);
        Assert.AreEqual(PlaybackTranscodeSpeedState.BelowTarget, thin.Advice.Transcode.State);
        Assert.AreEqual(PlaybackAdaptationAdvice.None, thin.Advice.Decision.Advice, "Real time without the 1.15x margin leaves no room for a costlier tier.");
    }

    [TestMethod]
    public void AChangeOpensANewCooldownSoTheNewDeliveryCannotStepStraightBackUp()
    {
        var first = new Harness();
        first.PlayUntil(125);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepUp, first.Advice.Decision.Advice);

        // The player follows the advice: a new session replaces the old one, planned at the higher tier.
        var directive = first.Store.NextDirective(first.Session);
        Assert.AreEqual(PlaybackAdaptationAdvice.StepUp, directive.Advice, "The next plan is made under the advice the session ended with.");
        var next = first.Store.Create(Viewer, first.Session.Target, first.Session.MediaFileId, first.Session.SourcePath, 1400, AutoPlan(delivered: 7_800), first.Session.Selections, first.Session.Id, adaptation: directive);

        var seq = 100L;
        for (var elapsed = 5; elapsed < 120; elapsed += 5)
        {
            first.Clock.Advance(TimeSpan.FromSeconds(5));
            Assert.IsTrue(PlaybackTelemetryRules.TryValidate(new PlaybackTelemetryUpdate(++seq, PlaybackClientState.Playing, 20, 30_000, 0, 0, elapsed), first.Clock.GetUtcNow(), out var report));
            first.Store.ReportTelemetry(next.Id, Viewer, report);
            Assert.AreEqual(PlaybackAdaptationAdvice.None, first.Store.Advise(next.Id, Viewer)!.Decision.Advice, $"{elapsed} s after the change: still cooling down.");
        }
    }

    [TestMethod]
    public void ATooSlowTranscodeStepsDownEvenWhenTheQualityIsLockedAndNeverBeforeItLasted()
    {
        var locked = new Harness(AutoPlan(PlaybackQualityPreset.Mbps8));
        var progress = locked.Session.BeginTranscodeRun(PlaybackHardwareBackend.Software)!;
        progress(new PlaybackTranscodeSample(0.8, 25, 30));
        Assert.AreEqual(PlaybackAdaptationAdvice.None, locked.Advice.Decision.Advice, "One slow report is no verdict.");

        locked.Clock.Advance(PlaybackTranscodeMeter.SustainedFor);
        progress(new PlaybackTranscodeSample(0.8, 25, 40));

        var decision = locked.Advice.Decision;
        Assert.AreEqual(PlaybackAdaptationAdvice.StepDown, decision.Advice, "The server's capacity outranks a selection: a fixed tier it cannot encode cannot be played.");
        Assert.AreEqual(PlaybackAdaptationReason.TranscodeTooSlow, decision.Reason);
        Assert.AreEqual(0.8, locked.Advice.Transcode.Speed);
    }

    [TestMethod]
    public void TheTelemetryHistoryIsBoundedAndPureReadsNeverChangeIt()
    {
        var harness = new Harness();
        for (var index = 0; index < 500; index++)
        {
            harness.Report(afterSeconds: 1);
        }

        var recent = harness.Session.Telemetry.Recent();
        Assert.IsTrue(recent.Count <= 64, "A client that reports too often cannot grow the session.");
        Assert.IsTrue(recent[^1].ReportedAtUtc - recent[0].ReportedAtUtc <= TimeSpan.FromMinutes(3));

        var before = harness.Session.LastSeenUtc;
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        _ = harness.Advice;
        _ = harness.Advice;
        Assert.AreEqual(before, harness.Session.LastSeenUtc, "Asking for advice is a read and never keeps a session alive.");
    }

    [TestMethod]
    public void TheTiersAboveAndBelowFollowTheLadderAndNeverPassTheSource()
    {
        Assert.AreEqual(2_000, PlaybackQualityPresets.TierBelow(3_800));
        Assert.AreEqual(8_000, PlaybackQualityPresets.TierBelow(12_000));
        Assert.AreEqual(1_000, PlaybackQualityPresets.TierBelow(2_000));
        Assert.IsNull(PlaybackQualityPresets.TierBelow(1_000), "Nothing lies below the lowest rung.");

        Assert.AreEqual(8_000, PlaybackQualityPresets.TierAbove(3_800, 12_000), "A delivery just under the 4 Mbps rung still counts as that rung.");
        Assert.AreEqual(4_000, PlaybackQualityPresets.TierAbove(2_000, 12_000));
        Assert.AreEqual(10_000, PlaybackQualityPresets.TierAbove(8_000, 10_000), "The source is the top tier when it lies below the next rung.");
        Assert.AreEqual(40_000, PlaybackQualityPresets.TierAbove(20_000, 40_000), "Above the top rung the source itself is the next tier.");
        Assert.IsNull(PlaybackQualityPresets.TierAbove(20_000, null), "An unknown source above the top rung offers nothing.");
        Assert.IsNull(PlaybackQualityPresets.TierAbove(11_000, 12_000), "Within the slack of the source it already is the source.");
        Assert.AreEqual(8_000, PlaybackQualityPresets.TierAbove(3_800, null), "An unknown source does not forbid the next rung (the encoder never exceeds the source).");
    }

    [TestMethod]
    public void TheNextPlansLimitFollowsTheDirective()
    {
        var network = new PlaybackNetworkConditions(PlaybackNetworkClass.Remote, EstimatedThroughputKbps: 6_000);
        var up = new PlaybackAdaptationDirective(PlaybackAdaptationAdvice.StepUp, PlaybackAdaptationReason.ThroughputHeadroom, null, []);

        var raised = PlaybackAutoQuality.Resolve(PlaybackQualityPreset.Auto, network, 3_800, up, 12_000);

        Assert.AreEqual(8_000, raised.MaxKbps, "The live evidence outranks the startup hint of 4.2 Mbps.");
        Assert.AreEqual(PlaybackLimitSource.Headroom, raised.Source);
        Assert.AreEqual(4_200, PlaybackAutoQuality.Resolve(PlaybackQualityPreset.Auto, network, 3_800).MaxKbps, "Without the advice nothing is raised.");

        var down = new PlaybackAdaptationDirective(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.LowBuffer, null, []);
        var lowered = PlaybackAutoQuality.Resolve(PlaybackQualityPreset.Auto, network, 3_800, down, 12_000);
        Assert.AreEqual(2_000, lowered.MaxKbps);
        Assert.AreEqual(PlaybackLimitSource.Stalls, lowered.Source);

        var capped = new PlaybackAdaptationDirective(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.TranscodeTooSlow, 4_000, []);
        Assert.AreEqual(4_000, PlaybackAutoQuality.Resolve(PlaybackQualityPreset.Original, network, 8_000, capped, 12_000).MaxKbps, "A capacity ceiling caps even Original.");
        Assert.AreEqual(PlaybackLimitSource.TranscodeSpeed, PlaybackAutoQuality.Resolve(PlaybackQualityPreset.Mbps8, network, 8_000, capped, 12_000).Source);
        Assert.AreEqual(2_000, PlaybackAutoQuality.Resolve(PlaybackQualityPreset.Mbps2, network, 2_000, capped, 12_000).MaxKbps, "A lower selection stays.");
    }

    [TestMethod]
    public void ATooSlowTierLowersTheCeilingOneRungAndATooSlowFloorMarksTheEncoder()
    {
        var slow = new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepDown, PlaybackAdaptationReason.TranscodeTooSlow);

        var lowered = PlaybackAdaptation.NextDirective(slow, PlaybackAdaptationDirective.None, 3_800, PlaybackHardwareBackend.Nvenc);
        Assert.AreEqual(2_000, lowered.CeilingKbps);
        Assert.AreEqual(0, lowered.SlowBackends.Count, "Lower quality comes first; the encoder is not blamed yet.");

        var floor = PlaybackAdaptation.NextDirective(slow, lowered, 1_000, PlaybackHardwareBackend.Nvenc);
        CollectionAssert.AreEqual(new[] { PlaybackHardwareBackend.Nvenc }, floor.SlowBackends.ToArray());
        Assert.IsNull(floor.CeilingKbps, "Another encoder starts from the requested tier again.");

        var again = PlaybackAdaptation.NextDirective(slow, floor, 1_000, PlaybackHardwareBackend.Nvenc);
        Assert.AreEqual(1, again.SlowBackends.Count, "The same encoder is recorded once.");

        var kept = PlaybackAdaptation.NextDirective(new PlaybackAdaptationDecision(PlaybackAdaptationAdvice.StepUp, PlaybackAdaptationReason.ThroughputHeadroom), floor, 3_800, PlaybackHardwareBackend.Software);
        CollectionAssert.AreEqual(floor.SlowBackends.ToArray(), kept.SlowBackends.ToArray(), "What the server learned about its capacity outlives a quality change.");
    }
}
