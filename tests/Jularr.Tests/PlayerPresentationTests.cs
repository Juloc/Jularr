using System.Text.RegularExpressions;
using Jint;

namespace Jularr.Tests;

/// <summary>
/// The presentation resolver of the shared web player (SPEC "iOS / iPadOS WebKit path"): Fullscreen resolves to element
/// fullscreen when the platform really offers it and otherwise to Jularr Theater, never to the native Apple player; the
/// system player and picture-in-picture are explicit, capability-driven and downgrade after a real failure. The scripts run
/// under Jint against simulated desktop and iPhone-like platforms.
/// </summary>
[TestClass]
public sealed class PlayerPresentationTests
{
    // A minimal platform: event targets, a document, a stage, a video and a history. `ios` removes element fullscreen and
    // adds WebKit's video surface; `desktop` offers element fullscreen and the standard picture-in-picture API.
    private const string Platform = """
        const target = () => {
            const listeners = {};
            return {
                addEventListener(name, listener) { (listeners[name] ||= []).push(listener); },
                emit(name, event = {}) { (listeners[name] || []).forEach(listener => listener(event)); }
            };
        };
        const build = (kind, tweak = () => {}) => {
            const calls = [];
            const win = Object.assign(target(), { scrollX: 0, scrollY: 0, scrollTo() { calls.push("scrollTo"); } });
            const doc = Object.assign(target(), { documentElement: { dataset: {} }, fullscreenElement: null });
            const stage = { dataset: {}, offsetHeight: 400, parentElement: { style: { minHeight: "" } } };
            const video = Object.assign(target(), { disablePictureInPicture: false });
            win.document = doc;
            win.console = { warn: message => calls.push("warn"), };
            win.history = {
                state: null,
                pushState(state) { this.state = state; calls.push("push"); },
                back() { this.state = null; calls.push("back"); win.emit("popstate"); }
            };

            if (kind === "desktop") {
                doc.fullscreenEnabled = true;
                doc.pictureInPictureEnabled = true;
                doc.pictureInPictureElement = null;
                stage.requestFullscreen = async () => { calls.push("requestFullscreen"); doc.fullscreenElement = stage; doc.emit("fullscreenchange"); };
                doc.exitFullscreen = async () => { calls.push("exitFullscreen"); doc.fullscreenElement = null; doc.emit("fullscreenchange"); };
                video.requestPictureInPicture = async () => { calls.push("requestPictureInPicture"); doc.pictureInPictureElement = video; video.emit("enterpictureinpicture"); };
                doc.exitPictureInPicture = async () => { calls.push("exitPictureInPicture"); doc.pictureInPictureElement = null; video.emit("leavepictureinpicture"); };
            } else {
                // iPhone WebKit: no element fullscreen, WebKit's own video fullscreen and presentation modes.
                doc.fullscreenEnabled = false;
                video.webkitPresentationMode = "inline";
                video.webkitSupportsPresentationMode = mode => mode === "picture-in-picture";
                video.webkitSetPresentationMode = mode => { calls.push("webkitSetPresentationMode:" + mode); video.webkitPresentationMode = mode; video.emit("webkitpresentationmodechanged"); };
                video.webkitEnterFullscreen = () => { calls.push("webkitEnterFullscreen"); video.webkitPresentationMode = "fullscreen"; video.webkitDisplayingFullscreen = true; video.emit("webkitbeginfullscreen"); };
                video.webkitExitFullscreen = () => { calls.push("webkitExitFullscreen"); video.webkitPresentationMode = "inline"; video.webkitDisplayingFullscreen = false; video.emit("webkitendfullscreen"); };
            }

            tweak({ win, doc, stage, video, calls });
            const updates = [];
            const capabilities = window.JularrPlaybackCapabilities.probePresentation(doc, stage, video);
            const presentation = window.JularrPlayerPresentation.create({ win, stage, video, capabilities, onUpdate: state => updates.push(state) });
            return { win, doc, stage, video, calls, updates, capabilities, presentation };
        };
        const rejecting = name => async () => { const error = new Error(name); error.name = name; throw error; };
        """;

    [TestMethod]
    public void TheCapabilityProbeReadsElementFullscreenNativeVideoAndPictureInPictureFromThePlatform()
    {
        var result = Run("""
            const desktop = build("desktop").capabilities;
            const ios = build("ios").capabilities;
            // (Join renders null as empty.) A bare requestFullscreen without the document's enabled flag and state is not proof.
            const bare = build("desktop", p => { p.doc.fullscreenEnabled = false; }).capabilities;
            return [
                desktop.elementFullscreen, desktop.nativeFullscreen, desktop.pictureInPicture.join("+"),
                ios.elementFullscreen, ios.nativeFullscreen, ios.pictureInPicture.join("+"),
                bare.elementFullscreen
            ].join(",");
            """);

        Assert.AreEqual("standard,false,standard,,true,webkit,", result);
    }

    [TestMethod]
    public void FullscreenUsesElementFullscreenWhereThePlatformOffersItAndNeverTheater()
    {
        var result = Run("""
            const { presentation, stage, doc, calls } = build("desktop");
            const out = [];
            return (async () => {
                await presentation.toggleFullscreen();
                out.push(presentation.state().mode, stage.dataset.presentation, presentation.state().immersive);
                await presentation.toggleFullscreen();
                out.push(presentation.state().mode, stage.dataset.presentation, presentation.state().immersive);
                out.push(calls.join("|"), doc.documentElement.dataset.playerTheater ?? "none");
                return out.join(",");
            })();
            """);

        Assert.AreEqual("element-fullscreen,element-fullscreen,true,inline,inline,false,requestFullscreen|exitFullscreen,none", result);
    }

    [TestMethod]
    public void FullscreenOnIPhoneResolvesToTheaterAndNeverToTheNativeApplePlayer()
    {
        var result = Run("""
            const { presentation, stage, doc, calls } = build("ios");
            const out = [];
            return (async () => {
                await presentation.toggleFullscreen();
                out.push(presentation.state().mode, stage.dataset.presentation, presentation.state().immersive);
                out.push(doc.documentElement.dataset.playerTheater, stage.parentElement.style.minHeight);
                await presentation.toggleFullscreen();
                out.push(presentation.state().mode, doc.documentElement.dataset.playerTheater ?? "none", stage.parentElement.style.minHeight === "" ? "restored" : "kept");
                out.push(calls.filter(call => call.startsWith("webkit")).length);
                return out.join(",");
            })();
            """);

        Assert.AreEqual("theater,theater,true,true,400px,inline,none,restored,0", result);
    }

    [TestMethod]
    public void TheaterLeavesThroughEscapeAndTheBackGestureButNotWhileAMenuHoldsTheKey()
    {
        var result = Run("""
            const { presentation, doc, win, calls } = build("ios");
            const out = [];
            return (async () => {
                await presentation.toggleFullscreen();
                doc.emit("keydown", { key: "Escape", defaultPrevented: true });
                out.push(presentation.state().mode);                      // a menu used the key
                doc.emit("keydown", { key: "Enter" });
                out.push(presentation.state().mode);
                doc.emit("keydown", { key: "Escape", defaultPrevented: false });
                out.push(presentation.state().mode, calls.filter(call => call === "back").length);   // history entry removed

                await presentation.toggleFullscreen();
                win.history.state = null;                                 // the user's own back gesture popped it
                win.emit("popstate");
                out.push(presentation.state().mode, calls.filter(call => call === "back").length);   // no second back
                return out.join(",");
            })();
            """);

        Assert.AreEqual("theater,theater,inline,1,inline,1", result);
    }

    [TestMethod]
    public void AFailedElementFullscreenRequestFallsBackToTheaterAndOnlyARealFailureDowngradesTheCapability()
    {
        var result = Run("""
            const out = [];
            return (async () => {
                const broken = build("desktop", p => { p.stage.requestFullscreen = async () => { p.calls.push("requestFullscreen"); throw Object.assign(new Error("x"), { name: "TypeError" }); }; });
                await broken.presentation.toggleFullscreen();
                out.push(broken.presentation.state().mode);
                await broken.presentation.toggleFullscreen();             // leave Theater
                await broken.presentation.toggleFullscreen();
                out.push(broken.calls.filter(call => call === "requestFullscreen").length);   // not asked again

                const gesture = build("desktop", p => { p.stage.requestFullscreen = async () => { p.calls.push("requestFullscreen"); throw Object.assign(new Error("x"), { name: "NotAllowedError" }); }; });
                await gesture.presentation.toggleFullscreen();
                await gesture.presentation.toggleFullscreen();
                await gesture.presentation.toggleFullscreen();
                out.push(gesture.presentation.state().mode, gesture.calls.filter(call => call === "requestFullscreen").length);   // asked again

                // A request that resolves without the stage becoming fullscreen is not a working capability either.
                const silent = build("desktop", p => { p.stage.requestFullscreen = async () => { p.calls.push("requestFullscreen"); }; });
                await silent.presentation.toggleFullscreen();
                out.push(silent.presentation.state().mode);
                return out.join(",");
            })();
            """);

        Assert.AreEqual("theater,1,theater,2,theater", result);
    }

    [TestMethod]
    public void TheSystemPlayerIsAnExplicitActionAndRoundTripsBackToTheJularrChrome()
    {
        var result = Run("""
            const { presentation, stage, video, calls, updates } = build("ios");
            const out = [];
            return (async () => {
                out.push(presentation.state().supports.nativeFullscreen);
                await presentation.toggleFullscreen();                    // Theater first
                presentation.toggleNativeFullscreen();
                out.push(presentation.state().mode, updates.at(-1).previousMode);
                video.webkitExitFullscreen();                             // the system player is closed
                out.push(presentation.state().mode, stage.dataset.presentation);
                presentation.toggleNativeFullscreen();
                presentation.toggleNativeFullscreen();                    // second press leaves it again
                out.push(presentation.state().mode, calls.filter(call => call === "webkitEnterFullscreen").length);

                const broken = build("ios", p => { p.video.webkitEnterFullscreen = () => { throw Object.assign(new Error("x"), { name: "NotSupportedError" }); }; });
                broken.presentation.toggleNativeFullscreen();
                out.push(broken.presentation.state().supports.nativeFullscreen);
                return out.join(",");
            })();
            """);

        Assert.AreEqual("true,native-fullscreen,theater,theater,theater,theater,2,false", result);
    }

    [TestMethod]
    public void PictureInPictureRoutesDowngradeAfterARealFailureAndTheControlGoesAway()
    {
        var result = Run("""
            const out = [];
            return (async () => {
                const desktop = build("desktop");
                await desktop.presentation.togglePictureInPicture();
                out.push(desktop.presentation.state().mode, desktop.presentation.state().supports.pictureInPicture);
                await desktop.presentation.togglePictureInPicture();
                out.push(desktop.presentation.state().mode);

                // Standard API reports support but rejects: the WebKit route is tried, then nothing is offered.
                const both = build("ios", p => {
                    p.doc.pictureInPictureEnabled = true;
                    p.video.requestPictureInPicture = rejecting("NotSupportedError");
                    p.video.webkitSetPresentationMode = mode => { p.calls.push("webkit:" + mode); throw Object.assign(new Error("x"), { name: "NotSupportedError" }); };
                });
                out.push(both.capabilities.pictureInPicture.join("+"));
                await both.presentation.togglePictureInPicture();
                out.push(both.presentation.state().supports.pictureInPicture, both.updates.at(-1).supports.pictureInPicture);
                await both.presentation.togglePictureInPicture();
                out.push(both.calls.filter(call => call.startsWith("webkit:")).length);   // never offered again

                // No gesture or metadata yet: try again later, keep the control.
                const early = build("desktop", p => { p.video.requestPictureInPicture = rejecting("InvalidStateError"); });
                await early.presentation.togglePictureInPicture();
                out.push(early.presentation.state().supports.pictureInPicture);
                return out.join(",");
            })();
            """);

        Assert.AreEqual("picture-in-picture,true,inline,standard+webkit,false,false,1,true", result);
    }

    [TestMethod]
    public void TheManualSeekIncrementsAreTenBackAndThirtyForwardAndMustBeDeclared()
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "player-design.js"));
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "player-gestures.js"));

        var result = engine.Evaluate("""
            const design = window.JularrPlayerDesign;
            const out = [];
            const steps = design.seekSeconds({ dataset: { seekBackSeconds: "10", seekForwardSeconds: "30" } });
            out.push(steps.back, steps.forward);
            for (const dataset of [{}, { seekBackSeconds: "10" }, { seekBackSeconds: "10", seekForwardSeconds: "0" }]) {
                try { design.seekSeconds({ dataset }); out.push("accepted"); } catch { out.push("rejected"); }
            }
            try { window.JularrPlayerGestures.createTapDecider({ stepSeconds: 10 }); out.push("accepted"); } catch { out.push("rejected"); }
            out.join(",");
            """).AsString();

        Assert.AreEqual("10,30,rejected,rejected,rejected,rejected", result);
    }

    [TestMethod]
    public void TheChromeAndThePageUseTheResolverAndNoSecondNativeFullscreenPath()
    {
        var chrome = Read("src", "Jularr.Web", "wwwroot", "js", "player-chrome.js");
        var presentation = Read("src", "Jularr.Web", "wwwroot", "js", "player-presentation.js");

        Assert.IsFalse(chrome.Contains("webkitEnterFullscreen", StringComparison.Ordinal), "The chrome never reaches for the native Apple player itself.");
        Assert.IsFalse(chrome.Contains("requestFullscreen", StringComparison.Ordinal), "Fullscreen is resolved by player-presentation.js only.");
        Assert.IsFalse(chrome.Contains("catch { /* not ready yet */ }", StringComparison.Ordinal), "A failed picture-in-picture call must downgrade, not vanish.");
        foreach (var file in new[] { chrome, presentation })
        {
            Assert.IsFalse(file.Contains("userAgent", StringComparison.Ordinal), "No UA sniffing in the presentation layer.");
            Assert.IsFalse(file.Contains("navigator.platform", StringComparison.Ordinal));
        }

        StringAssert.Contains(chrome, "window.JularrPlaybackCapabilities.probePresentation(");
        StringAssert.Contains(chrome, "window.JularrPlayerPresentation.create(");
        Assert.AreEqual(1, Regex.Matches(presentation, @"video\.webkitEnterFullscreen\(\)").Count);
        var nativeAction = presentation.IndexOf("const toggleNativeFullscreen", StringComparison.Ordinal);
        Assert.IsTrue(nativeAction > 0 && presentation.IndexOf("video.webkitEnterFullscreen()", StringComparison.Ordinal) > nativeAction);
        var fullscreenAction = presentation[presentation.IndexOf("const enterFullscreen", StringComparison.Ordinal)..presentation.IndexOf("const exitElementFullscreen", StringComparison.Ordinal)];
        StringAssert.Contains(fullscreenAction, "enterTheater();");
        Assert.IsFalse(fullscreenAction.Contains("webkitEnterFullscreen", StringComparison.Ordinal));

        foreach (var mode in new[] { "inline", "theater", "element-fullscreen", "native-fullscreen", "picture-in-picture" })
        {
            StringAssert.Contains(presentation, $"\"{mode}\"");
        }

        var page = EpisodePlayerSource.Read(PlayerControlsTests.RepositoryRoot());
        var order = new[] { "~/js/playback-capabilities.js", "~/js/player-presentation.js", "~/js/episode-player.js", "~/js/player-chrome.js" }
            .Select(script => page.IndexOf(script, StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(order.All(index => index > 0) && order.SequenceEqual(order.Order()), "The resolver loads after the capability probe and before its consumers.");
        Assert.IsTrue(Regex.IsMatch(page, "<video[^>]*playsinline[^>]*>"), "playsinline is on the element before playback starts.");
        StringAssert.Contains(page, "data-chrome-system-player");
        Assert.AreEqual(1, Regex.Matches(page, "<video[\\s>]").Count, "One media element.");
    }

    [TestMethod]
    public void TheaterStylesFillTheDynamicViewportWithSafeAreasAndRespectReducedMotion()
    {
        var css = Read("src", "Jularr.Web", "wwwroot", "css", "player.css");

        StringAssert.Contains(css, ".video-stage[data-presentation=\"theater\"]");
        StringAssert.Contains(css, "position: fixed; inset: 0;");
        StringAssert.Contains(css, "height: 100vh; height: 100dvh;");
        StringAssert.Contains(css, "html[data-player-theater]");
        StringAssert.Contains(css, "env(safe-area-inset-top, 0px)");
        StringAssert.Contains(css, "env(safe-area-inset-bottom, 0px)");
        Assert.IsFalse(css.Contains("is-fullscreen", StringComparison.Ordinal), "Presentation is one data-presentation attribute, not a second class.");
        var reduced = css[css.IndexOf("@media (prefers-reduced-motion: reduce) {\r\n    .video-stage[data-presentation", StringComparison.Ordinal)..];
        StringAssert.Contains(reduced[..reduced.IndexOf('}', StringComparison.Ordinal)], "animation: none");
    }

    private static string Run(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "playback-capabilities.js"));
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "player-presentation.js"));
        engine.Execute(Platform);
        return engine.Evaluate($"(() => {{ {script} }})()").UnwrapIfPromise().AsString();
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([PlayerControlsTests.RepositoryRoot(), .. parts]));
}
