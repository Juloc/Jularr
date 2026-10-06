using System.Net;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Playback.Decision;
using static Jularr.Tests.PlaybackTestPlans;

namespace Jularr.Tests;

/// <summary>
/// The ephemeral runtime telemetry of a playback session: what a player may report, which report wins, when it counts as
/// activity, and how a replaced session's evidence reaches the next plan. All of it runs on a clock the test moves.
/// </summary>
[TestClass]
public sealed class PlaybackTelemetryTests
{
    private const string Reader = "telemetry-reader";
    private static readonly DateTimeOffset s_start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static PlaybackTelemetryUpdate Update(
        long sequence = 1,
        PlaybackClientState state = PlaybackClientState.Playing,
        double buffer = 12,
        int? throughput = 24_000,
        int stalls = 0,
        long stallMs = 0,
        double position = 0) =>
        new(sequence, state, buffer, throughput, stalls, stallMs, position);

    private static PlaybackStreamSession NewSession(PlaybackStreamSessionStore store) =>
        store.Create(
            Reader,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/media/episode.mkv",
            1400,
            Transcode(Video()),
            new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, "web"));

    private static PlaybackTelemetry Report(PlaybackTelemetryUpdate update, DateTimeOffset at)
    {
        Assert.IsTrue(PlaybackTelemetryRules.TryValidate(update, at, out var report));
        return report;
    }

    [TestMethod]
    public void AReportCarriesOnlyValuesAPlayerCanObserve()
    {
        Assert.IsTrue(PlaybackTelemetryRules.TryValidate(Update(throughput: null), s_start, out var valid));
        Assert.IsNull(valid.ThroughputKbps, "A player that could not measure a rate says so.");

        var rejected = new[]
        {
            Update(buffer: double.NaN),
            Update(buffer: double.PositiveInfinity),
            Update(buffer: -0.1),
            Update(buffer: PlaybackTelemetryRules.MaxBufferAheadSeconds + 1),
            Update(sequence: -1),
            Update(throughput: -1),
            Update(throughput: PlaybackTelemetryRules.MaxThroughputKbps + 1),
            Update(stalls: -1),
            Update(stalls: PlaybackTelemetryRules.MaxStallCount + 1),
            Update(stallMs: -1),
            Update(stallMs: PlaybackTelemetryRules.MaxStallTotalMs + 1),
            Update(position: -1),
            Update(position: double.NaN),
            Update(position: PlaybackTelemetryRules.MaxPositionSeconds + 1),
            Update() with { PositionSeconds = null },
            Update(state: (PlaybackClientState)99),
            Update() with { State = null },
            Update() with { Sequence = null },
            Update() with { BufferAheadSeconds = null },
            Update() with { StallCount = null },
            Update() with { StallTotalMs = null }
        };
        foreach (var update in rejected)
        {
            Assert.IsFalse(PlaybackTelemetryRules.TryValidate(update, s_start, out _), $"{update} must be rejected.");
        }
    }

    [TestMethod]
    public void TheHighestSequenceWinsAndARepeatedOrOlderReportChangesNothing()
    {
        var telemetry = new PlaybackSessionTelemetry();

        Assert.AreNotEqual(PlaybackTelemetryOutcome.Ignored, telemetry.Apply(Report(Update(sequence: 5, buffer: 20), s_start)));
        Assert.AreEqual(PlaybackTelemetryOutcome.Ignored, telemetry.Apply(Report(Update(sequence: 5, buffer: 99), s_start.AddSeconds(5))), "The same sequence again is a retry.");
        Assert.AreEqual(PlaybackTelemetryOutcome.Ignored, telemetry.Apply(Report(Update(sequence: 4, buffer: 1), s_start.AddSeconds(6))), "An older report arrived late.");
        Assert.AreEqual(20, telemetry.Latest!.BufferAheadSeconds);
        Assert.AreNotEqual(PlaybackTelemetryOutcome.Ignored, telemetry.Apply(Report(Update(sequence: 6, buffer: 3), s_start.AddSeconds(10))));
        Assert.AreEqual(3, telemetry.Latest!.BufferAheadSeconds);
    }

    [TestMethod]
    public void OnlyTheStallsOfTheLastMinuteCountAsRecentAndAStaleBufferIsNotReported()
    {
        var telemetry = new PlaybackSessionTelemetry();
        Assert.IsNull(telemetry.Evidence(s_start), "A player that never reported leaves no evidence; the plan keeps the request's hints.");

        telemetry.Apply(Report(Update(sequence: 1, buffer: 30), s_start));
        telemetry.Apply(Report(Update(sequence: 2, buffer: 2, stalls: 1, stallMs: 1500), s_start.AddSeconds(10)));
        telemetry.Apply(Report(Update(sequence: 3, buffer: 1, stalls: 2, stallMs: 4200), s_start.AddSeconds(20)));

        var soon = telemetry.Evidence(s_start.AddSeconds(25))!;
        Assert.AreEqual(2, soon.RecentStalls);
        Assert.AreEqual(1, soon.BufferSeconds);

        var later = telemetry.Evidence(s_start.AddSeconds(75))!;
        Assert.AreEqual(1, later.RecentStalls, "The first stall is older than a minute; the second is not.");
        Assert.IsNull(later.BufferSeconds, "A buffer reported 55 seconds ago no longer describes the player.");

        Assert.AreEqual(0, telemetry.Evidence(s_start.AddSeconds(100))!.RecentStalls);
    }

    [TestMethod]
    public void AFloodOfReportsStillGivesTheRightEvidence()
    {
        var telemetry = new PlaybackSessionTelemetry();
        for (var step = 1; step <= 500; step++)
        {
            telemetry.Apply(Report(Update(sequence: step, stalls: step), s_start));
        }

        Assert.AreEqual(500, telemetry.Evidence(s_start)!.RecentStalls);
        Assert.AreEqual(0, telemetry.Evidence(s_start.AddSeconds(100))!.RecentStalls);
    }

    [TestMethod]
    public void OnlyAPlayerThatReallyPlaysOrWaitsKeepsTheSessionAlive()
    {
        var clock = new ManualTimeProvider(s_start);
        var store = new PlaybackStreamSessionStore(clock);
        var session = NewSession(store);
        var created = session.LastSeenUtc;
        bool SendReport(long sequence, PlaybackClientState state, double position, long stallMs = 0, int stalls = 0) =>
            store.ReportTelemetry(session.Id, Reader, Report(Update(sequence: sequence, state: state, position: position, stallMs: stallMs, stalls: stalls), clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsTrue(SendReport(1, PlaybackClientState.Paused, 100));
        Assert.AreEqual(created, session.LastSeenUtc, "A paused player is not using the session.");

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue(SendReport(2, PlaybackClientState.Playing, 100));
        Assert.AreEqual(created, session.LastSeenUtc, "A player that says it plays but has not moved yet is not shown to play.");

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue(SendReport(3, PlaybackClientState.Playing, 105));
        Assert.AreEqual(clock.GetUtcNow(), session.LastSeenUtc, "A position that advanced at playback speed is use.");

        var touched = session.LastSeenUtc;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue(SendReport(3, PlaybackClientState.Playing, 110));
        Assert.AreEqual(touched, session.LastSeenUtc, "Replaying an old report is not activity.");

        Assert.IsTrue(SendReport(4, PlaybackClientState.Playing, 105));
        Assert.AreEqual(touched, session.LastSeenUtc, "A player whose position stands still is not playing: a loop of reports keeps nothing alive.");

        touched = session.LastSeenUtc;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue(SendReport(5, PlaybackClientState.Playing, 5000));
        Assert.AreEqual(touched, session.LastSeenUtc, "A jump faster than any playback speed is a seek or a made-up position, not playback.");

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue(SendReport(6, PlaybackClientState.Buffering, 5000, stallMs: 4800, stalls: 1));
        Assert.AreEqual(clock.GetUtcNow(), session.LastSeenUtc, "Waiting for media counts while the stall time grows like the clock.");

        touched = session.LastSeenUtc;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue(SendReport(7, PlaybackClientState.Buffering, 5000, stallMs: 900_000, stalls: 1));
        Assert.AreEqual(touched, session.LastSeenUtc, "Stall time that grows faster than the clock is made up.");
    }

    [TestMethod]
    public void AClientWaitingOnADeadStreamKeepsTheSessionForTwoMinutesAtMost()
    {
        var clock = new ManualTimeProvider(s_start);
        var store = new PlaybackStreamSessionStore(clock);
        var session = NewSession(store);
        var stallMs = 0L;
        store.ReportTelemetry(session.Id, Reader, Report(Update(sequence: 1, position: 300), clock.GetUtcNow()));

        // Honest-looking buffering reports (stall time grows like the clock) with the position standing still.
        for (var step = 2; step < 400; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            stallMs += 5000;
            store.ReportTelemetry(session.Id, Reader, Report(Update(sequence: step, state: PlaybackClientState.Buffering, position: 300, stalls: 1, stallMs: stallMs), clock.GetUtcNow()));
        }

        Assert.IsNull(store.Peek(session.Id, Reader), "Without a position advance the waiting extended the session for two minutes and then no more.");
        Assert.IsTrue(session.LastSeenUtc <= s_start + PlaybackSessionTelemetry.MaxBufferingExtension + TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void ABackwardSeekReAnchorsTheProgressButAnAlternatingClientCannotKeepTheSessionAlive()
    {
        var clock = new ManualTimeProvider(s_start);
        var store = new PlaybackStreamSessionStore(clock);
        var session = NewSession(store);
        long sequence = 0;
        bool Extends(double position)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            var before = session.LastSeenUtc;
            store.ReportTelemetry(session.Id, Reader, Report(Update(sequence: ++sequence, position: position), clock.GetUtcNow()));
            return session.LastSeenUtc > before;
        }

        Extends(1000);
        Assert.IsTrue(Extends(1005));
        Assert.IsFalse(Extends(200), "The seek report itself is no playback.");
        Assert.IsTrue(Extends(205), "A viewer who rewound plays on from there: progress beyond the new mark counts.");
        Assert.IsFalse(Extends(5000), "A forward seek is not playback either.");
        Assert.IsTrue(Extends(5005), "The playback after it is.");

        // Alternating positions: every forward step looks fine on its own, but only a few backward seeks re-anchor the mark.
        var extensions = 0;
        for (var cycle = 0; cycle < 40; cycle++)
        {
            extensions += Extends(100) ? 1 : 0;
            extensions += Extends(105) ? 1 : 0;
        }

        Assert.IsTrue(extensions <= 3, $"Alternating forward and backward positions extended the session {extensions} times; at most the three rewinds count.");
    }

    [TestMethod]
    public void ASpamLoopOfPlayingReportsLetsTheSessionExpire()
    {
        var clock = new ManualTimeProvider(s_start);
        var store = new PlaybackStreamSessionStore(clock);
        var session = NewSession(store);
        store.ReportTelemetry(session.Id, Reader, Report(Update(sequence: 1, position: 50), clock.GetUtcNow()));

        for (var step = 2; step < 400; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            store.ReportTelemetry(session.Id, Reader, Report(Update(sequence: step, buffer: step % 30, position: 50), clock.GetUtcNow()));
        }

        Assert.IsNull(store.Peek(session.Id, Reader), "Two thousand seconds of reports without progress did not keep the session past its idle lifetime.");
    }

    [TestMethod]
    public void AnotherProfilesOrAnExpiredSessionTakesNoReport()
    {
        var clock = new ManualTimeProvider(s_start);
        var store = new PlaybackStreamSessionStore(clock);
        var session = NewSession(store);
        var report = Report(Update(), s_start);

        Assert.IsFalse(store.ReportTelemetry(session.Id, "someone-else", report));
        Assert.IsFalse(store.ReportTelemetry(Guid.NewGuid(), Reader, report));
        Assert.IsNull(session.Telemetry.Latest);

        clock.Advance(PlaybackStreamSessionStore.IdleLifetime + TimeSpan.FromSeconds(1));
        Assert.IsFalse(store.ReportTelemetry(session.Id, Reader, report), "Telemetry cannot revive an idle-expired session.");
    }

    [TestMethod]
    public async Task TheStallsAPlayerReportedDecideTheStepDownOfTheNextPlanNotTheRequestHint()
    {
        await using var fixture = await MediaInventoryFixture.CreateAsync();
        var media = await fixture.AddMediaAsync("episode.mkv", new byte[4096]);
        fixture.Runner.Returns(media.Path, MediaProbeFixtures.HevcTenBitHdrMultiAudio);
        var clock = new ManualTimeProvider(s_start);
        var store = new PlaybackStreamSessionStore(clock);
        var service = new PlaybackPlanService(fixture.Db, fixture.Inventory, store, PlaybackServerTestKit.Create().Capabilities);
        var remote = new PlaybackPlanInput(null, "web", "Mozilla/5.0 Chrome/140.0.0.0 Safari/537.36", IPAddress.Parse("203.0.113.9"), Network: new PlaybackNetworkReport(ThroughputKbps: 20_000));

        var first = await service.PlanAsync(media.EpisodeId!.Value, Reader, remote, CancellationToken.None);
        Assert.AreEqual(PlaybackLimitSource.Network, first!.Plan.Quality.LimitSource, "Automatic follows the measured throughput.");
        var firstSession = first.Session!;

        // Without telemetry the request's own hint is all there is.
        var stallingHint = new PlaybackNetworkReport(ThroughputKbps: 20_000, RecentStalls: 3, BufferSeconds: 1);
        var hinted = await service.PlanAsync(media.EpisodeId!.Value, Reader, remote with { ReplacesSessionId = firstSession.Id, Network = stallingHint }, CancellationToken.None);
        Assert.AreEqual(PlaybackLimitSource.Stalls, hinted!.Plan.Quality.LimitSource);

        var secondSession = store.Create(Reader, firstSession.Target, Guid.NewGuid(), media.Path, 1400, first.Plan, firstSession.Selections);
        store.ReportTelemetry(secondSession.Id, Reader, Report(Update(sequence: 1, buffer: 1.5, stalls: 0), clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(5));
        store.ReportTelemetry(secondSession.Id, Reader, Report(Update(sequence: 2, buffer: 0.5, stalls: 2, stallMs: 3100), clock.GetUtcNow()));

        var stalled = await service.PlanAsync(media.EpisodeId!.Value, Reader, remote with { ReplacesSessionId = secondSession.Id }, CancellationToken.None);
        Assert.AreEqual(PlaybackLimitSource.Stalls, stalled!.Plan.Quality.LimitSource, "Two reported stalls step the quality down.");
        Assert.IsTrue(stalled.Plan.Quality.LimitKbps < first.Plan.Quality.LimitKbps);

        var thirdSession = store.Create(Reader, firstSession.Target, Guid.NewGuid(), media.Path, 1400, first.Plan, firstSession.Selections);
        store.ReportTelemetry(thirdSession.Id, Reader, Report(Update(sequence: 1, buffer: 40, stalls: 0), clock.GetUtcNow()));
        var calm = await service.PlanAsync(media.EpisodeId!.Value, Reader, remote with { ReplacesSessionId = thirdSession.Id, Network = new PlaybackNetworkReport(ThroughputKbps: 20_000, RecentStalls: 9) }, CancellationToken.None);
        Assert.AreEqual(PlaybackLimitSource.Network, calm!.Plan.Quality.LimitSource, "What the player reported wins over a stale hint of the request.");

        // The evidence of another title's session says nothing about this one: the request's own hint decides.
        var otherTitle = store.Create(Reader, new PlaybackVideoTarget(Guid.NewGuid(), null), Guid.NewGuid(), media.Path, 1400, first.Plan, firstSession.Selections);
        store.ReportTelemetry(otherTitle.Id, Reader, Report(Update(sequence: 1, buffer: 40, stalls: 0), clock.GetUtcNow()));
        var foreign = await service.PlanAsync(media.EpisodeId!.Value, Reader, remote with { ReplacesSessionId = otherTitle.Id, Network = new PlaybackNetworkReport(ThroughputKbps: 20_000, RecentStalls: 4) }, CancellationToken.None);
        Assert.AreEqual(PlaybackLimitSource.Stalls, foreign!.Plan.Quality.LimitSource, "The hint's stalls are used because the replaced session belonged to another title.");

        // A report that is too old leaves no evidence either: a calm report from minutes ago must not hide the request's stalls.
        var stale = store.Create(Reader, firstSession.Target, Guid.NewGuid(), media.Path, 1400, first.Plan, firstSession.Selections);
        store.ReportTelemetry(stale.Id, Reader, Report(Update(sequence: 1, buffer: 40, stalls: 0), clock.GetUtcNow()));
        clock.Advance(PlaybackSessionTelemetry.EvidenceFreshFor + TimeSpan.FromSeconds(1));
        var old = await service.PlanAsync(media.EpisodeId!.Value, Reader, remote with { ReplacesSessionId = stale.Id, Network = new PlaybackNetworkReport(ThroughputKbps: 20_000, RecentStalls: 4) }, CancellationToken.None);
        Assert.AreEqual(PlaybackLimitSource.Stalls, old!.Plan.Quality.LimitSource);
    }
}
