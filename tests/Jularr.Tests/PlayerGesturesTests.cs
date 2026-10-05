using System.Text.RegularExpressions;
using Jint;

namespace Jularr.Tests;

/// <summary>
/// The video-surface tap rules of the web player (player-gestures.js): a single tap toggles the
/// controls only after the double-tap window, double taps on the sides seek and add up, and the
/// page wires taps (not clicks) to that decision.
/// </summary>
[TestClass]
public sealed class PlayerGesturesTests
{
    [TestMethod]
    public void SingleTapTogglesTheControlsOnlyAfterTheDoubleTapWindow()
    {
        var result = Run("""
            const d = window.JularrPlayerGestures.createTapDecider({ delayMs: 280, stepSeconds: 10 });
            const out = [];
            out.push(d.tap(0.5, 0).action);
            out.push(d.settle(100).action);   // too early: a second tap may still come
            out.push(d.settle(300).action);   // window passed: show/hide
            out.push(d.settle(400).action);   // only once
            out.push(d.tap(0.1, 1000).action, d.settle(1300).action); // a lone side tap toggles too
            out.join(",");
            """);

        Assert.AreEqual("wait,none,toggleControls,none,wait,toggleControls", result);
    }

    [TestMethod]
    public void DoubleTapOnTheSidesSeeksAndFurtherTapsAccumulate()
    {
        var result = Run("""
            const d = window.JularrPlayerGestures.createTapDecider({ delayMs: 280, seriesMs: 700, stepSeconds: 10 });
            const out = [];
            const log = r => out.push(r.action === "seek" ? `${r.zone}:${r.total}` : r.action);
            log(d.tap(0.1, 0));
            log(d.tap(0.12, 200));   // double tap left: -10
            log(d.tap(0.1, 500));    // -20
            log(d.tap(0.1, 900));    // -30
            log(d.settle(1300));     // the series never toggles the controls
            log(d.tap(0.9, 3000));
            log(d.tap(0.9, 3150));   // double tap right: +10
            log(d.tap(0.9, 3400));   // +20
            log(d.tap(0.1, 3500));   // other side ends the series and starts a fresh tap
            log(d.settle(3800));
            out.join(",");
            """);

        Assert.AreEqual("wait,back:10,back:20,back:30,none,wait,forward:10,forward:20,wait,toggleControls", result);
    }

    [TestMethod]
    public void SlowTapsDifferentZonesAndTheMiddleNeverSeek()
    {
        var result = Run("""
            const d = window.JularrPlayerGestures.createTapDecider({ delayMs: 280, stepSeconds: 10 });
            const out = [];
            out.push(d.tap(0.1, 0).action, d.tap(0.1, 400).action);             // too slow: two single taps
            d.reset();
            out.push(d.tap(0.1, 1000).action, d.tap(0.9, 1100).action);         // left then right: no seek
            d.reset();
            out.push(d.tap(0.5, 2000).action, d.tap(0.5, 2150).action);         // middle double tap: full screen
            out.push(d.settle(2600).action);
            d.reset();
            out.push(d.tap(0.1, 5000).action, d.tap(0.1, 5100).action, d.tap(0.1, 5900).action); // series expired
            out.join(",");
            """);

        Assert.AreEqual("wait,wait,wait,wait,wait,doubleTapCenter,none,wait,seek,wait", result);
    }

    [TestMethod]
    public void TheVideoSurfaceUsesTheTapDecisionAndNeverPausesOnClick()
    {
        var chrome = Read("src", "Jularr.Web", "wwwroot", "js", "player-chrome.js");
        Assert.IsFalse(Regex.IsMatch(chrome, @"video\.addEventListener\(\s*""(click|dblclick)"""),
            "A click on the video must not toggle playback.");
        StringAssert.Contains(chrome, "JularrPlayerGestures?.createTapDecider");
        StringAssert.Contains(chrome, "stage.addEventListener(\"pointerup\"");
        StringAssert.Contains(chrome, "\"seekBack10\" : \"seekForward10\"");

        var page = EpisodePlayerSource.Read(PlayerControlsTests.RepositoryRoot());
        var gestures = page.IndexOf("~/js/player-gestures.js", StringComparison.Ordinal);
        Assert.IsTrue(gestures > 0 && gestures < page.IndexOf("~/js/player-chrome.js", StringComparison.Ordinal));
        StringAssert.Contains(page, "data-seek-feedback=\"back\"");
        StringAssert.Contains(page, "data-seek-feedback=\"forward\"");

        var css = Read("src", "Jularr.Web", "wwwroot", "css", "player.css");
        StringAssert.Contains(css, "touch-action: manipulation", "Double taps must not zoom the page.");
    }

    private static string Run(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "player-gestures.js"));
        return engine.Evaluate(script).AsString();
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([PlayerControlsTests.RepositoryRoot(), .. parts]));
}
