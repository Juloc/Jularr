using System.Text.RegularExpressions;
using Jint;

namespace Jularr.Tests;

[TestClass]
public sealed class PlayerGesturesTests
{
    [TestMethod]
    public void SingleTapTogglesTheControlsOnlyAfterTheDoubleTapWindow()
    {
        var result = Run("""
            const d = window.JularrPlayerGestures.createTapDecider({ delayMs: 280, backSeconds: 10, forwardSeconds: 30 });
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
            const d = window.JularrPlayerGestures.createTapDecider({ delayMs: 280, seriesMs: 700, backSeconds: 10, forwardSeconds: 30 });
            const out = [];
            const log = r => out.push(r.action === "seek" ? `${r.zone}:${r.total}` : r.action);
            log(d.tap(0.1, 0));
            log(d.tap(0.12, 200));   // double tap left: -10
            log(d.tap(0.1, 500));    // -20
            log(d.tap(0.1, 900));    // -30
            log(d.settle(1300));     // the series never toggles the controls
            log(d.tap(0.9, 3000));
            log(d.tap(0.9, 3150));   // double tap right: +30
            log(d.tap(0.9, 3400));   // +60
            log(d.tap(0.1, 3500));   // other side ends the series and starts a fresh tap
            log(d.settle(3800));
            out.join(",");
            """);

        Assert.AreEqual("wait,back:10,back:20,back:30,none,wait,forward:30,forward:60,wait,toggleControls", result);
    }

    [TestMethod]
    public void SlowTapsDifferentZonesAndTheMiddleNeverSeek()
    {
        var result = Run("""
            const d = window.JularrPlayerGestures.createTapDecider({ delayMs: 280, backSeconds: 10, forwardSeconds: 30 });
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
    public void MouseSurfaceClickTogglesPlaybackAndDoubleClickOpensFullscreen()
    {
        var result = Run("""
            const d = window.JularrPlayerGestures.createMouseClickDecider({ delayMs: 280, maxDistancePx: 18 });
            const out = [];
            out.push(d.click(200, 100, 0).action, d.settle(200).action, d.settle(300).action);
            out.push(d.click(200, 100, 1000).action, d.click(208, 105, 1150).action, d.settle(1500).action);
            out.push(d.click(0, 0, 2000).action, d.click(100, 0, 2100).action);
            out.push(d.settle(2250).action, d.settle(2400).action);
            out.join(",");
            """);

        Assert.AreEqual("wait,none,playPause,wait,fullscreen,none,wait,playPauseAndWait,none,playPause", result);
    }

    [TestMethod]
    public void MousePointerAndTouchUseDistinctSurfaceControls()
    {
        var chrome = Read("src", "Jularr.Web", "wwwroot", "js", "player-chrome.js");
        Assert.IsFalse(Regex.IsMatch(chrome, @"video\.addEventListener\(\s*""(click|dblclick)"""),
            "Video input must use one stage interaction owner.");
        StringAssert.Contains(chrome, "JularrPlayerGestures?.createTapDecider");
        StringAssert.Contains(chrome, "stage.addEventListener(\"pointerup\"");
        StringAssert.Contains(chrome, "start.pointerType !== event.pointerType");
        StringAssert.Contains(chrome, "if (event.pointerType === \"mouse\")");
        StringAssert.Contains(chrome, "handleMouseClick(event.clientX, event.clientY)");
        StringAssert.Contains(chrome, "mouseClicks.click(x, y, performance.now())");
        StringAssert.Contains(chrome, "handleTap(rect.width > 0");
        StringAssert.Contains(chrome, "select, input, textarea, [contenteditable], [role=textbox], .player-settings");
        StringAssert.Contains(chrome, "\"seekBack10\" : \"seekForward10\"");

        var page = EpisodePlayerSource.Read(PlayerControlsTests.RepositoryRoot());
        var gestures = page.IndexOf("~/js/player-gestures.js", StringComparison.Ordinal);
        Assert.IsTrue(gestures > 0 && gestures < page.IndexOf("~/js/player-chrome.js", StringComparison.Ordinal));
        StringAssert.Contains(page, "data-seek-feedback=\"back\"");
        StringAssert.Contains(page, "data-seek-feedback=\"forward\"");

        var css = Read("src", "Jularr.Web", "wwwroot", "css", "player.css");
        StringAssert.Contains(css, "touch-action: manipulation", "Double taps must not zoom the page.");
    }

    [TestMethod]
    public void ActualStagePointerEvents_KeepMouseFullscreenSeparateFromTouchSeeking()
    {
        var chrome = Read("src", "Jularr.Web", "wwwroot", "js", "player-chrome.js");
        var start = chrome.IndexOf("    const seekSeconds = design.seekSeconds(root);", StringComparison.Ordinal);
        var end = chrome.IndexOf("    // --- timeline fill", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);

        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "player-gestures.js"));
        engine.Execute("""
            var trace = [];
            var now = 0;
            var stageWidth = 1000;
            var pendingTimers = [];
            var handlers = {};
            var performance = { now: () => now };
            var Element = function Element() {};
            var surface = new Element();
            surface.closest = () => null;
            var stage = {
                addEventListener(name, callback) { handlers[name] = callback; },
                querySelector() { return null; },
                getBoundingClientRect() { return { left: 0, width: stageWidth }; }
            };
            var root = { dataset: { seekBackSeconds: "10", seekForwardSeconds: "30" } };
            var video = { hidden: false };
            var design = {
                seekSeconds() { return { back: 10, forward: 30 }; },
                dispatch(_root, action) { trace.push(action); }
            };
            var text = {};
            var presentation = { toggleFullscreen() { trace.push("fullscreen"); } };
            var togglePlay = () => trace.push("playPause");
            var chromeHidden = () => false;
            var show = () => trace.push("show");
            var hide = () => trace.push("hide");
            var settingsOpen = () => false;
            var setSettings = () => {};
            window.setTimeout = (fn, delay) => {
                pendingTimers.push({ fn, at: now + delay });
                return pendingTimers.length;
            };
            window.clearTimeout = () => {};

            function pointer(kind, x, at) {
                now = at;
                const event = {
                    pointerId: 1, pointerType: kind, button: 0, isPrimary: true,
                    clientX: x, clientY: 100, target: surface
                };
                handlers.pointerdown(event);
                now += 1;
                handlers.pointerup(event);
            }

            function advance(at) {
                now = at;
                const ready = pendingTimers.filter(timer => timer.at <= at);
                pendingTimers = pendingTimers.filter(timer => timer.at > at);
                for (const timer of ready) timer.fn();
            }
            """);
        engine.Execute(chrome[start..end]);

        var result = engine.Evaluate("""
            (() => {
                pointer("mouse", 500, 0);
                advance(400);
                pointer("mouse", 500, 1000);
                pointer("mouse", 505, 1150);
                advance(1600);
                pointer("touch", 100, 2000);
                pointer("touch", 100, 2150);
                advance(2500);
                pointer("touch", 900, 3000);
                pointer("touch", 900, 3150);
                advance(3500);
                pointer("touch", 500, 4000);
                pointer("touch", 500, 4150);
                advance(4500);
                pointer("touch", 500, 5000);
                advance(5400);
                stageWidth = 320;
                pointer("touch", 20, 6000);
                pointer("touch", 24, 6150);
                advance(6500);
                pointer("touch", 305, 7000);
                pointer("touch", 300, 7150);
                advance(7500);
                const beforeCancel = trace.length;
                now = 8000;
                const interrupted = {
                    pointerId: 2, pointerType: "touch", button: 0, isPrimary: true,
                    clientX: 80, clientY: 100, target: surface
                };
                handlers.pointerdown(interrupted);
                handlers.pointercancel();
                handlers.pointerup(interrupted);
                advance(8400);
                return [...trace, trace.length === beforeCancel ? "cancelOK" : "cancelFailed"].join("|");
            })()
            """).AsString();

        Assert.AreEqual(
            "playPause|fullscreen|seekBack10|seekForward10|fullscreen|hide|seekBack10|seekForward10|cancelOK",
            result);
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
