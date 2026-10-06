using Jint;

namespace Jularr.Tests;

/// <summary>
/// The loading indicator and the stall counts of the player, run under Jint against the real player-buffering.js: the indicator only
/// shows what lasted a moment, stall counts never go back and a seek inside buffered media does not swallow the next genuine stall.
/// </summary>
[TestClass]
public sealed class PlayerWaitingTests
{
    [TestMethod]
    public void TheLoadingIndicatorOnlyAppearsAfterTheDelayAndLeavesAtOnce()
    {
        const string setup = """
            const timers = { pending: null, setTimeout(fn) { timers.pending = fn; return 1; }, clearTimeout() { timers.pending = null; } };
            const shown = [];
            const indicator = buffering.createDelayedIndicator({ timers, delayMs: buffering.waitingIndicatorDelayMs, apply: (value) => shown.push(value) });
            """;

        Assert.AreEqual(
            "[[],[true],[true,false]]",
            Json(setup + "indicator.set(true); const a = shown.slice(); timers.pending(); const b = shown.slice(); indicator.set(false); return [a, b, shown];"),
            "Nothing shows before the delay; the end of the wait hides it immediately.");
        Assert.AreEqual(
            "[]",
            Json(setup + "indicator.set(true); indicator.set(false); return shown;"),
            "A wait shorter than the delay never flashes the indicator.");
        Assert.AreEqual(300.0, Evaluate("return buffering.waitingIndicatorDelayMs;").AsNumber());
    }

    [TestMethod]
    public void StallCountsNeverGoBackAndASeekInsideBufferedMediaDoesNotSwallowTheNextStall()
    {
        // A stall that already lasted a while is counted when a seek ends it: a report may have carried it.
        Assert.AreEqual(
            "[1,1,true]",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); const a = t.snapshot(1500); t.seeking(2000); const b = t.snapshot(2500); return [a.count, b.count, b.totalMs >= a.totalMs];"));
        Assert.AreEqual(
            "[1,1,true]",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); const a = t.snapshot(1500); t.sourceChanged(2000); const b = t.snapshot(2500); return [a.count, b.count, b.totalMs >= a.totalMs];"));
        // A blip a seek cuts short is no stall, and was never counted.
        Assert.AreEqual("0", Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); t.seeking(1100); return t.snapshot(2000).count;"));

        // A seek inside buffered media ends with seeked and no new playing event: with data again, the next stall counts.
        Assert.AreEqual(
            "1",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.seeking(1000); t.seeked(4); t.waiting(5000, false); t.playing(6000); return t.snapshot(9000).count;"));
        Assert.AreEqual(
            "0",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.seeking(1000); t.seeked(1); t.waiting(5000, false); t.playing(6000); return t.snapshot(9000).count;"),
            "While the seek still has no data the waiting belongs to the seek.");
    }

    [TestMethod]
    public void ThePlayerNeverPausesItsOwnElementToWaitForABuffer()
    {
        var player = File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));

        // A paused element loads only a couple of seconds (measured in Edge: a 3 s wait with a fast server loaded nothing more and
        // delayed the start by the whole wait), so a hold is a pure delay and the player keeps no startup wait.
        Assert.IsFalse(player.Contains("startupHold", StringComparison.Ordinal));
        StringAssert.Contains(player, "buffering.isWaitingForMedia(", "The indicator follows the one tested predicate.");
    }

    [TestMethod]
    [DataRow("{ readyState: 0, paused: false }", true, "Play was asked for and no data has arrived: waiting.")]
    [DataRow("{ readyState: 2, paused: false }", true, "A stall: not enough data for the next frame.")]
    [DataRow("{ readyState: 3, paused: false }", false, "Enough data to play: not waiting.")]
    [DataRow("{ readyState: 0, paused: true }", false, "A paused viewer waits for nothing.")]
    [DataRow("{ readyState: 0, paused: false, ended: true }", false, "Ended.")]
    [DataRow("{ readyState: 0, paused: false, failed: true }", false, "A failed element never gets data: no spinner over the failure banner.")]
    [DataRow("{ readyState: 0, paused: false, hidden: true }", false, "A hidden video (placeholder, storage state) shows no spinner.")]
    [DataRow("{ readyState: 0, paused: false, handedOver: true }", false, "The system player or picture-in-picture owns the picture.")]
    public void TheLoadingIndicatorShowsOnlyWhilePlaybackIsGenuinelyWaiting(string input, bool expected, string reason)
    {
        var script = "return buffering.isWaitingForMedia(Object.assign({ hidden: false, paused: false, ended: false, failed: false, handedOver: false, readyState: 0 }, " + input + "));";

        Assert.AreEqual(expected, Evaluate(script).AsBoolean(), reason);
    }

    [TestMethod]
    public void AFailedStreamEndsItsStallInsteadOfKeepingItRunning()
    {
        // The error settles the stall like a pause: the count includes it and the stall time stops growing with the clock.
        Assert.AreEqual(
            "[{\"count\":1,\"totalMs\":1500},{\"count\":1,\"totalMs\":1500}]",
            Json("const t = buffering.createStallTracker(); t.playing(0); t.waiting(1000, false); t.paused(2500); return [t.snapshot(3000), t.snapshot(600000)];"));
    }

    private static string Json(string script) => NewEngine().Evaluate($"JSON.stringify((() => {{ {script} }})())").AsString();

    private static Jint.Native.JsValue Evaluate(string script) => NewEngine().Evaluate($"(() => {{ {script} }})()");

    private static Engine NewEngine()
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "player-buffering.js")));
        engine.Execute("const buffering = window.JularrPlayerBuffering;");
        return engine;
    }
}
