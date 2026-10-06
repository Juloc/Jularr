using System.Text.Json;
using Jint;

namespace Jularr.Tests;

/// <summary>
/// What the player observes about its buffer and reports to the server, run under Jint with a clock the test passes in:
/// buffered ranges, the stall rules (startup and seeks are not stalls), the smoothed receive rate, the startup wait and the
/// telemetry payload and cadence.
/// </summary>
[TestClass]
public sealed class PlayerBufferingTests
{
    private const string Ranges = "const ranges = (...pairs) => ({ length: pairs.length, start: (i) => pairs[i][0], end: (i) => pairs[i][1] });";

    [TestMethod]
    public void BufferedRangesAreReadInAbsoluteTimeAndTheBufferAheadIsTheRangeAroundThePlayhead()
    {
        Assert.AreEqual(
            "[[100,130],[400,410]]",
            Json($"{Ranges} return buffering.rangesOf(ranges([0, 30], [300, 310]), 100);"),
            "A live stream started at 100 s reports ranges relative to that start.");
        Assert.AreEqual(12.0, Number($"{Ranges} return buffering.bufferAhead(buffering.rangesOf(ranges([0, 42]), 0), 30);"));
        Assert.AreEqual(5.0, Number($"{Ranges} return buffering.bufferAhead(buffering.rangesOf(ranges([0, 10], [50, 90]), 0), 5);"));
        Assert.AreEqual(0.0, Number($"{Ranges} return buffering.bufferAhead(buffering.rangesOf(ranges([0, 10]), 0), 20);"), "Outside every range nothing is buffered ahead.");
        Assert.AreEqual(10.0, Number($"{Ranges} return buffering.bufferAhead(buffering.rangesOf(ranges([0, 40]), 0), 30);"), "The playhead sits inside the range.");
        Assert.AreEqual("[]", Json("return buffering.rangesOf({ length: 1, start: () => NaN, end: () => 5 }, 0);"), "A range that is not finite is dropped.");
        Assert.AreEqual("null", Json("return buffering.bufferedEnd([[0, 10]], 50);"));
    }

    [TestMethod]
    public void TheTimelineGradientLightsExactlyTheLoadedRanges()
    {
        Assert.AreEqual(
            "linear-gradient(to right, transparent 0.000% 10.000%, var(--player-buffered, rgba(255,255,255,.6)) 10.000% 30.000%, transparent 30.000% 50.000%, var(--player-buffered, rgba(255,255,255,.6)) 50.000% 60.000%, transparent 60.000% 100%)",
            Text("return buffering.bufferedGradient([[10, 30], [50, 60]], 100);"));
        Assert.AreEqual("linear-gradient(transparent, transparent)", Text("return buffering.bufferedGradient([], 100);"));
        Assert.AreEqual("linear-gradient(transparent, transparent)", Text("return buffering.bufferedGradient([[0, 10]], 0);"), "Without a known duration there is no timeline to light.");
        StringAssert.Contains(Text("return buffering.bufferedGradient([[90, 500]], 100);"), "var(--player-buffered, rgba(255,255,255,.6)) 90.000% 100.000%", "A range never paints beyond the end.");
        Assert.AreEqual(
            Text("return buffering.bufferedGradient([[10, 30], [50, 60]], 100);"),
            Text("return buffering.bufferedGradient([[50, 60], [10, 30]], 100);"),
            "The order of the ranges does not matter.");
    }

    [TestMethod]
    public void OnlyWaitingAfterPlaybackStartedIsAStallNotTheStartNorASeekNorPausedTime()
    {
        // Startup: the element waits for its first data before it ever played.
        Assert.AreEqual(
            "{\"count\":0,\"totalMs\":0}",
            Json("const t = buffering.createStallTracker(); t.waiting(0, false); return t.snapshot(5000);"));

        // A real stall: playing, then waiting for 1.5 s.
        Assert.AreEqual(
            "{\"count\":1,\"totalMs\":1500}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(10000, false); t.playing(11500); return t.snapshot(20000);"));

        // A seek: seeking, waiting and playing again are one navigation, not a stall.
        Assert.AreEqual(
            "{\"count\":0,\"totalMs\":0}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.seeking(9000); t.waiting(10000, false); t.playing(12000); return t.snapshot(20000);"));

        // The seek only suppresses until playback resumed: a later stall counts again.
        Assert.AreEqual(
            "{\"count\":1,\"totalMs\":800}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.seeking(500); t.waiting(1000, false); t.playing(2000); t.waiting(5000, false); t.playing(5800); return t.snapshot(9000);"));

        // A new source (a restarted live delivery) has its own startup; the count of the session stays.
        Assert.AreEqual(
            "{\"count\":1,\"totalMs\":1000}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); t.playing(2000); t.sourceChanged(2500); t.waiting(3000, false); t.playing(9000); return t.snapshot(9500);"));

        // Waiting while paused is not a stall.
        Assert.AreEqual(
            "{\"count\":0,\"totalMs\":0}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, true); return t.snapshot(9000);"));
    }

    [TestMethod]
    public void AStallEndsWhenThePlayerPausesAndATinyBlipIsNotCounted()
    {
        Assert.AreEqual(
            "{\"count\":1,\"totalMs\":2000}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); t.paused(3000); return t.snapshot(60000);"),
            "Time paused after the stall began is not stall time.");
        Assert.AreEqual(
            "{\"count\":0,\"totalMs\":0}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); t.playing(1100); return t.snapshot(5000);"),
            "A waiting event that clears within a quarter second is a blip.");
        Assert.AreEqual(250.0, Number("return buffering.minStallMs;"));
    }

    [TestMethod]
    public void AStallThatIsStillGoingOnIsReportedWithItsElapsedTimeAndTheStateIsBuffering()
    {
        Assert.AreEqual(
            "{\"count\":1,\"totalMs\":4000}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); return t.snapshot(5000);"));
        Assert.IsTrue(Evaluate("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); return t.isStalled();").AsBoolean());
        Assert.IsFalse(Evaluate("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); t.playing(2000); return t.isStalled();").AsBoolean());
    }

    [TestMethod]
    public void ANewSessionStartsItsStallCountOverButANewSourceDoesNot()
    {
        Assert.AreEqual(
            "{\"count\":0,\"totalMs\":0}",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); t.playing(3000); t.resetSession(); return t.snapshot(9000);"));
    }

    [TestMethod]
    public void TheReceiveRateIsTheBufferGrowthTimesTheBitrateSmoothedOverFifteenSeconds()
    {
        // The buffer grows two media seconds per second of a 8 Mbps stream: 16 Mbps arrive.
        const string steady = """
            const e = buffering.createThroughputEstimator();
            for (let second = 0; second <= 5; second++) e.observe({ nowMs: second * 1000, bufferedEndSeconds: 10 + second * 2, bitrateKbps: 8000, idle: false });
            return e.value(5000);
            """;
        Assert.AreEqual(16000.0, Number(steady));

        // The rate drops to 8 Mbps: after one window (15 s) the estimate has moved 63 % of the way, never instantly.
        const string drop = """
            const e = buffering.createThroughputEstimator();
            e.observe({ nowMs: 0, bufferedEndSeconds: 0, bitrateKbps: 8000, idle: false });
            e.observe({ nowMs: 1000, bufferedEndSeconds: 2, bitrateKbps: 8000, idle: false });
            for (let second = 2; second <= 16; second++) e.observe({ nowMs: second * 1000, bufferedEndSeconds: 2 + (second - 1), bitrateKbps: 8000, idle: false });
            return e.value(16000);
            """;
        Assert.AreEqual(8000 + 8000 * Math.Exp(-1), Number(drop), 1.0);
    }

    [TestMethod]
    public void ARateIsOnlyMeasuredWhileTheBrowserFetchesAndANoSampleIsNeverZero()
    {
        // The browser stopped fetching (buffer full): the idle seconds measure nothing.
        const string idle = """
            const e = buffering.createThroughputEstimator();
            e.observe({ nowMs: 0, bufferedEndSeconds: 10, bitrateKbps: 8000, idle: false });
            e.observe({ nowMs: 1000, bufferedEndSeconds: 12, bitrateKbps: 8000, idle: false });
            for (let second = 2; second <= 10; second++) e.observe({ nowMs: second * 1000, bufferedEndSeconds: 12, bitrateKbps: 8000, idle: true });
            return e.value(10000);
            """;
        Assert.AreEqual(16000.0, Number(idle));

        // Never sampled: no value, rather than a made-up one.
        Assert.AreEqual("null", Json(Observed(8000, (0, 10))));
        // A shrinking range is a flush or a seek, and an unknown bitrate leaves nothing to compute.
        Assert.AreEqual("null", Json(Observed(8000, (0, 50), (1000, 5))));
        Assert.AreEqual("null", Json(Observed(null, (0, 0), (1000, 3))));
        // After a reset (a seek) the first observation is only a new baseline.
        Assert.AreEqual("null", Json("""
            const e = buffering.createThroughputEstimator();
            e.observe({ nowMs: 0, bufferedEndSeconds: 0, bitrateKbps: 8000, idle: false });
            e.reset();
            e.observe({ nowMs: 1000, bufferedEndSeconds: 90, bitrateKbps: 8000, idle: false });
            return e.value(1000);
            """));
    }

    [TestMethod]
    public void ARateNobodyMeasuredForAMinuteIsNoLongerReported()
    {
        const string script = """
            const e = buffering.createThroughputEstimator();
            e.observe({ nowMs: 0, bufferedEndSeconds: 0, bitrateKbps: 8000, idle: false });
            e.observe({ nowMs: 1000, bufferedEndSeconds: 2, bitrateKbps: 8000, idle: false });
            return [e.value(1000), e.value(61000), e.value(61001)];
            """;

        Assert.AreEqual("[16000,16000,null]", Json(script));
    }

    [TestMethod]
    public void TheTelemetryPayloadHasTheContractShapeAndRoundsToReadableValues()
    {
        const string script = """
            return buffering.buildReport({ sequence: 7, state: 'buffering', bufferAheadSeconds: 12.3456, throughputKbps: 23999.6, stalls: { count: 2, totalMs: 3100.4 }, positionSeconds: 61.52 });
            """;

        Assert.AreEqual(
            "{\"sequence\":7,\"state\":\"buffering\",\"bufferAheadSeconds\":12.3,\"throughputKbps\":24000,\"stallCount\":2,\"stallTotalMs\":3100,\"positionSeconds\":61.5}",
            Json(script));
        Assert.AreEqual(
            "{\"sequence\":1,\"state\":\"playing\",\"bufferAheadSeconds\":0,\"throughputKbps\":null,\"stallCount\":0,\"stallTotalMs\":0,\"positionSeconds\":0}",
            Json("return buffering.buildReport({ sequence: 1, state: 'playing', bufferAheadSeconds: -4, throughputKbps: null, stalls: { count: 0, totalMs: 0 }, positionSeconds: 0 });"));
        Assert.AreEqual(
            "{\"sequence\":1,\"state\":\"playing\",\"bufferAheadSeconds\":3600,\"throughputKbps\":10000000,\"stallCount\":100000,\"stallTotalMs\":0,\"positionSeconds\":604800}",
            Json("return buffering.buildReport({ sequence: 1, state: 'playing', bufferAheadSeconds: 99999, throughputKbps: 99999999, stalls: { count: 999999, totalMs: 0 }, positionSeconds: 99999999 });"),
            "A report never claims more than the server accepts.");
    }

    [TestMethod]
    public void ReportsGoOutEveryFiveSecondsAndAPausedPlayerReportsOnlyOnce()
    {
        Assert.AreEqual(5000.0, Number("return buffering.reportIntervalMs;"));
        Assert.AreEqual(15000.0, Number("return buffering.throughputWindowMs;"));
        Assert.IsTrue(Evaluate("return buffering.shouldReport('playing', 'playing');").AsBoolean());
        Assert.IsTrue(Evaluate("return buffering.shouldReport('buffering', null);").AsBoolean());
        Assert.IsTrue(Evaluate("return buffering.shouldReport('paused', 'playing');").AsBoolean(), "The pause itself is reported once.");
        Assert.IsFalse(Evaluate("return buffering.shouldReport('paused', 'paused');").AsBoolean(), "Then the player stays silent until it plays again.");
        Assert.IsTrue(Evaluate("return buffering.shouldReport('playing', 'paused');").AsBoolean());
    }

    [TestMethod]
    public void ThePlayerWiresTheBufferingRulesIntoItsEventsTimersAndPlanRequests()
    {
        var root = PlayerControlsTests.RepositoryRoot();
        var player = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));
        var chrome = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "player-chrome.js"));
        var page = EpisodePlayerSource.Read(root);

        Assert.IsTrue(page.IndexOf("player-buffering.js", StringComparison.Ordinal) is > 0 and var rule && rule < page.IndexOf("episode-player.js", StringComparison.Ordinal), "The rules load before the player.");
        StringAssert.Contains(page, "data-stream-session-telemetry-url-template=");
        foreach (var wiring in new[]
        {
            "video.addEventListener(\"waiting\"", "video.addEventListener(\"playing\"", "video.addEventListener(\"seeking\"",
            "window.setInterval(() => void sendTelemetry(), buffering.reportIntervalMs)", "buffering.buildReport(", "buffering.bufferedGradient("
        })
        {
            StringAssert.Contains(player, wiring);
        }

        var flush = player.IndexOf("await sendTelemetry(true);", StringComparison.Ordinal);
        Assert.IsTrue(flush > 0 && flush < player.IndexOf("await fetch(planUrl", StringComparison.Ordinal), "The replaced session's last evidence reaches the server before the next plan is requested.");
        Assert.IsTrue(player.Contains("stalls.seeked(video.readyState)", StringComparison.Ordinal), "A seek inside buffered media ends without a playing event.");
    }

    [TestMethod]
    public void TheSharedTimelineHasABufferedLayerBetweenTheAccentFillAndTheUnloadedRest()
    {
        var css = File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "css", "player.css"));

        var rule = System.Text.RegularExpressions.Regex.Match(css, @"\n\.player-timeline\s*\{(?<body>[^}]*)\}").Groups["body"].Value;

        var played = rule.IndexOf("var(--accent)", StringComparison.Ordinal);
        var buffered = rule.IndexOf("var(--buffered-ranges", StringComparison.Ordinal);
        var rest = rule.IndexOf("var(--player-timeline-rest)", StringComparison.Ordinal);
        Assert.IsTrue(played >= 0 && buffered > played && rest > buffered, "Played fill on top, buffered ranges behind it, the unloaded rest at the bottom.");
        Assert.IsFalse(
            System.Text.RegularExpressions.Regex.IsMatch(css, @"\.player-volume-slider[^{]*\{[^}]*--buffered-ranges"),
            "The volume slider shares the look of the timeline but has no buffer.");
    }

    // A throughput estimator fed with (time ms, buffered end seconds) observations at the given bitrate; the script returns its value at the last time.
    private static string Observed(int? bitrateKbps, params (int NowMs, int EndSeconds)[] observations)
    {
        var bitrate = bitrateKbps?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        var calls = observations.Select(x => $"e.observe({{ nowMs: {x.NowMs}, bufferedEndSeconds: {x.EndSeconds}, bitrateKbps: {bitrate}, idle: false }}); ");
        return "const e = buffering.createThroughputEstimator(); " + string.Concat(calls) + $"return e.value({observations[^1].NowMs});";
    }

    private static string Json(string script) => Evaluate($"return JSON.stringify((() => {{ {script} }})());").AsString();

    private static string Text(string script) => Evaluate(script).AsString();

    private static double Number(string script) => Evaluate(script).AsNumber();

    private static Jint.Native.JsValue Evaluate(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "player-buffering.js")));
        return engine.Evaluate($"(() => {{ const buffering = window.JularrPlayerBuffering; {script} }})()");
    }
}
